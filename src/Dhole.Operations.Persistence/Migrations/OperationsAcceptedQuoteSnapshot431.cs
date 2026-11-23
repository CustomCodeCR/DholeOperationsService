using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Operations.Persistence.Migrations;

/// <summary>Restricts full accepted Pricing snapshot to quote-link data, not the general opening JSON.</summary>
[DbContext(typeof(OperationsDbContext))]
[Migration("202611231905_OperationsAcceptedQuoteSnapshot431")]
public sealed class OperationsAcceptedQuoteSnapshot431 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE operations.operations_case_quote_links
              ADD COLUMN IF NOT EXISTS commercial_snapshot_json jsonb NOT NULL DEFAULT '{}'::jsonb;
            COMMENT ON COLUMN operations.operations_case_quote_links.commercial_snapshot_json IS
              'Immutable accepted Pricing snapshot: guarded by operations.finance.manage, never shared in case opening_data_json';
            """);
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE operations.operations_case_quote_links
              DROP COLUMN IF EXISTS commercial_snapshot_json;
            """);
    }
}
