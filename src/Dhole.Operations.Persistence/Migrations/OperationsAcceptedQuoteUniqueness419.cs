using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Operations.Persistence.Migrations;

/// <summary>Business-key uniqueness across event IDs and worker restarts.</summary>
[DbContext(typeof(OperationsDbContext))]
[Migration("202611231550_OperationsAcceptedQuoteUniqueness419")]
public sealed class OperationsAcceptedQuoteUniqueness419 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Legacy manual records have no source quote ID; only automatic cases are constrained.
        migrationBuilder.Sql("""
            CREATE UNIQUE INDEX IF NOT EXISTS ux_operations_automatic_source_quote
            ON operations.operations_cases (source_quote_id)
            WHERE aperture_type = 'AUTOMATIC' AND source_quote_id IS NOT NULL;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Retain automatic-opening deduplication on rollback.");
}
