using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Operations.Persistence.Migrations;

/// <summary>
/// Adds independent draft metadata without modifying existing commercial quotes.
/// Legacy IDTRAs retain their original external identifiers.
/// </summary>
[DbContext(typeof(OperationsDbContext))]
[Migration("202611211715_OperationsOpeningDataAndGlobalIdtra")]
public sealed class OperationsOpeningDataAndGlobalIdtra : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE operations.operations_cases
              ADD COLUMN IF NOT EXISTS opening_data_json jsonb NOT NULL DEFAULT '{}'::jsonb;
            COMMENT ON COLUMN operations.operations_cases.opening_data_json IS
              'Manual opening draft, not authoritative for CRM/Pricing commercial records';
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE operations.operations_cases
              DROP COLUMN IF EXISTS opening_data_json;
            """);
    }
}
