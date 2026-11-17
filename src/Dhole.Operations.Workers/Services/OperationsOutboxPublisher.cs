using CustomCodeFramework.Messaging.Outbox;
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
                    .Where(x => x.Status == OutboxMessageStatus.Pending)
                    .OrderBy(x => x.CreatedAtUtc).Take(50).ToListAsync(stoppingToken);
                foreach (var entry in entries)
                {
                    try
                    {
                        await redis.GetDatabase().StreamAddAsync(stream, new NameValueEntry[]
                        {
                            new("id", entry.EventId.ToString("D")),
                            new("type", entry.EventName),
                            new("data", entry.PayloadJson),
                            new("occurredAt", entry.CreatedAtUtc.ToString("O"))
                        });
                        entry.Status = OutboxMessageStatus.Processed;
                        entry.ProcessedAtUtc = DateTime.UtcNow;
                        entry.ErrorMessage = null;
                    }
                    catch (Exception ex)
                    {
                        entry.RetryCount++;
                        entry.ErrorMessage = ex.Message;
                        logger.LogError(ex, "Publishing Operations event {EventId} failed", entry.EventId);
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
