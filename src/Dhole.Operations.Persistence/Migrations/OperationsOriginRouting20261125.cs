using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Operations.Persistence.Migrations;

[DbContext(typeof(OperationsDbContext))]
[Migration("202611250001_OperationsOriginRouting")]
public sealed class OperationsOriginRouting20261125 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            INSERT INTO operations.operations_assignment_rules
              (id, team_id, department_code, modality_code, origin_country_code, destination_country_code, priority, is_active)
            SELECT gen_random_uuid(), t.id, t.department_code, defaults.modality, defaults.country, NULL, defaults.priority, true
            FROM operations.operations_teams t
            JOIN (VALUES
                ('Origen · Estados Unidos', 'FCL', 'US', 100),
                ('Origen · Estados Unidos', 'LCL', 'US', 100),
                ('Origen · Estados Unidos', 'AIR', 'US', 100),
                ('Origen · FCL internacional', 'FCL', NULL, 50),
                ('Origen · LCL y Aéreo internacional', 'LCL', NULL, 50),
                ('Origen · LCL y Aéreo internacional', 'AIR', NULL, 50)
            ) AS defaults(name, modality, country, priority) ON t.name = defaults.name
            WHERE t.department_code = 'COORDINACION_ORIGEN'
              AND NOT EXISTS (SELECT 1 FROM operations.operations_assignment_rules r WHERE r.team_id = t.id);
            """);
    }
    protected override void Down(MigrationBuilder migrationBuilder) { /* Preserve administrator-editable rules. */ }
}
