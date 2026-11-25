using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Operations.Persistence.Migrations;

[DbContext(typeof(OperationsDbContext))]
[Migration("202611242359_OperationsMultiIdtraQuote")]
public sealed class OperationsMultiIdtraQuote20261124 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP INDEX IF EXISTS operations.ux_operations_cases_auto_quote;
            CREATE INDEX IF NOT EXISTS ix_operations_cases_auto_quote
                ON operations.operations_cases(source_quote_id)
                WHERE aperture_type = 'AUTOMATIC' AND source_quote_id IS NOT NULL;
            DROP INDEX IF EXISTS operations.ux_operations_accepted_quote_identity;
            CREATE INDEX IF NOT EXISTS ix_operations_accepted_quote_identity
                ON operations.operations_accepted_quote_snapshots(quote_id, accepted_option_id, commercial_revision);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Do not delete cases or reintroduce a constraint incompatible with multiple valid IDTRAs.
    }
}
