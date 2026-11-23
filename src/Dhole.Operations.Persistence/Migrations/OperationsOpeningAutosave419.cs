using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Operations.Persistence.Migrations;

[DbContext(typeof(OperationsDbContext))]
[Migration("202611231600_OperationsOpeningAutosave419")]
public sealed class OperationsOpeningAutosave419 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE IF NOT EXISTS operations.operations_opening_autosave_drafts (
              id uuid PRIMARY KEY,
              owner_user_id uuid NOT NULL,
              company_id uuid NULL,
              payload_json jsonb NOT NULL,
              updated_at_utc timestamptz NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_operations_autosave_owner_updated
              ON operations.operations_opening_autosave_drafts (owner_user_id, updated_at_utc DESC);
            """);
    }
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Preserve user drafts until an approved retention policy is applied.");
}
