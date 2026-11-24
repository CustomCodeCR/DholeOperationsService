using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Operations.Persistence.Migrations;

[DbContext(typeof(OperationsDbContext))]
[Migration("202611241430_OperationsQuoteDiscrepancyReview")]
public sealed class OperationsQuoteDiscrepancyReview : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE IF NOT EXISTS operations.operations_quote_discrepancy_reviews (
                id uuid PRIMARY KEY,
                case_id uuid NOT NULL REFERENCES operations.operations_cases(id) ON DELETE CASCADE,
                quote_id uuid NOT NULL,
                accepted_option_id uuid NOT NULL,
                commercial_revision integer NOT NULL,
                fingerprint varchar(64) NOT NULL,
                differences_json jsonb NOT NULL,
                reason text NOT NULL,
                status varchar(40) NOT NULL,
                requested_by uuid NOT NULL,
                requested_at_utc timestamptz NOT NULL,
                pricing_request_id uuid NULL,
                latest_pricing_version bigint NOT NULL DEFAULT 0,
                resolved_at_utc timestamptz NULL,
                decision_reason text NULL,
                UNIQUE(case_id, fingerprint)
            );
            CREATE INDEX IF NOT EXISTS ix_operations_quote_discrepancy_reviews_case_status
                ON operations.operations_quote_discrepancy_reviews(case_id,status);
            """);
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Do not destroy commercial review history on rollback.
    }
}
