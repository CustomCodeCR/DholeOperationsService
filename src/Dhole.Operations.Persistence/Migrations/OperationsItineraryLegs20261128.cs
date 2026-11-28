using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Operations.Persistence.Migrations;

[DbContext(typeof(OperationsDbContext))]
[Migration("202611280007_OperationsItineraryLegs")]
public sealed class OperationsItineraryLegs20261128 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE operations.operations_case_stages
            ADD CONSTRAINT ak_operations_stage_case UNIQUE (id, case_id);
        ALTER TABLE operations.operations_case_service_lines
            ADD CONSTRAINT ak_operations_service_case UNIQUE (id, case_id);
        CREATE TABLE operations.operations_itinerary_legs (
            id uuid PRIMARY KEY,
            case_id uuid NOT NULL REFERENCES operations.operations_cases(id) ON DELETE RESTRICT,
            plan_request_id uuid NOT NULL,
            sequence integer NOT NULL,
            transport_code varchar(20) NOT NULL,
            from_location_id uuid NOT NULL,
            to_location_id uuid NOT NULL,
            from_label varchar(250) NOT NULL,
            to_label varchar(250) NOT NULL,
            from_country_code varchar(2) NOT NULL,
            to_country_code varchar(2) NOT NULL,
            transit_stage_id uuid NOT NULL,
            destination_stage_id uuid NULL,
            destination_kind varchar(20) NOT NULL,
            destination_service_line_id uuid NULL,
            status varchar(30) NOT NULL,
            srm_provider_id uuid NULL,
            planned_departure_at_utc timestamptz NULL,
            planned_arrival_at_utc timestamptz NULL,
            estimated_departure_at_utc timestamptz NULL,
            estimated_arrival_at_utc timestamptz NULL,
            estimated_departure_offset_minutes smallint NULL,
            estimated_arrival_offset_minutes smallint NULL,
            estimated_departure_source varchar(30) NULL,
            estimated_arrival_source varchar(30) NULL,
            actual_departure_at_utc timestamptz NULL,
            actual_arrival_at_utc timestamptz NULL,
            created_by uuid NOT NULL,
            created_at_utc timestamptz NOT NULL,
            version bigint NOT NULL,
            UNIQUE (case_id, sequence),
            FOREIGN KEY (transit_stage_id, case_id) REFERENCES operations.operations_case_stages(id, case_id) ON DELETE RESTRICT,
            FOREIGN KEY (destination_stage_id, case_id) REFERENCES operations.operations_case_stages(id, case_id) ON DELETE RESTRICT,
            FOREIGN KEY (destination_service_line_id, case_id) REFERENCES operations.operations_case_service_lines(id, case_id) ON DELETE RESTRICT,
            CHECK (sequence BETWEEN 1 AND 20 AND version > 0),
            CHECK (from_location_id <> to_location_id),
            CHECK (transport_code IN ('MARITIME','ROAD','AIR')),
            CHECK (destination_kind IN ('NONE','FINAL','ENTRY_TRANSFER')),
            CHECK ((destination_kind = 'NONE' AND destination_stage_id IS NULL AND destination_service_line_id IS NULL)
                OR (destination_kind <> 'NONE' AND destination_stage_id IS NOT NULL AND destination_stage_id <> transit_stage_id)),
            CHECK (destination_kind <> 'ENTRY_TRANSFER' OR destination_service_line_id IS NOT NULL)
        );
        CREATE INDEX ix_operations_itinerary_plan ON operations.operations_itinerary_legs(plan_request_id);
        CREATE INDEX ix_operations_itinerary_transit ON operations.operations_itinerary_legs(transit_stage_id, case_id);
        CREATE INDEX ix_operations_itinerary_destination ON operations.operations_itinerary_legs(destination_stage_id, case_id);
        CREATE INDEX ix_operations_itinerary_service ON operations.operations_itinerary_legs(destination_service_line_id, case_id);
        """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Persisted itinerary identity must be retained; use a reviewed rollback.");
}
