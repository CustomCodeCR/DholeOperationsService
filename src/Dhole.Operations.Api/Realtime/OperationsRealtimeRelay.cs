using System.Text.Json;
using Dhole.Operations.Api.Hubs;
using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;

namespace Dhole.Operations.Api.Realtime;

/// <summary>Only case-scoped invalidations are pushed; clients refetch authorized data over REST.</summary>
public sealed class OperationsRealtimeRelay(
    IConnectionMultiplexer redis,
    IHubContext<OperationsHub> hub,
    ILogger<OperationsRealtimeRelay> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var subscriber = redis.GetSubscriber();
        var channel = RedisChannel.Literal("dhole.operations.realtime");
        await subscriber.SubscribeAsync(channel, async (_, message) =>
        {
            try
            {
                using var root = JsonDocument.Parse(message.ToString());
                var obj = root.RootElement;
                if (!obj.TryGetProperty("payload", out var payload) ||
                    payload.ValueKind != JsonValueKind.String) return;
                using var inner = JsonDocument.Parse(payload.GetString()!);
                if (!inner.RootElement.TryGetProperty("caseId", out var id) ||
                    !Guid.TryParse(id.GetString(), out var caseId)) return;
                var eventId = obj.TryGetProperty("eventId", out var eid) ? eid.GetString() : null;
                var eventName = obj.TryGetProperty("type", out var type) ? type.GetString() : null;
                await hub.Clients.Group($"operations:case:{caseId:N}")
                    .SendAsync("operationsChanged", new { eventId, eventName, caseId }, stoppingToken);
            }
            catch (Exception ex) { logger.LogWarning(ex, "Operations realtime invalidation failed"); }
        });
        try { await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { await subscriber.UnsubscribeAsync(channel); }
    }
}
