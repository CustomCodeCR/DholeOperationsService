using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Operations.Persistence.Migrations;

[DbContext(typeof(OperationsDbContext))]
[Migration("202611280004_OperationsDocumentOwnership")]
public sealed class OperationsDocumentOwnership20261128 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // No ownership or access-class backfill is inferred for historical files.
        migrationBuilder.Sql("""
            ALTER TABLE operations.operations_documents
                ADD COLUMN owner_stage_id uuid NULL REFERENCES operations.operations_case_stages(id) ON DELETE RESTRICT,
                ADD COLUMN access_class varchar(30) NULL,
                ADD COLUMN version bigint NOT NULL DEFAULT 1;
            CREATE INDEX ix_operations_documents_owner_stage_id
                ON operations.operations_documents(owner_stage_id);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Document ownership and review history require a reviewed rollback.");
}
