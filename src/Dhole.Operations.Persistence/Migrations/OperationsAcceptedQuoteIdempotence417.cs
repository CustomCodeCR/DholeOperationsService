using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Operations.Persistence.Migrations;

/// <summary>Global idempotence for automatic QUO openings, across worker restarts.</summary>
[DbContext(typeof(OperationsDbContext))]
[Migration("202611231620_OperationsAcceptedQuoteIdempotence417")]
public sealed class OperationsAcceptedQuoteIdempotence417 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE IF NOT EXISTS operations.operations_opening_drafts (
                id uuid PRIMARY KEY,
                company_id uuid NOT NULL,
                user_id uuid NOT NULL,
                payload_json jsonb NOT NULL DEFAULT '{}'::jsonb,
                created_at_utc timestamptz NOT NULL DEFAULT now(),
                updated_at_utc timestamptz NOT NULL DEFAULT now()
            );
            CREATE INDEX IF NOT EXISTS ix_operations_opening_drafts_user_417
                ON operations.operations_opening_drafts (company_id, user_id, updated_at_utc DESC);

            ALTER TABLE operations.operations_case_quote_links
              ADD COLUMN IF NOT EXISTS commercial_snapshot_json jsonb NULL;

            CREATE UNIQUE INDEX IF NOT EXISTS ux_operations_auto_case_source_quote_417
              ON operations.operations_cases (source_quote_id)
              WHERE source_quote_id IS NOT NULL AND aperture_type = 'AUTOMATIC';

            CREATE UNIQUE INDEX IF NOT EXISTS ux_operations_auto_case_quote_group_417
              ON operations.operations_case_quote_links (quote_number)
              WHERE is_primary = TRUE AND status = 'ACCEPTED' AND quote_number IS NOT NULL;
            """);
    }

    // Never remove business-record protections through an automatic rollback.
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Automatic QUO opening idempotence must be retained.");
}
