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
            WITH case_high AS (
                SELECT COALESCE(MAX(CASE
                        WHEN idtra ~ '^IDTRA[0-9]+$'
                            THEN substring(idtra FROM 6)::bigint
                        WHEN idtra ~ '^IDTRA-[0-9]+-[0-9]+$'
                            THEN split_part(idtra, '-', 3)::bigint
                        ELSE NULL END), 0) AS high
                FROM operations.operations_cases
            ),
            all_high AS (
                SELECT GREATEST(
                    (SELECT high FROM case_high),
                    COALESCE((SELECT MAX(last_number)
                        FROM operations.operations_case_sequences), 0)
                ) AS high
            )
            INSERT INTO operations.operations_case_sequences
                (id, series_code, last_number, updated_at_utc)
            SELECT gen_random_uuid(), '00000', high, now() FROM all_high
            ON CONFLICT (series_code)
            DO UPDATE SET last_number = GREATEST(
                operations_case_sequences.last_number, EXCLUDED.last_number),
                updated_at_utc = now();
            """);
    }

    // Reconciliation intentionally has no rollback: decrementing a published
    // counter would reissue previously allocated visible case identifiers.
    protected override void Down(MigrationBuilder migrationBuilder) { }
}
