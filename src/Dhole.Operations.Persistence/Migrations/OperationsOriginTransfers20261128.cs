using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Operations.Persistence.Migrations;

[DbContext(typeof(OperationsDbContext))]
[Migration("202611280006_OperationsOriginTransfers")]
public sealed class OperationsOriginTransfers20261128 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE operations.operations_origin_transfers (
            id uuid PRIMARY KEY,
            request_id uuid NOT NULL UNIQUE,
            case_id uuid NOT NULL REFERENCES operations.operations_cases(id) ON DELETE RESTRICT,
            origin_stage_id uuid NOT NULL UNIQUE REFERENCES operations.operations_case_stages(id) ON DELETE RESTRICT,
            transit_stage_id uuid NOT NULL UNIQUE REFERENCES operations.operations_case_stages(id) ON DELETE RESTRICT,
            actor_user_id uuid NOT NULL,
            expected_case_version bigint NOT NULL,
            expected_stage_version bigint NOT NULL,
            result_case_version bigint NOT NULL,
            rule_id uuid NOT NULL,
            rule_version integer NOT NULL,
            rule_fingerprint varchar(64) NOT NULL,
            rule_snapshot_json jsonb NOT NULL,
            checklist_json jsonb NOT NULL,
            transferred_at_utc timestamptz NOT NULL,
            CONSTRAINT ck_operations_origin_transfer_versions CHECK (
                expected_case_version > 0 AND expected_stage_version > 0 AND result_case_version > expected_case_version AND rule_version > 0),
            CONSTRAINT ck_operations_origin_transfer_stages CHECK (origin_stage_id <> transit_stage_id)
        );
        CREATE INDEX ix_operations_origin_transfers_case_id ON operations.operations_origin_transfers(case_id);
        """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Transfer receipts are retained; use a reviewed rollback.");
}
