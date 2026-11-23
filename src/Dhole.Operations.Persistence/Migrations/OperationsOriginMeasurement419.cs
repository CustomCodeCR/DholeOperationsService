using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Operations.Persistence.Migrations;

[DbContext(typeof(OperationsDbContext))]
[Migration("202611231750_OperationsOriginMeasurement419")]
public sealed class OperationsOriginMeasurement419 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("""
            CREATE TABLE IF NOT EXISTS operations.operations_origin_measurements (
                id uuid PRIMARY KEY,
                case_id uuid NOT NULL UNIQUE REFERENCES operations.operations_cases(id) ON DELETE CASCADE,
                original_weight numeric(20,6) NULL,
                weight_unit varchar(3) NULL,
                weight_kg numeric(20,6) NULL,
                original_volume numeric(20,6) NULL,
                volume_unit varchar(3) NULL,
                volume_cbm numeric(20,6) NULL,
                confirmed_by uuid NOT NULL,
                confirmed_at_utc timestamptz NOT NULL,
                version bigint NOT NULL CHECK (version >= 1)
            );
            """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Preserve original logistics measurements for audit.");
}
