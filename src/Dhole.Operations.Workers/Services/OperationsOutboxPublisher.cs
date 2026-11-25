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
                        if (entry.EventName == "audit.event.registered")
                        {
                            // CustomCodeFramework.Redis.Streams wire format consumed by DholeAuditLogs.
                            await redis.GetDatabase().StreamAddAsync(
                                config["Redis:Streams:AuditStreamName"] ?? "dhole.audit.events",
                                new NameValueEntry[] {
                                    new("message_type", "audit.event.registered"),
                                    new("payload_json", entry.PayloadJson),
                                    new("header:source_service", "DholeOperationsService"),
                                    new("header:message_id", entry.EventId.ToString("D"))
                                });
                        }
                        else if (entry.EventName == Dhole.Operations.Persistence.Messaging.OperationsOutboxFactory.NotificationRequested)
                        {
                            await redis.GetDatabase().StreamAddAsync(
                                config["Redis:Streams:NotificationsStreamName"] ?? "dhole.notifications.events",
                                new NameValueEntry[] {
                                    new("message_type", entry.EventName),
                                    new("payload_json", entry.PayloadJson),
                                    new("header:source_service", "DholeOperationsService"),
                                    new("header:message_id", entry.EventId.ToString("D")),
                                    new("header:event_id", entry.EventId.ToString("D"))
                                });
                        }
                        else if (entry.EventName == "operations.pricing-review.requested.v1")
                        {
                            // The Pricing worker consumes framework-style stream
                            // envelopes, not Operations' realtime envelope.
                            await redis.GetDatabase().StreamAddAsync(
                                config["Redis:PricingAcceptance:StreamName"] ?? "dhole.pricing.events",
                                new NameValueEntry[] {
                                    new("message_type", entry.EventName),
                                    new("payload_json", entry.PayloadJson),
                                    new("header:source_service", "DholeOperationsService"),
                                    new("header:event_id", entry.EventId.ToString("D"))
                                });
                        }
                        else
                        {
                            await redis.GetDatabase().StreamAddAsync(stream, new NameValueEntry[]
                            {
                                new("id", entry.EventId.ToString("D")),
                                new("type", entry.EventName),
                                new("data", entry.PayloadJson),
                                new("occurredAt", entry.CreatedAtUtc.ToString("O"))
                            });
                        }
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
