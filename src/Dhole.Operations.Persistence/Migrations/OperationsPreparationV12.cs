using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Operations.Persistence.Migrations;

/// <summary>Additive v1.2 preparation. Preserves Config-compatible outbox and inbox.</summary>
[DbContext(typeof(OperationsDbContext))]
[Migration("202611172230_OperationsPreparationV12")]
public sealed class OperationsPreparationV12 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE operations.operations_origin_progress (
                id uuid PRIMARY KEY,
                case_stage_id uuid NOT NULL UNIQUE REFERENCES operations.operations_case_stages(id) ON DELETE CASCADE,
                status_code varchar(40) NOT NULL,
                actual_departure_at_utc timestamptz NULL,
                departure_event_id uuid NULL UNIQUE,
                departure_source varchar(40) NULL,
                evidence_storage_file_id uuid NULL,
                updated_at_utc timestamptz NOT NULL,
                version bigint NOT NULL CHECK (version >= 0)
            );
            CREATE TABLE operations.operations_internal_notes (
                id uuid PRIMARY KEY,
                case_id uuid NOT NULL REFERENCES operations.operations_cases(id) ON DELETE CASCADE,
                case_stage_id uuid NULL REFERENCES operations.operations_case_stages(id) ON DELETE RESTRICT,
                body text NOT NULL,
                created_by uuid NOT NULL,
                created_at_utc timestamptz NOT NULL,
                updated_at_utc timestamptz NOT NULL,
                version bigint NOT NULL CHECK (version >= 1)
            );
            CREATE INDEX ix_operations_private_notes_case_created
                ON operations.operations_internal_notes(case_id, created_at_utc DESC);
            CREATE TABLE operations.operations_carrier_connections (
                id uuid PRIMARY KEY,
                company_id uuid NOT NULL,
                carrier_code varchar(30) NOT NULL,
                srm_provider_id uuid NULL,
                environment varchar(12) NOT NULL CHECK (environment IN ('UAT', 'PRODUCTION')),
                status varchar(40) NOT NULL,
                last_validated_at_utc timestamptz NULL,
                created_at_utc timestamptz NOT NULL,
                updated_at_utc timestamptz NOT NULL
            );
            CREATE INDEX ix_operations_carriers_tenant
                ON operations.operations_carrier_connections(company_id, carrier_code, status);
            CREATE TABLE operations.operations_carrier_capabilities (
                id uuid PRIMARY KEY,
                connection_id uuid NOT NULL REFERENCES operations.operations_carrier_connections(id) ON DELETE CASCADE,
                product_code varchar(60) NOT NULL,
                capability_code varchar(60) NOT NULL,
                status varchar(30) NOT NULL,
                api_version varchar(30) NULL,
                verified_at_utc timestamptz NULL,
                evidence_ref varchar(200) NULL,
                UNIQUE(connection_id, product_code, capability_code)
            );
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Review data retention before rollback of operational records.");
}
