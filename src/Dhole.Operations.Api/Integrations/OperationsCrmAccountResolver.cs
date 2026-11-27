using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dhole.Operations.Api.Integrations;

/// <summary>CRM owns account/executive identity. Never trust a creator-provided sales executive.</summary>
public sealed class OperationsCrmAccountResolver(IHttpClientFactory factory, IConfiguration configuration)
{
    public sealed record Resolution(Guid? ExecutiveUserId, string? ErrorCode, int StatusCode);

    public async Task<Resolution> ResolveAsync(Guid accountId, string authorization, CancellationToken ct)
    {
        var configured = configuration["Crm:ApiBaseUrl"];
        if (!Uri.TryCreate(configured, UriKind.Absolute, out var baseUrl) ||
            (baseUrl.Scheme != Uri.UriSchemeHttp && baseUrl.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(baseUrl.UserInfo))
            return new(null, "CRM_NOT_CONFIGURED", 503);
        if (!AuthenticationHeaderValue.TryParse(authorization, out var bearer) ||
            !string.Equals(bearer.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase))
            return new(null, "AUTHORIZATION_REQUIRED", 401);

        var url = new Uri(new Uri(baseUrl.ToString().TrimEnd('/') + "/"),
            $"api/crm/operations/accounts/{accountId:D}");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = bearer;
        try
        {
            using var response = await factory.CreateClient("crm-operations-contacts")
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.Forbidden) return new(null, "CRM_ACCESS_DENIED", 403);
            if (response.StatusCode == HttpStatusCode.Unauthorized) return new(null, "CRM_ACCESS_DENIED", 401);
            if (response.StatusCode == HttpStatusCode.NotFound) return new(null, "CRM_ACCOUNT_NOT_FOUND", 404);
            if (!response.IsSuccessStatusCode) return new(null, "CRM_UNAVAILABLE", 503);

            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct),
                cancellationToken: ct);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new(null, "CRM_INVALID_CONTRACT", 503);
            Guid executiveId = Guid.Empty;
            foreach (var property in root.EnumerateObject())
                if (string.Equals(property.Name, "executiveUserId", StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.String)
                    Guid.TryParse(property.Value.GetString(), out executiveId);
            // No fallback to creator or fictitious staff, even for an institutional account.
            if (executiveId == Guid.Empty) return new(null, "CRM_EXECUTIVE_UNASSIGNED", 409);
            return new(executiveId, null, 200);
        }
        catch (HttpRequestException) { return new(null, "CRM_UNAVAILABLE", 503); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(null, "CRM_TIMEOUT", 503);
        }
        catch (JsonException) { return new(null, "CRM_INVALID_CONTRACT", 503); }
    }
    public sealed record DivisionResolution(string? ErrorCode, int StatusCode);

    // Only existing case callers may supply a trusted actor; creation uses the user's scoped CRM token.
    public async Task<DivisionResolution> ValidateDivisionAsync(Guid accountId, JsonObject data,
        string authorization, Guid? trustedActor, CancellationToken ct)
    {
        if (data["crmDivisionId"] is null ||
            (data["crmDivisionId"] is JsonValue empty && empty.TryGetValue<string>(out var raw) && string.IsNullOrWhiteSpace(raw)))
        {
            data.Remove("crmDivisionName");
            return new(null, 200);
        }
        if (data["crmDivisionId"] is not JsonValue value || !value.TryGetValue<string>(out var id) ||
            !Guid.TryParse(id, out var division) || division == Guid.Empty)
            return new("CRM_DIVISION_INVALID", 400);
        var configured = configuration["Crm:ApiBaseUrl"];
        if (!Uri.TryCreate(configured, UriKind.Absolute, out var baseUrl) ||
            (baseUrl.Scheme != Uri.UriSchemeHttp && baseUrl.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(baseUrl.UserInfo)) return new("CRM_NOT_CONFIGURED", 503);
        var path = trustedActor.HasValue ? "api/internal/crm/operations/accounts/" : "api/crm/operations/accounts/";
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(baseUrl.ToString().TrimEnd('/') + "/"),
            $"{path}{accountId:D}/contacts?divisionId={division:D}&includeDirectory=true"));
        if (trustedActor.HasValue)
        {
            var key = configuration["Crm:OperationsAcceptance:InternalServiceKey"];
            if (string.IsNullOrWhiteSpace(key)) return new("CRM_NOT_CONFIGURED", 503);
            request.Headers.TryAddWithoutValidation("X-Dhole-Service-Key", key);
            request.Headers.TryAddWithoutValidation("X-Dhole-Operations-Actor", trustedActor.Value.ToString("D"));
        }
        else
        {
            if (!AuthenticationHeaderValue.TryParse(authorization, out var bearer) ||
                !string.Equals(bearer.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase))
                return new("AUTHORIZATION_REQUIRED", 401);
            request.Headers.Authorization = bearer;
        }
        try
        {
            using var response = await factory.CreateClient("crm-operations-contacts")
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return new("CRM_DIVISION_NOT_IN_ACCOUNT", 400);
            if (response.StatusCode == HttpStatusCode.Forbidden) return new("CRM_ACCESS_DENIED", 403);
            if (response.StatusCode == HttpStatusCode.Unauthorized) return new("CRM_ACCESS_DENIED", 401);
            if (!response.IsSuccessStatusCode) return new("CRM_UNAVAILABLE", 503);
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("divisions", out var divisions) ||
                divisions.ValueKind != JsonValueKind.Array || !root.TryGetProperty("contacts", out var contacts) ||
                contacts.ValueKind != JsonValueKind.Array) return new("CRM_INVALID_CONTRACT", 503);
            string? divisionName = null;
            foreach (var row in divisions.EnumerateArray())
                if (row.TryGetProperty("id", out var candidate) && candidate.ValueKind == JsonValueKind.String &&
                    candidate.TryGetGuid(out var parsed) && parsed == division &&
                    row.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                    divisionName = name.GetString();
            if (string.IsNullOrWhiteSpace(divisionName)) return new("CRM_DIVISION_NOT_IN_ACCOUNT", 400);
            var allowed = new HashSet<Guid>();
            foreach (var row in contacts.EnumerateArray())
                if (row.TryGetProperty("id", out var contact) && contact.ValueKind == JsonValueKind.String &&
                    contact.TryGetGuid(out var parsed)) allowed.Add(parsed);
            if (data["contactIds"] is not null)
            {
                if (data["contactIds"] is not JsonArray selected) return new("CRM_CONTACTS_INVALID", 400);
                foreach (var item in selected)
                    if (item is not JsonValue contact || !contact.TryGetValue<string>(out var selectedId) ||
                        !Guid.TryParse(selectedId, out var parsed) || !allowed.Contains(parsed))
                        return new("CRM_CONTACT_NOT_IN_DIVISION", 400);
            }
            data["crmDivisionId"] = division.ToString("D");
            data["crmDivisionName"] = divisionName;
            return new(null, 200);
        }
        catch (HttpRequestException) { return new("CRM_UNAVAILABLE", 503); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return new("CRM_TIMEOUT", 503); }
        catch (JsonException) { return new("CRM_INVALID_CONTRACT", 503); }
        catch (InvalidOperationException) { return new("CRM_INVALID_CONTRACT", 503); }
    }
}
