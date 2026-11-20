using Dhole.Operations.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Operations.Persistence.Migrations;

/// <summary>
/// Non-destructive upgrade. Old IDTRAs stay unchanged. The reserved "00000"
/// series is now the single global counter, never a user-selectable series.
/// Historical Dynamics' highest assigned number MUST be synchronized before
/// issuing new IDs, since Dynamics belongs to a different database.
/// </summary>
[DbContext(typeof(OperationsDbContext))]
[Migration("202611201430_IdtraGlobalCounterAndApertureDraft")]
public sealed class IdtraGlobalCounterAndApertureDraft : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE operations.operations_cases
              ADD COLUMN IF NOT EXISTS aperture_details_json jsonb NULL;
            ALTER TABLE operations.operations_case_sequences
              ADD COLUMN IF NOT EXISTS is_authoritatively_synced boolean NOT NULL DEFAULT false;

            -- Preserve and reconcile numbers across all existing local series,
            -- including the new IDTRA+digits notation if it was ever issued.
            -- MAX is a one-time migration safeguard, never an issuance algorithm.
            INSERT INTO operations.operations_case_sequences
                (id, series_code, last_number, updated_at_utc, is_authoritatively_synced)
            SELECT gen_random_uuid(), '00000',
                   GREATEST(
                      COALESCE((SELECT MAX(last_number)
                                 FROM operations.operations_case_sequences), 0),
                      COALESCE((SELECT MAX(sequence_number)
                                 FROM operations.operations_cases), 0),
                      COALESCE((SELECT MAX(
                          CASE WHEN idtra ~ '^IDTRA[0-9]{1,18}$'
                               THEN substring(idtra FROM 6)::bigint
                               ELSE 0 END)
                        FROM operations.operations_cases), 0)),
                   now(), false
            ON CONFLICT (series_code) DO UPDATE
                SET last_number = GREATEST(
                    operations_case_sequences.last_number, EXCLUDED.last_number),
                    updated_at_utc = now();

            -- Unique visible IDs remain protected by the existing unique index.
            -- Do not rewrite imported Dynamics references or previously-issued IDs.
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException(
            "Manual reconciliation required before removing IDTRA counter safeguards.");
}
