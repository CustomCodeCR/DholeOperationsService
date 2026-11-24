using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Operations.Persistence.Migrations;

/// <summary>
/// Drafts must survive browser restarts and may be incomplete. No account,
/// shipment, team or IDTRA is allocated until the case is actually created.
/// </summary>
[DbContext(typeof(OperationsDbContext))]
[Migration("202611231900_OperationsManualOpeningDrafts434")]
public sealed class OperationsManualOpeningDrafts434 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE IF NOT EXISTS operations.operations_manual_opening_drafts (
                id uuid PRIMARY KEY,
                company_id uuid NOT NULL,
                created_by uuid NOT NULL,
                payload_json jsonb NOT NULL DEFAULT '{}'::jsonb,
                version bigint NOT NULL DEFAULT 1,
                created_at_utc timestamptz NOT NULL DEFAULT now(),
                updated_at_utc timestamptz NOT NULL DEFAULT now(),
                CONSTRAINT ck_operations_manual_draft_object
                    CHECK (jsonb_typeof(payload_json) = 'object'),
                CONSTRAINT ck_operations_manual_draft_version
                    CHECK (version >= 1)
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_operations_manual_opening_drafts_user_company
                ON operations.operations_manual_opening_drafts(company_id, created_by);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Existing user drafts must not be silently deleted during rollback.
    }
}
