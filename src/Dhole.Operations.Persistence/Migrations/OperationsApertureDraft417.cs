using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Operations.Persistence.Migrations;

/// <summary>Separate early/unfinished PostgreSQL drafts, without consuming an IDTRA.</summary>
[DbContext(typeof(OperationsDbContext))]
[Migration("202611231150_OperationsApertureDraft417")]
public sealed class OperationsApertureDraft417 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE IF NOT EXISTS operations.operations_aperture_drafts (
                id uuid PRIMARY KEY,
                company_id uuid NOT NULL,
                owner_user_id uuid NOT NULL,
                payload_json jsonb NOT NULL DEFAULT '{}'::jsonb,
                updated_at_utc timestamptz NOT NULL,
                version bigint NOT NULL DEFAULT 1 CHECK(version >= 1)
            );
            CREATE INDEX IF NOT EXISTS ix_operations_aperture_drafts_owner_updated
                ON operations.operations_aperture_drafts(company_id, owner_user_id, updated_at_utc DESC);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) { }
}
