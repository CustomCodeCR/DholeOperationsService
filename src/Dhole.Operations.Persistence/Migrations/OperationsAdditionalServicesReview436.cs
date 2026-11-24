using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Operations.Persistence.Migrations;

/// <summary>Non-destructive, individually audited commercial-review linkage.</summary>
[DbContext(typeof(OperationsDbContext))]
[Migration("202611232040_OperationsAdditionalServicesReview436")]
public sealed class OperationsAdditionalServicesReview436 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE operations.operations_case_service_lines
                ADD COLUMN IF NOT EXISTS idempotency_key uuid;
            CREATE UNIQUE INDEX IF NOT EXISTS ux_operations_extra_service_idempotency
                ON operations.operations_case_service_lines(case_id,idempotency_key)
                WHERE source='OPERATIONS_ADDED' AND idempotency_key IS NOT NULL;

            CREATE TABLE IF NOT EXISTS operations.operations_commercial_alerts (
                id uuid PRIMARY KEY,
                case_id uuid NOT NULL REFERENCES operations.operations_cases(id) ON DELETE CASCADE,
                service_line_id uuid NOT NULL UNIQUE
                    REFERENCES operations.operations_case_service_lines(id) ON DELETE CASCADE,
                code varchar(50) NOT NULL,
                severity varchar(20) NOT NULL DEFAULT 'YELLOW',
                status varchar(40) NOT NULL DEFAULT 'PENDING_PRICING',
                created_at_utc timestamptz NOT NULL DEFAULT now(),
                created_by uuid NOT NULL,
                resolved_at_utc timestamptz NULL,
                CONSTRAINT ck_operations_commercial_severity
                    CHECK (severity IN ('YELLOW','RED','GREEN'))
            );
            CREATE INDEX IF NOT EXISTS ix_operations_commercial_alerts_pending
                ON operations.operations_commercial_alerts(case_id,status)
                WHERE status IN ('PENDING_PRICING','REVIEWING');

            CREATE TABLE IF NOT EXISTS operations.operations_pricing_review_requests (
                id uuid PRIMARY KEY,
                case_id uuid NOT NULL REFERENCES operations.operations_cases(id) ON DELETE CASCADE,
                service_line_id uuid NOT NULL UNIQUE
                    REFERENCES operations.operations_case_service_lines(id) ON DELETE CASCADE,
                quote_id uuid NULL,
                accepted_option_id uuid NULL,
                reason text NOT NULL,
                status varchar(40) NOT NULL DEFAULT 'PENDING_PRICING',
                requested_by uuid NOT NULL,
                requested_at_utc timestamptz NOT NULL DEFAULT now(),
                pricing_request_id uuid NULL,
                resolved_at_utc timestamptz NULL,
                CONSTRAINT ck_operations_review_reason CHECK (length(reason) >= 10)
            );
            CREATE INDEX IF NOT EXISTS ix_operations_pricing_review_pending
                ON operations.operations_pricing_review_requests(case_id,status)
                WHERE status IN ('PENDING_PRICING','REVIEWING');
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Do not discard commercial-review evidence during a software rollback.
    }
}
