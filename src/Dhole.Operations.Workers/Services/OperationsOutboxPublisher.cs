using Dhole.Operations.Persistence;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace Dhole.Operations.Workers.Services;

/// <summary>At-least-once publisher: a crash between XADD and DB commit is safe for idempotent consumers.</summary>
public sealed class OperationsOutboxPublisher(
    IServiceScopeFactory scopeFactory,
    IConnectionMultiplexer redis,
    IConfiguration config,
    ILogger<OperationsOutboxPublisher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var stream = config["Redis:Streams:Name"] ?? "dhole.operations.events";
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
                var entries = await db.OutboxMessages
                    .Where(x => x.PublishedAtUtc == null)
                    .OrderBy(x => x.OccurredAtUtc).Take(50).ToListAsync(stoppingToken);
                foreach (var entry in entries)
                {
                    try
                    {
                        await redis.GetDatabase().StreamAddAsync(stream, new NameValueEntry[]
                        {
                            new("id", entry.Id.ToString("D")),
                            new("type", entry.EventName),
                            new("data", entry.PayloadJson),
                            new("occurredAt", entry.OccurredAtUtc.ToString("O"))
                        });
                        entry.PublishedAtUtc = DateTimeOffset.UtcNow;
                        entry.LastError = null;
                    }
                    catch (Exception ex)
                    {
                        entry.RetryCount++;
                        entry.LastError = ex.Message;
                        logger.LogError(ex, "Publishing Operations event {EventId} failed", entry.Id);
                        break;
                    }
                }
                await db.SaveChangesAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Operations Outbox cycle failed");
            }
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
}
