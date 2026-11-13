using Dhole.Operations.Domain.Models;
using Dhole.Operations.Persistence;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace Dhole.Operations.Workers.Services;

/// <summary>Consumer-group recovery, Postgres deduplication, replay and realtime fanout.</summary>
public sealed class OperationsInboxConsumer(
    IServiceScopeFactory scopeFactory,
    IConnectionMultiplexer redis,
    IConfiguration config,
    ILogger<OperationsInboxConsumer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var stream = config["Redis:Streams:Name"] ?? "dhole.operations.events";
        var group = config["Redis:Streams:ConsumerGroup"] ?? "dhole-operations";
        var consumer = $"{config["Redis:Streams:ConsumerName"] ?? "operations-worker"}-{Environment.MachineName}";
        var database = redis.GetDatabase();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                try { await database.StreamCreateConsumerGroupAsync(stream, group, "0-0", createStream: true); }
                catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP", StringComparison.OrdinalIgnoreCase)) { }
                var entries = await database.StreamReadGroupAsync(stream, group, consumer, "0-0", count: 50);
                if (entries.Length == 0)
                    entries = await database.StreamReadGroupAsync(stream, group, consumer, ">", count: 50);
                foreach (var entry in entries)
                {
                    var idText = entry.Values.FirstOrDefault(x => x.Name == "id").Value.ToString();
                    if (!Guid.TryParse(idText, out var eventId))
                    {
                        logger.LogWarning("Skipping malformed Operations event in stream {Stream} at {EntryId}; retained pending", stream, entry.Id);
                        continue;
                    }
                    using var scope = scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
                    if (!await db.InboxReceipts.AnyAsync(x =>
                        x.ConsumerName == "operations-realtime" && x.EventId == eventId, stoppingToken))
                    {
                        var type = entry.Values.FirstOrDefault(x => x.Name == "type").Value.ToString() ?? "";
                        var payload = entry.Values.FirstOrDefault(x => x.Name == "data").Value.ToString() ?? "{}";
                        db.InboxReceipts.Add(new InboxReceipt
                        {
                            ConsumerName = "operations-realtime", EventId = eventId, EventName = type,
                            PayloadJson = payload, ProcessedAtUtc = DateTimeOffset.UtcNow
                        });
                        await db.SaveChangesAsync(stoppingToken);
                        await redis.GetSubscriber().PublishAsync(RedisChannel.Literal("dhole.operations.realtime"),
                            System.Text.Json.JsonSerializer.Serialize(new { eventId, type, payload }));
                    }
                    await database.StreamAcknowledgeAsync(stream, group, entry.Id);
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Operations Inbox cycle failed; consumer will retry pending entries");
            }
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }
}
