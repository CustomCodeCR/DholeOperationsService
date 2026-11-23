using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Operations.Persistence.Migrations;

/// <summary>Additive quoted option identity and historical commercial snapshot.
/// Existing IDTRA sequences and manual cases are never reset or rewritten.</summary>
[DbContext(typeof(OperationsDbContext))]
[Migration("202611231130_OperationsAcceptedQuote417")]
public sealed class OperationsAcceptedQuote417 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $migration$
            BEGIN
                IF EXISTS (SELECT 1 FROM information_schema.tables
                    WHERE table_schema='operations' AND table_name='operations_cases') THEN
                    ALTER TABLE operations.operations_cases
                        ADD COLUMN IF NOT EXISTS source_quote_group_id uuid NULL;
                    IF EXISTS (SELECT 1 FROM information_schema.columns
                        WHERE table_schema='operations' AND table_name='operations_cases'
                          AND column_name='source_quote_id')
                        AND EXISTS (SELECT 1 FROM information_schema.columns
                        WHERE table_schema='operations' AND table_name='operations_cases'
                          AND column_name='aperture_type') THEN
                        CREATE UNIQUE INDEX IF NOT EXISTS ux_operations_accepted_quote
                            ON operations.operations_cases(source_quote_id)
                            WHERE source_quote_id IS NOT NULL AND aperture_type = 'AUTOMATIC';
                        CREATE UNIQUE INDEX IF NOT EXISTS ux_operations_accepted_quote_group
                            ON operations.operations_cases(source_quote_group_id)
                            WHERE source_quote_group_id IS NOT NULL AND aperture_type = 'AUTOMATIC';
                    END IF;
                END IF;
                IF EXISTS (SELECT 1 FROM information_schema.tables
                    WHERE table_schema='operations' AND table_name='operations_case_quote_links') THEN
                    ALTER TABLE operations.operations_case_quote_links
                        ADD COLUMN IF NOT EXISTS snapshot_json jsonb NOT NULL DEFAULT '{}'::jsonb;
                END IF;
            END $migration$;
            """);
    }

    // No destructive rollback of accepted commercial history.
    protected override void Down(MigrationBuilder migrationBuilder) { }
}
