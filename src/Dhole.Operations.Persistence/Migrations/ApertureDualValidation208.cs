using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace Dhole.Operations.Persistence.Migrations;

[DbContext(typeof(OperationsDbContext))]
[Migration("202611180100_ApertureDualValidation208")]
public sealed class ApertureDualValidation208 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE operations.operations_aperture_validations (
                id uuid PRIMARY KEY,
                case_stage_id uuid NOT NULL REFERENCES operations.operations_case_stages(id) ON DELETE CASCADE,
                team_id uuid NOT NULL REFERENCES operations.operations_teams(id) ON DELETE RESTRICT,
                user_id uuid NOT NULL,
                slot smallint NOT NULL CHECK (slot IN (1, 2)),
                status varchar(20) NOT NULL CHECK (status IN ('PENDING', 'APPROVED', 'REJECTED')),
                assigned_by uuid NOT NULL,
                assigned_at_utc timestamptz NOT NULL,
                reviewed_at_utc timestamptz NULL,
                comment varchar(2000) NULL,
                CONSTRAINT uq_operations_validation_stage_slot UNIQUE (case_stage_id,slot),
                CONSTRAINT uq_operations_validation_stage_user UNIQUE (case_stage_id,user_id)
            );
            CREATE INDEX ix_operations_validation_user_status ON operations.operations_aperture_validations(user_id,status);
            CREATE TABLE operations.operations_team_audit_entries (
                id uuid PRIMARY KEY,
                team_id uuid NOT NULL REFERENCES operations.operations_teams(id) ON DELETE RESTRICT,
                event_id uuid NOT NULL UNIQUE,
                actor_user_id uuid NOT NULL,
                action_code varchar(100) NOT NULL,
                before_json jsonb NULL,
                after_json jsonb NULL,
                occurred_at_utc timestamptz NOT NULL
            );
            CREATE INDEX ix_operations_team_audit_team ON operations.operations_team_audit_entries(team_id,occurred_at_utc DESC);
        """);
    }
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Do not delete audit or dual validation history automatically.");
}
