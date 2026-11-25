using System.Text.Json;
using Dhole.Operations.Api.Hubs;
using Dhole.Operations.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;

namespace Dhole.Operations.Api.Realtime;

/// <summary>Case and company-scoped dashboard invalidations; REST remains the authority for data.</summary>
public sealed class OperationsRealtimeRelay(
    IConnectionMultiplexer redis,
    IHubContext<OperationsHub> hub,
    IServiceScopeFactory scopes,
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

                // A dashboard cannot subscribe to a case that does not yet exist
                // or is not yet assigned. Resolve the persisted tenant rather
                // than trusting the stream payload, and send no case metadata.
                if (eventName?.StartsWith("operations.", StringComparison.Ordinal) != true) return;
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
                var company = await db.Cases.AsNoTracking().Where(c => c.Id == caseId)
                    .Select(c => (Guid?)c.CompanyId).SingleOrDefaultAsync(stoppingToken);
                await hub.Clients.Group("operations:dashboard:admin")
                    .SendAsync("operationsDashboardChanged", cancellationToken: stoppingToken);
                if (company.HasValue)
                    await hub.Clients.Group($"operations:dashboard:{company.Value:N}")
                        .SendAsync("operationsDashboardChanged", cancellationToken: stoppingToken);
            }
            catch (Exception ex) { logger.LogWarning(ex, "Operations realtime invalidation failed"); }
        });
        try { await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { await subscriber.UnsubscribeAsync(channel); }
    }
}
