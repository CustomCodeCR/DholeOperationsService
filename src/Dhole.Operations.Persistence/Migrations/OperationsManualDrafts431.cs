using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Operations.Persistence.Migrations;

[DbContext(typeof(OperationsDbContext))]
[Migration("202611231900_OperationsManualDrafts431")]
public sealed class OperationsManualDrafts431 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE IF NOT EXISTS operations.operations_manual_opening_drafts (
                id uuid PRIMARY KEY,
                user_id uuid NOT NULL,
                payload_json jsonb NOT NULL DEFAULT '{}'::jsonb,
                updated_at_utc timestamptz NOT NULL DEFAULT now()
            );
            CREATE INDEX IF NOT EXISTS ix_operations_manual_opening_drafts_user_id_updated_at_utc
              ON operations.operations_manual_opening_drafts (user_id, updated_at_utc);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TABLE IF EXISTS operations.operations_manual_opening_drafts;
            """);
    }
}
