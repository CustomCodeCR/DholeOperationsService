using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Operations.Persistence.Migrations;

[DbContext(typeof(OperationsDbContext))]
[Migration("202611231830_OperationsAdditionalServices419")]
public sealed class OperationsAdditionalServices419 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("""
            CREATE TABLE IF NOT EXISTS operations.operations_additional_services (
                id uuid PRIMARY KEY,
                case_id uuid NOT NULL REFERENCES operations.operations_cases(id) ON DELETE CASCADE,
                source_quote_id uuid NULL,
                service_code varchar(80) NOT NULL,
                service_name varchar(300) NOT NULL,
                reason varchar(2000) NOT NULL,
                source varchar(30) NOT NULL DEFAULT 'OPERATIONS_ADDED',
                review_status varchar(50) NOT NULL DEFAULT 'PENDING_PRICING_REVIEW',
                added_by uuid NOT NULL,
                added_at_utc timestamptz NOT NULL,
                reviewed_at_utc timestamptz NULL,
                pricing_resolution_notes varchar(2000) NULL
            );
            CREATE INDEX IF NOT EXISTS ix_operations_additional_services_case
            ON operations.operations_additional_services(case_id);
            """);
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Retain commercial review audit trail.");
}
