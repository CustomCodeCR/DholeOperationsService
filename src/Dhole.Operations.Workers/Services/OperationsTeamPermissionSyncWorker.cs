using System.Net.Http.Json;
using Dhole.Operations.Persistence;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace Dhole.Operations.Workers.Services;

/// <summary>Durable membership invalidations plus periodic full reconciliation
/// backfill existing teams and recover even if an event was missed.</summary>
public sealed class OperationsTeamPermissionSyncWorker(
    IServiceScopeFactory scopeFactory, IConnectionMultiplexer redis, IConfiguration config,
    IHttpClientFactory clients, ILogger<OperationsTeamPermissionSyncWorker> logger) : BackgroundService
{
    private const string EventType = "operations.team.permissions.changed.v1";
    private const string Group = "dhole-operations-team-permission-sync";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var stream = config["Redis:Streams:Name"] ?? "dhole.operations.events";
        var consumer = config["Redis:TeamPermissions:ConsumerName"] ?? "operations-team-permission-worker";
        var database = redis.GetDatabase();
        var nextFullSync = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Initial and periodic reconciliation also repairs existing memberships.
                if (DateTimeOffset.UtcNow >= nextFullSync)
                {
                    await SyncAsync(stoppingToken);
                    nextFullSync = DateTimeOffset.UtcNow.AddSeconds(30);
                }

                try { await database.StreamCreateConsumerGroupAsync(stream, Group, "$", createStream: true); }
                catch (RedisServerException e) when (e.Message.Contains("BUSYGROUP", StringComparison.OrdinalIgnoreCase)) { }
                var pending = await database.StreamReadGroupAsync(stream, Group, consumer, "0-0", count: 100);
                var fresh = await database.StreamReadGroupAsync(stream, Group, consumer, ">", count: 100);
                var entries = pending.Concat(fresh).DistinctBy(e => e.Id).ToArray();
                if (entries.Any(e => e.Values.Any(v => v.Name == "type" && v.Value == EventType)))
                {
                    await SyncAsync(stoppingToken);
                    nextFullSync = DateTimeOffset.UtcNow.AddSeconds(30);
                }

                // Acknowledge only after Auth has committed the snapshot and security outbox.
                foreach (var entry in entries)
                    await database.StreamAcknowledgeAsync(stream, Group, entry.Id);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Operations team permission sync failed; will retry"); }
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        }
    }

    private async Task SyncAsync(CancellationToken ct)
    {
        var key = config["Auth:OperationsPool:InternalServiceKey"];
        if (!Uri.TryCreate(config["Auth:OperationsPool:BaseAddress"], UriKind.Absolute, out var address) ||
            address.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(address.UserInfo) ||
            string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Internal Auth permission sync is not configured.");

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Serialize snapshots across worker replicas. PostgreSQL transaction IDs
        // are monotonic and persisted, so Auth can discard stale retries.
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(839162097426);", ct);
        var revision = (await db.Database.SqlQueryRaw<long>(
            "SELECT txid_current()::bigint AS \"Value\"").ToListAsync(ct)).Single();
        var members = await db.TeamMembers.AsNoTracking().Where(m => m.Team.IsActive &&
            (m.Team.DepartmentCode == "APERTURAS" || m.Team.DepartmentCode == "COORDINACION_ORIGEN" ||
             m.Team.DepartmentCode == "DOCUMENTACION" || m.Team.DepartmentCode == "TRANSITO" ||
             m.Team.DepartmentCode == "PANAMA" || m.Team.DepartmentCode == "COSTA_RICA"))
            .Select(m => new { m.UserId, m.Team.DepartmentCode, m.IsSupervisor, m.RoleCode, TeamName = m.Team.Name })
            .Distinct().ToArrayAsync(ct);

        // Workload availability is deliberately independent of permissions.
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(
            new Uri(address.ToString().TrimEnd('/') + "/"), "api/internal/auth/operations-team-permissions"));
        request.Headers.TryAddWithoutValidation("X-Dhole-Service-Key", key);
        request.Content = JsonContent.Create(new { revision, members });
        using var response = await clients.CreateClient("auth-operations-team-permissions")
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await tx.CommitAsync(ct);
    }
}
