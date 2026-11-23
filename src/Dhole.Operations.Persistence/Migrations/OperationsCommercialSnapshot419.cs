using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Operations.Persistence.Migrations;

[DbContext(typeof(OperationsDbContext))]
[Migration("202611231700_OperationsCommercialSnapshot419")]
public sealed class OperationsCommercialSnapshot419 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("""
            CREATE TABLE IF NOT EXISTS operations.operations_case_commercial_snapshots (
                id uuid PRIMARY KEY,
                case_id uuid NOT NULL UNIQUE REFERENCES operations.operations_cases(id) ON DELETE CASCADE,
                quote_id uuid NOT NULL,
                revision_number integer NOT NULL,
                snapshot_json jsonb NOT NULL,
                captured_at_utc timestamptz NOT NULL
            );
            """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Commercial snapshots must never be silently erased.");
}
