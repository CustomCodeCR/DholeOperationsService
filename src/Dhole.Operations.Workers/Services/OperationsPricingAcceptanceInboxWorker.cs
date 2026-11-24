using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dhole.Operations.Application.Cases;
using Dhole.Operations.Persistence;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace Dhole.Operations.Workers.Services;

/// <summary>
/// Consume only definitively accepted Pricing QUOs from their own durable
/// stream. Operations persists Inbox + unique IDTRA + commercial snapshot in
/// one PostgreSQL transaction, then acknowledges Redis. Never fabricate CRM
/// accounts, executives, company IDs, commercial options or client acceptances.
/// </summary>
public sealed class OperationsPricingAcceptanceInboxWorker(
    IServiceScopeFactory scopes,
    IHttpClientFactory clients,
    IConnectionMultiplexer redis,
    IConfiguration config,
    ILogger<OperationsPricingAcceptanceInboxWorker> logger) : BackgroundService
{
    private const string EventName = "pricing.quote.accepted.v1";
    private sealed record CrmAcceptedAccount(
        Guid AccountId, string AccountName, Guid ExecutiveUserId,
        string? ExecutiveName, bool InstitutionalExecutive);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var stream = config["Redis:PricingAcceptance:StreamName"] ?? "dhole.pricing.events";
        var group = config["Redis:PricingAcceptance:ConsumerGroup"] ??
            "dhole-operations-pricing-acceptance";
        var consumer = config["Redis:PricingAcceptance:ConsumerName"] ??
            "operations-pricing-acceptance";
        var dbRedis = redis.GetDatabase();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                try
                {
                    await dbRedis.StreamCreateConsumerGroupAsync(
                        stream, group, "0-0", createStream: true);
                }
                catch (RedisServerException exception) when (
                    exception.Message.Contains("BUSYGROUP", StringComparison.OrdinalIgnoreCase)) { }

                // Existing pending events AND new events are read on every cycle,
                // so one malformed older payload cannot block newer work.
                var pending = await dbRedis.StreamReadGroupAsync(
                    stream, group, consumer, "0-0", count:20);
                var fresh = await dbRedis.StreamReadGroupAsync(
                    stream, group, consumer, ">", count:20);
                foreach (var entry in pending.Concat(fresh).DistinctBy(x => x.Id))
                {
                    var type = Field(entry, "message_type");
                    if (string.IsNullOrWhiteSpace(type)) type = Field(entry, "type");
                    if (type != EventName)
                    {
                        await dbRedis.StreamAcknowledgeAsync(stream, group, entry.Id);
                        continue;
                    }

                    if (Field(entry, "header:source_service") != "DholePricingService")
                    {
                        logger.LogError(
                            "Unexpected source for Pricing acceptance at {RedisEntry}",
                            entry.Id);
                        // Do not acknowledge a forged or malformed commercial
                        // acceptance. Verified Pricing must re-publish it.
                        continue;
                    }
                    var eventIdText = Field(entry, "header:event_id");
                    if (string.IsNullOrWhiteSpace(eventIdText))
                        eventIdText = Field(entry, "header:message_id");
                    if (string.IsNullOrWhiteSpace(eventIdText))
                        eventIdText = Field(entry, "id");
                    if (!Guid.TryParse(eventIdText, out var eventId) ||
                        eventId == Guid.Empty)
                    {
                        logger.LogError("Pricing stream acceptance lacks a durable event ID at {RedisEntry}",
                            entry.Id);
                        // Retain pending for a repaired producer; never create
                        // an operation with a newly fabricated random event ID.
                        continue;
                    }
                    var payload = Field(entry, "payload_json");
                    if (string.IsNullOrWhiteSpace(payload)) payload = Field(entry, "data");
                    try
                    {
                        await ConsumeAcceptanceAsync(eventId, payload, stoppingToken);
                        await dbRedis.StreamAcknowledgeAsync(stream, group, entry.Id);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception error)
                    {
                        logger.LogError(error,
                            "Accepted Pricing event {EventId} not committed. Pending Redis message retained",
                            eventId);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                logger.LogError(error, "Pricing acceptance Inbox poll failed; retrying pending events");
            }
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private static string Field(StreamEntry entry, string key) =>
        entry.Values.FirstOrDefault(x => x.Name == key).Value.ToString() ?? "";

    private async Task ConsumeAcceptanceAsync(Guid eventId, string payload, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(payload) || payload.Length > 1_000_000)
            throw new InvalidOperationException("Invalid Pricing accepted quote payload.");
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Pricing event is not an object.");
        var quoteId = ReadGuid(root, "quoteId");
        var optionId = ReadGuid(root, "acceptedOptionId");
        var accountId = ReadGuid(root, "crmAccountId");
        var clientName = ReadString(root, "clientName");
        var actorId = ReadGuid(root, "acceptedByUserId");
        var version = ReadInt(root, "commercialRevision");
        var quoteNumber = ReadString(root, "quoteNumber");
        var mode = ReadString(root, "shipmentMode");
        var acceptedAt = ReadString(root, "acceptedAtUtc");
        if (quoteId == Guid.Empty || optionId == Guid.Empty ||
            ReadGuid(root, "rateId") != optionId ||
            (accountId == Guid.Empty && string.IsNullOrWhiteSpace(clientName)) ||
            actorId == Guid.Empty || version <= 0 ||
            string.IsNullOrWhiteSpace(quoteNumber) || string.IsNullOrWhiteSpace(mode) ||
            !DateTimeOffset.TryParse(acceptedAt,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal,
                out var acceptedAtUtc))
            throw new InvalidOperationException(
                "Definitive quote acceptance is missing client identity, option, actor or revision.");

        // Prefer the immutable CRM account GUID from Pricing. For historical
        // name-only quotes, ask CRM for a unique exact active account instead
        // of fabricating IDs or silently dropping the accepted quote.
        var account = await ResolveCrmAccountAsync(accountId, clientName, actorId, ct);
        if (account.AccountId == Guid.Empty ||
            (accountId != Guid.Empty && account.AccountId != accountId) ||
            account.ExecutiveUserId == Guid.Empty)
            throw new InvalidOperationException("CRM account identity mismatch.");
        accountId = account.AccountId;

        using var scope = scopes.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
        var knownCompanies = await operations.Teams.AsNoTracking()
            .Select(t => t.CompanyId)
            .Union(operations.Cases.AsNoTracking().Select(c => c.CompanyId))
            .Distinct().Take(3).ToArrayAsync(ct);

        var configured = config["PricingAcceptance:CompanyId"];
        Guid companyId;
        if (Guid.TryParse(configured, out var explicitCompany) &&
            explicitCompany != Guid.Empty &&
            (knownCompanies.Length == 0 || knownCompanies.Contains(explicitCompany)))
            companyId = explicitCompany;
        else if (string.IsNullOrWhiteSpace(configured) &&
            knownCompanies.Length == 1 && knownCompanies[0] != Guid.Empty)
            companyId = knownCompanies[0];
        else
            throw new InvalidOperationException(
                "Pricing acceptance cannot resolve a unique verified Operations company.");

        var store = scope.ServiceProvider.GetRequiredService<IOperationsCaseStore>();
        var result = await store.CreateAcceptedQuoteAsync(new CreateAcceptedQuoteCaseCommand(
            companyId, accountId, account.ExecutiveUserId, actorId,
            quoteId, optionId, quoteNumber, version, acceptedAtUtc,
            eventId, payload, mode), ct);
        logger.LogInformation("Accepted quotation {QuoteId} linked to {Idtra}, duplicate={Duplicate}",
            quoteId, result.Idtra, result.Existing);
    }

    private async Task<CrmAcceptedAccount> ResolveCrmAccountAsync(
        Guid accountId, string clientName, Guid acceptedBy, CancellationToken ct)
    {
        var baseAddress = config["Crm:OperationsAcceptance:BaseAddress"];
        var serviceKey = config["Crm:OperationsAcceptance:InternalServiceKey"];
        if (!Uri.TryCreate(baseAddress, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme is not ("http" or "https") ||
            !string.IsNullOrWhiteSpace(baseUri.UserInfo) ||
            string.IsNullOrWhiteSpace(serviceKey))
            throw new InvalidOperationException(
                "CRM Operations acceptance contract is not configured; no IDTRA created.");

        var resource = accountId != Guid.Empty
            ? $"api/internal/crm/operations/accounts/{accountId:D}"
            : "api/internal/crm/operations/accounts/by-name?name=" +
                Uri.EscapeDataString(clientName.Trim());
        using var request = new HttpRequestMessage(HttpMethod.Get,
            new Uri(new Uri(baseUri.ToString().TrimEnd('/') + "/"), resource));
        request.Headers.TryAddWithoutValidation("X-Dhole-Service-Key", serviceKey);
        request.Headers.TryAddWithoutValidation("X-Dhole-Accepted-By", acceptedBy.ToString("D"));
        using var response = await clients.CreateClient("crm-pricing-acceptance")
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict)
            throw new InvalidOperationException("CRM account or executive needs correction; " +
                "name-only quotes require a unique active CRM account.");
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("CRM acceptance identity could not be verified.");

        var account = await response.Content.ReadFromJsonAsync<CrmAcceptedAccount>(
            cancellationToken: ct);
        return account is not null && account.AccountId != Guid.Empty &&
               (accountId == Guid.Empty || account.AccountId == accountId)
            ? account : throw new InvalidOperationException("CRM returned an invalid account.");
    }

    private static Guid ReadGuid(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        Guid.TryParse(value.GetString(), out var guid)
            ? guid : Guid.Empty;
    private static string ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";
    private static int ReadInt(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var version) ? version : 0;
}
