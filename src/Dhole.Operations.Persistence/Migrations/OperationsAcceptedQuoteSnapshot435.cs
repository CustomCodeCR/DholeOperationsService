using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Operations.Persistence.Migrations;

/// <summary>Client acceptance is at-least-once: DB constraints enforce one
/// automatic case per accepted parent QUO and one immutable event/snapshot.</summary>
[DbContext(typeof(OperationsDbContext))]
[Migration("202611232025_OperationsAcceptedQuoteSnapshot435")]
public sealed class OperationsAcceptedQuoteSnapshot435 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE operations.operations_cases
                ADD COLUMN IF NOT EXISTS aperture_type varchar(20) NOT NULL DEFAULT 'MANUAL';
            ALTER TABLE operations.operations_cases
                ADD COLUMN IF NOT EXISTS source_quote_id uuid;
            CREATE UNIQUE INDEX IF NOT EXISTS ux_operations_cases_auto_quote
            ON operations.operations_cases(source_quote_id)
            WHERE aperture_type = 'AUTOMATIC' AND source_quote_id IS NOT NULL;

            CREATE TABLE IF NOT EXISTS operations.operations_accepted_quote_snapshots (
                id uuid PRIMARY KEY,
                case_id uuid NOT NULL UNIQUE REFERENCES operations.operations_cases(id) ON DELETE CASCADE,
                event_id uuid NOT NULL UNIQUE,
                quote_id uuid NOT NULL,
                accepted_option_id uuid NOT NULL,
                commercial_revision integer NOT NULL CHECK (commercial_revision > 0),
                snapshot_json jsonb NOT NULL,
                accepted_at_utc timestamptz NOT NULL,
                captured_at_utc timestamptz NOT NULL DEFAULT now(),
                CONSTRAINT ck_operations_accepted_quote_snapshot_json CHECK
                    (jsonb_typeof(snapshot_json) = 'object')
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_operations_accepted_quote_identity
            ON operations.operations_accepted_quote_snapshots(quote_id, accepted_option_id, commercial_revision);

            CREATE TABLE IF NOT EXISTS operations.operations_case_service_lines (
                id uuid PRIMARY KEY,
                case_id uuid NOT NULL REFERENCES operations.operations_cases(id) ON DELETE CASCADE,
                pricing_service_id uuid,
                service_code varchar(60) NOT NULL,
                service_name varchar(200) NOT NULL,
                source varchar(30) NOT NULL CHECK (source IN ('QUOTED','OPERATIONS_ADDED')),
                requires_pricing_review boolean NOT NULL DEFAULT false,
                review_status varchar(40) NOT NULL DEFAULT 'NOT_REQUIRED',
                created_by uuid NOT NULL,
                created_at_utc timestamptz NOT NULL DEFAULT now()
            );
            CREATE INDEX IF NOT EXISTS ix_operations_case_service_lines_case
                ON operations.operations_case_service_lines(case_id);

            CREATE TABLE IF NOT EXISTS operations.operations_case_equipment_lines (
                id uuid PRIMARY KEY,
                case_id uuid NOT NULL REFERENCES operations.operations_cases(id) ON DELETE CASCADE,
                equipment_type_id uuid NOT NULL,
                equipment_code varchar(50) NOT NULL,
                equipment_name varchar(200) NOT NULL,
                quantity integer NOT NULL CHECK (quantity > 0),
                unit_weights_kg_json jsonb NOT NULL DEFAULT '[]'::jsonb,
                CONSTRAINT ck_operations_equipment_units_json CHECK
                    (jsonb_typeof(unit_weights_kg_json) = 'array')
            );
            CREATE INDEX IF NOT EXISTS ix_operations_case_equipment_lines_case
                ON operations.operations_case_equipment_lines(case_id);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Non-destructive rollback: preserve immutable accepted commercial evidence.
    }
}
