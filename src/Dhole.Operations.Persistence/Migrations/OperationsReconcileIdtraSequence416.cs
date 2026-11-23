using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Operations.Persistence.Migrations;

/// <summary>
/// One-time, non-destructive reconciliation of the global IDTRA counter with
/// legacy sequence rows and all persisted cases. Runtime allocation still uses
/// the existing atomic INSERT ON CONFLICT UPDATE, never MAX(idtra)+1.
/// </summary>
[DbContext(typeof(OperationsDbContext))]
[Migration("202611230730_OperationsReconcileIdtraSequence416")]
public sealed class OperationsReconcileIdtraSequence416 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $reconcile$
            DECLARE
                highest bigint := 0;
                highest_cases bigint := 0;
            BEGIN
                SELECT COALESCE(MAX(last_number), 0) INTO highest
                  FROM operations.operations_case_sequences;

                -- Older deployments can have a partial legacy operations_cases
                -- table without IDTRA or sequence_number. Avoid referencing
                -- columns that have not existed in that schema generation.
                IF EXISTS (
                    SELECT 1 FROM information_schema.columns
                     WHERE table_schema = 'operations'
                       AND table_name = 'operations_cases'
                       AND column_name = 'idtra'
                ) THEN
                    EXECUTE $cases$
                        SELECT COALESCE(MAX(CASE
                          WHEN idtra ~ '^IDTRA[0-9]{1,18}$'
                            THEN substring(idtra FROM 6)::bigint
                          WHEN idtra ~ '^IDTRA-[0-9]+-[0-9]{1,18}$'
                            THEN split_part(idtra, '-', 3)::bigint
                          ELSE NULL END), 0)
                        FROM operations.operations_cases
                    $cases$ INTO highest_cases;
                    highest := GREATEST(highest, highest_cases);
                END IF;

                INSERT INTO operations.operations_case_sequences
                    (id, series_code, last_number, updated_at_utc)
                VALUES (gen_random_uuid(), '00000', highest, now())
                ON CONFLICT (series_code)
                DO UPDATE SET last_number = GREATEST(
                    operations_case_sequences.last_number, EXCLUDED.last_number),
                    updated_at_utc = now();
            END;
            $reconcile$;
            """);
    }

    // Reconciliation intentionally has no rollback: decrementing a published
    // counter would reissue previously allocated visible case identifiers.
    protected override void Down(MigrationBuilder migrationBuilder) { }
}
