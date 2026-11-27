using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Operations.Persistence.Migrations;

[DbContext(typeof(OperationsDbContext))]
[Migration("202611260002_OperationsTeamResponsibilities")]
public sealed class OperationsTeamResponsibilities20261126 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Null retains the original team-scoped operator/supervisor semantics.
        migrationBuilder.Sql("""
            ALTER TABLE operations.operations_team_members
            ADD COLUMN IF NOT EXISTS role_code varchar(30) NULL;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE operations.operations_team_members DROP COLUMN IF EXISTS role_code;
            """);
    }
}
