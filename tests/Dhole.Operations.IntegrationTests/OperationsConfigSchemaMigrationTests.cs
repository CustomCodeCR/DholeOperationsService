using CustomCodeFramework.Messaging.Outbox;
using Dhole.Operations.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Dhole.Operations.IntegrationTests;

/// <summary>Proves upgrades from DH-185 preserve data and recreate Config-style messaging.</summary>
[TestClass]
public sealed class OperationsConfigSchemaMigrationTests
{
    [TestMethod]
    public async Task ExistingLegacyRowsAreMovedNotCopiedOrLost()
    {
        var ciConnection = Environment.GetEnvironmentVariable("DHOLE_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(ciConnection))
            Assert.Inconclusive("DHOLE_TEST_POSTGRES is required.");
        var dbName = "ops_dh186_" + Guid.NewGuid().ToString("N")[..12];
        var source = new NpgsqlConnectionStringBuilder(ciConnection!);
        var adminBuilder = new NpgsqlConnectionStringBuilder(source.ConnectionString) { Database = "postgres" };
        var isolatedBuilder = new NpgsqlConnectionStringBuilder(source.ConnectionString) { Database = dbName };
        await using var admin = new NpgsqlConnection(adminBuilder.ConnectionString);
        await admin.OpenAsync();
        await using (var cmd = new NpgsqlCommand($"CREATE DATABASE {dbName}", admin))
            await cmd.ExecuteNonQueryAsync();

        try
        {
            var options = new DbContextOptionsBuilder<OperationsDbContext>()
                .UseNpgsql(isolatedBuilder.ConnectionString).Options;
            var pendingId = Guid.NewGuid();
            var publishedId = Guid.NewGuid();
            var receivedId = Guid.NewGuid();
            var teamId = Guid.NewGuid();

            await using (var legacy = new OperationsDbContext(options))
            {
                var available = legacy.Database.GetMigrations().ToArray();
                Assert.IsTrue(available.Any(x => x.EndsWith("_OperationsFirstTables", StringComparison.Ordinal)),
                    "Cannot find legacy migration: " + string.Join(", ", available));
                await legacy.Database.GetService<IMigrator>()
                    .MigrateAsync("OperationsFirstTables");
                await using var connection = new NpgsqlConnection(isolatedBuilder.ConnectionString);
                await connection.OpenAsync();
                await using (var cmd = new NpgsqlCommand("""
                    INSERT INTO public.operations_outbox
                      ("Id","EventName","PayloadJson","OccurredAtUtc","PublishedAtUtc","RetryCount","LastError")
                    VALUES (@pending,'operations.changed','{"caseId":"pending"}',now(),NULL,0,NULL),
                           (@published,'operations.changed','{"caseId":"published"}',now(),now(),1,NULL);
                    INSERT INTO public.operations_inbox
                      ("ConsumerName","EventId","EventName","PayloadJson","ProcessedAtUtc")
                    VALUES ('operations-realtime',@received,'operations.changed','{"caseId":"received"}',now());
                    INSERT INTO public.operations_teams(id,company_id,department_code,name,is_active)
                    VALUES (@team,@company,'APERTURAS','Legacy Pool',true);
                    """, connection))
                {
                    cmd.Parameters.AddWithValue("pending", pendingId);
                    cmd.Parameters.AddWithValue("published", publishedId);
                    cmd.Parameters.AddWithValue("received", receivedId);
                    cmd.Parameters.AddWithValue("team", teamId);
                    cmd.Parameters.AddWithValue("company", Guid.NewGuid());
                    await cmd.ExecuteNonQueryAsync();
                }
                await legacy.Database.MigrateAsync();
                await legacy.Database.MigrateAsync(); // idempotent
            }

            await using (var verified = new OperationsDbContext(options))
            {
                var pending = await verified.OutboxMessages.SingleAsync(x => x.EventId == pendingId);
                var published = await verified.OutboxMessages.SingleAsync(x => x.EventId == publishedId);
                Assert.AreEqual(pendingId, pending.Id);
                Assert.AreEqual(OutboxMessageStatus.Pending, pending.Status);
                Assert.AreEqual(OutboxMessageStatus.Processed, published.Status);
                Assert.AreEqual(1, published.RetryCount);
                Assert.AreEqual(teamId, (await verified.Teams.SingleAsync(x => x.Id == teamId)).Id);
                var receipt = await verified.InboxMessages.SingleAsync(x => x.EventId == receivedId);
                Assert.AreEqual("operations-realtime", receipt.ConsumerService);
                Assert.AreEqual("operations.changed", receipt.EventName);
                var payload = await verified.InboxMessages.Where(x => x.EventId == receivedId)
                    .Select(x => EF.Property<string>(x, "PayloadJson")).SingleAsync();
                Assert.AreEqual("{\"caseId\":\"received\"}", payload);

                await using var sql = new NpgsqlConnection(isolatedBuilder.ConnectionString);
                await sql.OpenAsync();
                await using var assertSql = new NpgsqlCommand("""
                    SELECT (SELECT count(*) FROM information_schema.tables
                              WHERE table_schema='operations' AND table_name IN
                              ('outbox_messages','inbox_messages','operations_cases')) = 3
                           AND (SELECT count(*) FROM information_schema.tables
                              WHERE table_schema='public' AND table_name LIKE 'operations_%') = 0
                           AND (SELECT count(*) FROM operations.outbox_messages) = 2
                           AND (SELECT count(*) FROM operations.inbox_messages) = 1;
                    """, sql);
                Assert.IsTrue((bool)(await assertSql.ExecuteScalarAsync())!);
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
