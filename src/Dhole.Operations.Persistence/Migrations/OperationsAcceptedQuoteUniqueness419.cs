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
            DO $accepted_quote$ BEGIN
              IF EXISTS (
                SELECT 1 FROM information_schema.columns
                WHERE table_schema='operations' AND table_name='operations_cases'
                AND column_name='aperture_type'
              ) AND EXISTS (
                SELECT 1 FROM information_schema.columns
                WHERE table_schema='operations' AND table_name='operations_cases'
                AND column_name='source_quote_id'
              ) THEN
                EXECUTE 'CREATE UNIQUE INDEX IF NOT EXISTS ux_operations_automatic_source_quote
                  ON operations.operations_cases (source_quote_id)
                  WHERE aperture_type = ''AUTOMATIC'' AND source_quote_id IS NOT NULL';
              END IF;
            END $accepted_quote$;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Retain automatic-opening deduplication on rollback.");
}
