using Dhole.Operations.Persistence.Cases;
using StackExchange.Redis;

namespace Dhole.Operations.Workers.Services;

/// <summary>
/// A dedicated Pricing Redis Streams Inbox. Failed events are NOT acknowledged;
/// their payload and Redis pending entry remain available for recovery.
/// Never consumes unrelated Pricing notifications or margin-approval events.
/// </summary>
public sealed class PricingQuoteAcceptedInboxConsumer(
    IServiceScopeFactory scopeFactory,
    IConnectionMultiplexer redis,
    IConfiguration configuration,
    ILogger<PricingQuoteAcceptedInboxConsumer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var stream = configuration["Redis:Streams:PricingAcceptedStream"]
            ?? "dhole.operations.pricing.v1";
        var group = configuration["Redis:Streams:PricingAcceptedGroup"]
            ?? "dhole-operations-accepted-quote";
        var consumer = configuration["Redis:Streams:PricingAcceptedConsumer"]
            ?? "operations-pricing-worker";
        var client = redis.GetDatabase();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                try
                {
                    await client.StreamCreateConsumerGroupAsync(
                        stream, group, "0-0", createStream: true);
                }
                catch (RedisServerException exception) when (
                    exception.Message.Contains("BUSYGROUP", StringComparison.OrdinalIgnoreCase)) { }

                // Process new messages even when a missing CRM account leaves an
                // earlier message pending. The stable consumer also replays own pending.
                var fresh = await client.StreamReadGroupAsync(stream, group, consumer,
                    ">", count: 25);
                var pending = await client.StreamReadGroupAsync(stream, group, consumer,
                    "0-0", count: 5);
                var messages = fresh.Concat(pending)
                    .GroupBy(x => x.Id).Select(x => x.First()).ToArray();

                foreach (var entry in messages)
                {
                    var name = Value(entry, "message_type", "type");
                    var id = Value(entry, "header:event_id", "header:message_id", "id");
                    var payload = Value(entry, "payload_json", "data");
                    if (name != "pricing.quote.accepted.v1" ||
                        !Guid.TryParse(id, out var eventId) ||
                        string.IsNullOrWhiteSpace(payload))
                    {
                        logger.LogError(
                            "Invalid Pricing acceptance frame at {EntryId}; not acknowledged",
                            entry.Id);
                        continue;
                    }
                    try
                    {
                        using var scope = scopeFactory.CreateScope();
                        var materializer = scope.ServiceProvider
                            .GetRequiredService<AcceptedQuoteCaseMaterializer>();
                        var idtra = await materializer.ApplyAsync(eventId, payload, stoppingToken);
                        await client.StreamAcknowledgeAsync(stream, group, entry.Id);
                        logger.LogInformation("Accepted quote Inbox event {EventId}: {Result}",
                            eventId, idtra);
                    }
                    catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                    {
                        logger.LogError(exception,
                            "Accepted quote {EventId} pending reconciliation; no Inbox ack and no orphan case",
                            eventId);
                    }
                }
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(exception, "Pricing acceptance Inbox cycle failed; retrying");
            }
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        }
    }

    private static string Value(StreamEntry entry, params string[] keys)
    {
        foreach (var key in keys)
        {
            var field = entry.Values.FirstOrDefault(x => x.Name == key);
            if (field.Name.HasValue) return field.Value.ToString();
        }
        return "";
    }
}
