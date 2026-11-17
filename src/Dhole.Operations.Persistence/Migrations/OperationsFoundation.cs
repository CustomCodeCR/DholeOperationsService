using Dhole.Operations.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Operations.Persistence.Migrations;

/// <summary>Non-destructive foundation: retains legacy operations_outbox / operations_inbox.</summary>
[DbContext(typeof(OperationsDbContext))]
[Migration("202611162310_OperationsFoundation")]
public sealed class OperationsFoundation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE operations_cases (
                id uuid PRIMARY KEY,
                company_id uuid NOT NULL,
                idtra varchar(32) NOT NULL UNIQUE,
                series_code varchar(5) NOT NULL,
                sequence_number bigint NOT NULL,
                account_id uuid NOT NULL,
                executive_user_id uuid NOT NULL,
                source_quote_id uuid NULL,
                source_quote_option_id uuid NULL,
                aperture_type varchar(20) NOT NULL,
                modality_code varchar(30) NOT NULL,
                status varchar(40) NOT NULL,
                priority smallint NOT NULL,
                complexity_points numeric(10,2) NOT NULL,
                created_by uuid NOT NULL,
                created_at_utc timestamptz NOT NULL,
                updated_at_utc timestamptz NOT NULL,
                version bigint NOT NULL,
                CONSTRAINT ck_operations_case_points CHECK (complexity_points > 0 AND priority BETWEEN 1 AND 5)
            );
            CREATE INDEX ix_operations_cases_company_status_updated ON operations_cases(company_id, status, updated_at_utc);
            CREATE INDEX ix_operations_cases_account ON operations_cases(account_id);
            CREATE INDEX ix_operations_cases_quote ON operations_cases(source_quote_id);
            CREATE INDEX ix_operations_cases_executive ON operations_cases(executive_user_id);

            CREATE TABLE operations_case_sequences (
                id uuid PRIMARY KEY,
                company_id uuid NOT NULL,
                series_code varchar(5) NOT NULL,
                last_number bigint NOT NULL,
                updated_at_utc timestamptz NOT NULL,
                UNIQUE(company_id, series_code)
            );
            CREATE TABLE operations_teams (
                id uuid PRIMARY KEY,
                department_code varchar(30) NOT NULL,
                name varchar(150) NOT NULL,
                is_active boolean NOT NULL,
                UNIQUE(department_code, name)
            );
            CREATE TABLE operations_team_members (
                id uuid PRIMARY KEY,
                team_id uuid NOT NULL REFERENCES operations_teams(id) ON DELETE CASCADE,
                user_id uuid NOT NULL,
                is_supervisor boolean NOT NULL,
                is_available boolean NOT NULL,
                capacity_points numeric(10,2) NOT NULL,
                CONSTRAINT ck_operations_member_capacity CHECK (capacity_points > 0),
                UNIQUE(team_id, user_id)
            );
            CREATE TABLE operations_assignment_rules (
                id uuid PRIMARY KEY,
                team_id uuid NOT NULL REFERENCES operations_teams(id) ON DELETE RESTRICT,
                department_code varchar(30) NOT NULL,
                modality_code varchar(30) NULL,
                origin_country_code varchar(2) NULL,
                destination_country_code varchar(2) NULL,
                priority integer NOT NULL,
                is_active boolean NOT NULL
            );
            CREATE INDEX ix_operations_rules_selection ON operations_assignment_rules(department_code, is_active, priority);
            CREATE TABLE operations_case_stages (
                id uuid PRIMARY KEY,
                case_id uuid NOT NULL REFERENCES operations_cases(id) ON DELETE CASCADE,
                department_code varchar(30) NOT NULL,
                status varchar(30) NOT NULL,
                team_id uuid NULL,
                started_at_utc timestamptz NULL,
                completed_at_utc timestamptz NULL,
                version bigint NOT NULL,
                UNIQUE(case_id, department_code)
            );
            CREATE INDEX ix_operations_stages_status ON operations_case_stages(department_code, status);
            CREATE TABLE operations_stage_assignments (
                id uuid PRIMARY KEY,
                case_stage_id uuid NOT NULL REFERENCES operations_case_stages(id) ON DELETE CASCADE,
                team_id uuid NOT NULL,
                user_id uuid NOT NULL,
                assigned_points numeric(10,2) NOT NULL,
                assigned_by uuid NOT NULL,
                assigned_at_utc timestamptz NOT NULL,
                released_at_utc timestamptz NULL
            );
            CREATE UNIQUE INDEX ix_operations_assignment_active ON operations_stage_assignments(case_stage_id) WHERE released_at_utc IS NULL;
            CREATE INDEX ix_operations_assignment_user ON operations_stage_assignments(user_id, released_at_utc);

            CREATE TABLE operations_documents (
                id uuid PRIMARY KEY,
                case_id uuid NOT NULL REFERENCES operations_cases(id) ON DELETE CASCADE,
                document_type_code varchar(60) NOT NULL,
                status varchar(30) NOT NULL,
                created_at_utc timestamptz NOT NULL
            );
            CREATE INDEX ix_operations_documents_type ON operations_documents(case_id, document_type_code);
            CREATE TABLE operations_document_versions (
                id uuid PRIMARY KEY,
                document_id uuid NOT NULL REFERENCES operations_documents(id) ON DELETE CASCADE,
                version_number integer NOT NULL,
                storage_file_id uuid NOT NULL UNIQUE,
                file_name varchar(255) NOT NULL,
                mime_type varchar(150) NOT NULL,
                sha256 varchar(64) NULL,
                uploaded_by uuid NOT NULL,
                uploaded_at_utc timestamptz NOT NULL,
                review_status varchar(30) NOT NULL,
                UNIQUE(document_id, version_number)
            );
            CREATE TABLE operations_document_extraction_jobs (
                id uuid PRIMARY KEY,
                document_version_id uuid NOT NULL REFERENCES operations_document_versions(id) ON DELETE RESTRICT,
                external_extraction_job_id uuid NULL,
                status varchar(30) NOT NULL,
                extracted_json jsonb NULL,
                validation_errors_json jsonb NULL,
                requested_by uuid NOT NULL,
                reviewed_by uuid NULL,
                requested_at_utc timestamptz NOT NULL,
                reviewed_at_utc timestamptz NULL
            );
            CREATE INDEX ix_operations_extraction_version ON operations_document_extraction_jobs(document_version_id, requested_at_utc);
            CREATE INDEX ix_operations_extraction_status ON operations_document_extraction_jobs(status, requested_at_utc);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TABLE operations_document_extraction_jobs;
            DROP TABLE operations_document_versions;
            DROP TABLE operations_documents;
            DROP TABLE operations_stage_assignments;
            DROP TABLE operations_case_stages;
            DROP TABLE operations_assignment_rules;
            DROP TABLE operations_team_members;
            DROP TABLE operations_teams;
            DROP TABLE operations_case_sequences;
            DROP TABLE operations_cases;
            """);
    }
}
