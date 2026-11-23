using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Operations.Persistence.Migrations;

[DbContext(typeof(OperationsDbContext))]
[Migration("202611231210_OperationsCommercialServiceReview417")]
public sealed class OperationsCommercialServiceReview417 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE IF NOT EXISTS operations.operations_case_service_lines (
                id uuid PRIMARY KEY,
                case_id uuid NOT NULL REFERENCES operations.operations_cases(id) ON DELETE CASCADE,
                pricing_service_line_id uuid NULL,
                service_catalog_id uuid NULL,
                service_code varchar(80) NOT NULL DEFAULT '',
                service_name varchar(255) NOT NULL,
                source varchar(24) NOT NULL CHECK(source IN ('QUOTED','OPERATIONS_ADDED')),
                review_status varchar(35) NOT NULL,
                review_reason text NULL,
                created_by uuid NOT NULL,
                created_at_utc timestamptz NOT NULL,
                resolved_by uuid NULL,
                resolved_at_utc timestamptz NULL,
                version bigint NOT NULL DEFAULT 1
            );
            CREATE INDEX IF NOT EXISTS ix_ops_case_services_case_review
                ON operations.operations_case_service_lines(case_id, review_status);
            CREATE UNIQUE INDEX IF NOT EXISTS ux_ops_case_services_quoted
                ON operations.operations_case_service_lines(case_id, pricing_service_line_id)
                WHERE source = 'QUOTED' AND pricing_service_line_id IS NOT NULL;
            """);
    }
    protected override void Down(MigrationBuilder migrationBuilder) { }
}
