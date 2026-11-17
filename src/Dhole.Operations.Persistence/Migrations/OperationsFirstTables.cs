using Dhole.Operations.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Operations.Persistence.Migrations;

/// <summary>Additive operational tables: QUO snapshots, timeline, work tracking and traceability.
/// Never drops existing case/pool/document/outbox/inbox tables on upgrade.</summary>
[DbContext(typeof(OperationsDbContext))]
[Migration("202611170310_OperationsFirstTables")]
public sealed class OperationsFirstTables : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE operations_case_quote_links (
                id uuid PRIMARY KEY,
                case_id uuid NOT NULL REFERENCES operations_cases(id) ON DELETE CASCADE,
                quote_id uuid NOT NULL,
                quote_option_id uuid NULL,
                quote_number varchar(60) NULL,
                is_primary boolean NOT NULL DEFAULT false,
                status varchar(30) NOT NULL,
                snapshot_version integer NOT NULL,
                linked_by uuid NOT NULL,
                linked_at_utc timestamptz NOT NULL,
                CONSTRAINT ck_operations_quote_snapshot_version CHECK (snapshot_version >= 1)
            );
            CREATE INDEX ix_operations_quote_links_case ON operations_case_quote_links(case_id, linked_at_utc);
            CREATE INDEX ix_operations_quote_links_source ON operations_case_quote_links(quote_id);
            CREATE UNIQUE INDEX ux_operations_quote_primary ON operations_case_quote_links(case_id) WHERE is_primary;

            CREATE TABLE operations_case_quote_costs (
                id uuid PRIMARY KEY,
                quote_link_id uuid NOT NULL REFERENCES operations_case_quote_links(id) ON DELETE CASCADE,
                source_cost_id uuid NULL,
                supplier_id uuid NULL,
                supplier_name varchar(200) NULL,
                cost_code varchar(60) NOT NULL,
                description varchar(500) NOT NULL,
                currency_code varchar(3) NOT NULL,
                cost_amount numeric(18,4) NOT NULL,
                sale_amount numeric(18,4) NOT NULL,
                captured_at_utc timestamptz NOT NULL,
                CONSTRAINT ck_operations_quote_currency CHECK (currency_code ~ '^[A-Z]{3}$')
            );
            CREATE INDEX ix_operations_quote_costs_link ON operations_case_quote_costs(quote_link_id);
            CREATE INDEX ix_operations_quote_costs_supplier ON operations_case_quote_costs(supplier_id);
            CREATE UNIQUE INDEX ux_operations_quote_cost_source
                ON operations_case_quote_costs(quote_link_id, source_cost_id)
                WHERE source_cost_id IS NOT NULL;

            CREATE TABLE operations_case_milestones (
                id uuid PRIMARY KEY,
                case_id uuid NOT NULL REFERENCES operations_cases(id) ON DELETE CASCADE,
                case_stage_id uuid NULL REFERENCES operations_case_stages(id) ON DELETE RESTRICT,
                milestone_code varchar(60) NOT NULL,
                title varchar(200) NOT NULL,
                status varchar(30) NOT NULL,
                planned_at_utc timestamptz NULL,
                actual_at_utc timestamptz NULL,
                created_by uuid NOT NULL,
                created_at_utc timestamptz NOT NULL
            );
            CREATE INDEX ix_operations_milestones_case ON operations_case_milestones(case_id, planned_at_utc);
            CREATE INDEX ix_operations_milestones_stage ON operations_case_milestones(case_stage_id);

            CREATE TABLE operations_case_tasks (
                id uuid PRIMARY KEY,
                case_id uuid NOT NULL REFERENCES operations_cases(id) ON DELETE CASCADE,
                case_stage_id uuid NULL REFERENCES operations_case_stages(id) ON DELETE RESTRICT,
                title varchar(200) NOT NULL,
                details text NULL,
                status varchar(30) NOT NULL,
                priority smallint NOT NULL,
                assigned_user_id uuid NULL,
                due_at_utc timestamptz NULL,
                completed_at_utc timestamptz NULL,
                created_by uuid NOT NULL,
                created_at_utc timestamptz NOT NULL,
                updated_at_utc timestamptz NOT NULL,
                version bigint NOT NULL,
                CONSTRAINT ck_operations_task_priority CHECK (priority BETWEEN 1 AND 5)
            );
            CREATE INDEX ix_operations_tasks_case_status ON operations_case_tasks(case_id, status, due_at_utc);
            CREATE INDEX ix_operations_tasks_assignee ON operations_case_tasks(assigned_user_id, status, due_at_utc);
            CREATE INDEX ix_operations_tasks_stage ON operations_case_tasks(case_stage_id);

            CREATE TABLE operations_case_notes (
                id uuid PRIMARY KEY,
                case_id uuid NOT NULL REFERENCES operations_cases(id) ON DELETE CASCADE,
                case_stage_id uuid NULL REFERENCES operations_case_stages(id) ON DELETE RESTRICT,
                content text NOT NULL,
                visibility varchar(20) NOT NULL,
                created_by uuid NOT NULL,
                created_at_utc timestamptz NOT NULL,
                updated_at_utc timestamptz NULL
            );
            CREATE INDEX ix_operations_notes_case_created ON operations_case_notes(case_id, created_at_utc);
            CREATE INDEX ix_operations_notes_stage ON operations_case_notes(case_stage_id);

            CREATE TABLE operations_case_audit_entries (
                id uuid PRIMARY KEY,
                case_id uuid NOT NULL REFERENCES operations_cases(id) ON DELETE CASCADE,
                case_stage_id uuid NULL REFERENCES operations_case_stages(id) ON DELETE RESTRICT,
                event_id uuid NOT NULL UNIQUE,
                action_code varchar(100) NOT NULL,
                before_json jsonb NULL,
                after_json jsonb NULL,
                actor_user_id uuid NULL,
                correlation_id varchar(100) NULL,
                occurred_at_utc timestamptz NOT NULL
            );
            CREATE INDEX ix_operations_audit_case_occurred ON operations_case_audit_entries(case_id, occurred_at_utc);
            CREATE INDEX ix_operations_audit_stage ON operations_case_audit_entries(case_stage_id);
            """);
    }

    // A rollback of this migration intentionally requires an explicit reviewed procedure.
    // Dropping business records automatically would violate Operations data-retention policy.
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Operations records are retained; automatic destructive rollback is disabled.");
}
