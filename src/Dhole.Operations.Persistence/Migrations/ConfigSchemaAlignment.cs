using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Operations.Persistence.Migrations;

/// <summary>
/// DH-186 — non-destructive, transactional conversion of public legacy tables
/// into the Config-style dedicated schema and canonical framework inbox/outbox.
/// Existing data/PK/FK/index object identities are preserved by ALTER TABLE.
/// </summary>
[DbContext(typeof(OperationsDbContext))]
[Migration("202611170420_ConfigSchemaAlignment")]
public sealed class ConfigSchemaAlignment : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE SCHEMA IF NOT EXISTS operations;
            DO $operations_migrate$
            DECLARE table_name text;
            BEGIN
                FOREACH table_name IN ARRAY ARRAY[
                    'operations_cases',
                    'operations_case_sequences',
                    'operations_case_stages',
                    'operations_teams',
                    'operations_team_members',
                    'operations_assignment_rules',
                    'operations_stage_assignments',
                    'operations_documents',
                    'operations_document_versions',
                    'operations_document_extraction_jobs',
                    'operations_case_quote_links',
                    'operations_case_quote_costs',
                    'operations_case_milestones',
                    'operations_case_tasks',
                    'operations_case_notes',
                    'operations_case_audit_entries',
                    'operations_outbox',
                    'operations_inbox'
                ] LOOP
                    IF to_regclass(format('operations.%I', table_name)) IS NOT NULL THEN
                        RAISE EXCEPTION 'Refusing to overwrite existing operations.%', table_name;
                    END IF;
                    IF to_regclass(format('public.%I', table_name)) IS NULL THEN
                        RAISE EXCEPTION 'Required legacy table public.% is missing', table_name;
                    END IF;
                    EXECUTE format('ALTER TABLE public.%I SET SCHEMA operations', table_name);
                END LOOP;
            END $operations_migrate$;

            -- Rename legacy outbox in place: no copying, dropping or event loss.
            ALTER TABLE operations.operations_outbox RENAME TO outbox_messages;
            ALTER TABLE operations.outbox_messages RENAME COLUMN "Id" TO id;
            ALTER TABLE operations.outbox_messages RENAME COLUMN "EventName" TO event_name;
            ALTER TABLE operations.outbox_messages RENAME COLUMN "PayloadJson" TO payload_json;
            ALTER TABLE operations.outbox_messages RENAME COLUMN "OccurredAtUtc" TO created_at;
            ALTER TABLE operations.outbox_messages RENAME COLUMN "PublishedAtUtc" TO processed_at;
            ALTER TABLE operations.outbox_messages RENAME COLUMN "RetryCount" TO retry_count;
            ALTER TABLE operations.outbox_messages RENAME COLUMN "LastError" TO error_message;
            ALTER TABLE operations.outbox_messages
                ALTER COLUMN event_name TYPE varchar(500),
                ALTER COLUMN payload_json TYPE jsonb USING payload_json::jsonb;
            ALTER TABLE operations.outbox_messages ADD COLUMN event_id uuid;
            ALTER TABLE operations.outbox_messages ADD COLUMN event_type varchar(500);
            ALTER TABLE operations.outbox_messages ADD COLUMN source_service varchar(200)
                NOT NULL DEFAULT 'DholeOperationsService';
            ALTER TABLE operations.outbox_messages ADD COLUMN headers_json jsonb;
            ALTER TABLE operations.outbox_messages ADD COLUMN correlation_id varchar(100);
            ALTER TABLE operations.outbox_messages ADD COLUMN status varchar(50);
            UPDATE operations.outbox_messages
                SET event_id = id, event_type = event_name,
                    status = CASE WHEN processed_at IS NULL THEN 'Pending' ELSE 'Processed' END;
            ALTER TABLE operations.outbox_messages
                ALTER COLUMN event_id SET NOT NULL,
                ALTER COLUMN event_type SET NOT NULL,
                ALTER COLUMN status SET NOT NULL;
            CREATE UNIQUE INDEX ux_operations_outbox_event_id
                ON operations.outbox_messages(event_id);
            CREATE INDEX ix_operations_outbox_status_created
                ON operations.outbox_messages(status, created_at);

            -- Keep the legacy replay payload as a shadow property; canonical
            -- framework InboxMessage fields are added without removing receipts.
            ALTER TABLE operations.operations_inbox RENAME TO inbox_messages;
            ALTER TABLE operations.inbox_messages RENAME COLUMN "ConsumerName" TO consumer_service;
            ALTER TABLE operations.inbox_messages RENAME COLUMN "EventId" TO event_id;
            ALTER TABLE operations.inbox_messages RENAME COLUMN "EventName" TO event_name;
            ALTER TABLE operations.inbox_messages RENAME COLUMN "PayloadJson" TO payload_json;
            ALTER TABLE operations.inbox_messages RENAME COLUMN "ProcessedAtUtc" TO processed_at;
            ALTER TABLE operations.inbox_messages
                ALTER COLUMN consumer_service TYPE varchar(200),
                ALTER COLUMN event_name TYPE varchar(500),
                ALTER COLUMN processed_at DROP NOT NULL;
            ALTER TABLE operations.inbox_messages ADD COLUMN id uuid;
            ALTER TABLE operations.inbox_messages ADD COLUMN event_type varchar(500);
            ALTER TABLE operations.inbox_messages ADD COLUMN source_service varchar(200)
                NOT NULL DEFAULT 'DholeOperationsService';
            ALTER TABLE operations.inbox_messages ADD COLUMN correlation_id varchar(100);
            ALTER TABLE operations.inbox_messages ADD COLUMN status varchar(50);
            ALTER TABLE operations.inbox_messages ADD COLUMN created_at timestamptz;
            UPDATE operations.inbox_messages
                SET id = gen_random_uuid(), event_type = event_name,
                    status = 'Processed', created_at = COALESCE(processed_at, now());
            ALTER TABLE operations.inbox_messages
                ALTER COLUMN id SET NOT NULL,
                ALTER COLUMN event_type SET NOT NULL,
                ALTER COLUMN status SET NOT NULL,
                ALTER COLUMN created_at SET NOT NULL;
            ALTER TABLE operations.inbox_messages DROP CONSTRAINT "PK_operations_inbox";
            ALTER TABLE operations.inbox_messages ADD CONSTRAINT pk_operations_inbox_messages PRIMARY KEY (id);
            CREATE UNIQUE INDEX ux_operations_inbox_consumer_event
                ON operations.inbox_messages(event_id, consumer_service);
            CREATE INDEX ix_operations_inbox_status_created
                ON operations.inbox_messages(status, created_at);
            """);
    }

    // Never silently undo a schema move or destroy production business events.
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("DH-186 schema upgrade is non-destructive and not automatically reversible.");
}
