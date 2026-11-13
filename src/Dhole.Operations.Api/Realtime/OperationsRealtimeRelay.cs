using Dhole.Operations.Api.Hubs;
using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;

namespace Dhole.Operations.Api.Realtime;

/// <summary>SignalR push is best-effort; durable replay is the authorized REST endpoint.</summary>
public sealed class OperationsRealtimeRelay(
    IConnectionMultiplexer redis,
    IHubContext<OperationsHub> hub,
    ILogger<OperationsRealtimeRelay> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var subscriber = redis.GetSubscriber();
        var channel = RedisChannel.Literal("dhole.operations.realtime");
        await subscriber.SubscribeAsync(channel, (_, message) =>
        {
            _ = hub.Clients.All.SendAsync("operationsChanged", message.ToString(), stoppingToken)
                .ContinueWith(t => logger.LogError(t.Exception, "Realtime relay failed"),
                    TaskContinuationOptions.OnlyOnFaulted);
        });
        try { await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { await subscriber.UnsubscribeAsync(channel); }
    }
}
