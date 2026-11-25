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
            var originTeamId = Guid.NewGuid();

            await using (var legacy = new OperationsDbContext(options))
            {
                // Build a minimal *legacy* public schema and migration history.
                // The historic IDs have 12-digit prefixes; EF's target migration
                // name parser cannot select them explicitly, so recreate the
                // legacy catalog safely in this isolated disposable CI database.
                await using var connection = new NpgsqlConnection(isolatedBuilder.ConnectionString);
                await connection.OpenAsync();
                var legacyTables = new[] {
                    "operations_case_sequences", "operations_team_members",
                    "operations_stage_assignments",
                    "operations_documents", "operations_document_versions",
                    "operations_document_extraction_jobs", "operations_case_quote_links",
                    "operations_case_quote_costs", "operations_case_milestones",
                    "operations_case_tasks", "operations_case_notes", "operations_case_audit_entries"
                };
                await using (var setup = new NpgsqlCommand("""
                    CREATE TABLE public."__EFMigrationsHistory" (
                        "MigrationId" varchar(150) PRIMARY KEY, "ProductVersion" varchar(32) NOT NULL
                    );
                    INSERT INTO public."__EFMigrationsHistory" VALUES
                      ('202611120001_InitialOperations','10.0.13'),
                      ('202611162310_OperationsFoundation','10.0.13'),
                      ('202611170310_OperationsFirstTables','10.0.13');
                    CREATE TABLE public.operations_cases (id uuid PRIMARY KEY);
                    CREATE TABLE public.operations_case_stages (
                        id uuid PRIMARY KEY,
                        case_id uuid NOT NULL REFERENCES public.operations_cases(id)
                    );
                    CREATE TABLE public.operations_teams (
                        id uuid PRIMARY KEY, company_id uuid NOT NULL, department_code varchar(30) NOT NULL,
                        name varchar(150) NOT NULL, is_active boolean NOT NULL
                    );
                    CREATE TABLE public.operations_assignment_rules (
                        id uuid PRIMARY KEY,
                        team_id uuid NOT NULL REFERENCES public.operations_teams(id),
                        department_code varchar(30) NOT NULL,
                        modality_code varchar(30) NULL,
                        origin_country_code varchar(2) NULL,
                        destination_country_code varchar(2) NULL,
                        priority integer NOT NULL,
                        is_active boolean NOT NULL
                    );
                    CREATE TABLE public.operations_outbox (
                        "Id" uuid PRIMARY KEY,
                        "EventName" varchar(200) NOT NULL,
                        "PayloadJson" text NOT NULL,
                        "OccurredAtUtc" timestamptz NOT NULL,
                        "PublishedAtUtc" timestamptz NULL,
                        "RetryCount" integer NOT NULL,
                        "LastError" text NULL
                    );
                    CREATE TABLE public.operations_inbox (
                        "ConsumerName" varchar(100) NOT NULL,
                        "EventId" uuid NOT NULL,
                        "EventName" varchar(200) NOT NULL,
                        "PayloadJson" text NOT NULL,
                        "ProcessedAtUtc" timestamptz NOT NULL,
                        CONSTRAINT "PK_operations_inbox" PRIMARY KEY ("ConsumerName", "EventId")
                    );
                    """, connection))
                    await setup.ExecuteNonQueryAsync();
                foreach (var name in legacyTables)
                {
                    await using var createTable = new NpgsqlCommand(
                        $"CREATE TABLE public.{name} (id uuid PRIMARY KEY)", connection);
                    await createTable.ExecuteNonQueryAsync();
                }
                await using (var cmd = new NpgsqlCommand("""
                    INSERT INTO public.operations_outbox
                      ("Id","EventName","PayloadJson","OccurredAtUtc","PublishedAtUtc","RetryCount","LastError")
                    VALUES (@pending,'operations.changed','{"caseId":"pending"}',now(),NULL,0,NULL),
                           (@published,'operations.changed','{"caseId":"published"}',now(),now(),1,NULL);
                    INSERT INTO public.operations_inbox
                      ("ConsumerName","EventId","EventName","PayloadJson","ProcessedAtUtc")
                    VALUES ('operations-realtime',@received,'operations.changed','{"caseId":"received"}',now());
                    INSERT INTO public.operations_teams(id,company_id,department_code,name,is_active)
                    VALUES (@team,@company,'APERTURAS','Legacy Pool',true),
                           (@originTeam,@company,'COORDINACION_ORIGEN','Origen · FCL internacional',true);
                    """, connection))
                {
                    cmd.Parameters.AddWithValue("pending", pendingId);
                    cmd.Parameters.AddWithValue("published", publishedId);
                    cmd.Parameters.AddWithValue("received", receivedId);
                    cmd.Parameters.AddWithValue("team", teamId);
                    cmd.Parameters.AddWithValue("originTeam", originTeamId);
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
                var routing = await verified.AssignmentRules.SingleAsync(x => x.TeamId == originTeamId);
                Assert.AreEqual("FCL", routing.ModalityCode);
                Assert.IsNull(routing.OriginCountryCode);
                Assert.IsTrue(routing.IsActive);
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
