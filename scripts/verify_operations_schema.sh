#!/usr/bin/env bash
# Assert actual Operations SQL tables and migration history on the selected PostgreSQL environment.
set -euo pipefail
: "${DHOLE_ENV_FILE:?}"
: "${COMPOSE_PROJECT:?}"
test -r "$DHOLE_ENV_FILE"
read_key() { grep -m1 "^$1=" "$DHOLE_ENV_FILE" | cut -d= -f2- | tr -d '\r' || true; }
db="$(read_key OPERATIONS_DB)"
db="${db:-dhole_operations}"
user="$(read_key POSTGRES_USER)"
[[ "$db" =~ ^[a-z][a-z0-9_]{0,62}$ ]] || exit 1
test -n "$user"
case "$COMPOSE_PROJECT" in
  dhole) container="postgres";;
  dhole-staging)
    container="$(docker ps -q --filter 'label=com.docker.compose.project=dhole-staging-infra' --filter 'label=com.docker.compose.service=postgres' | head -n1)"
    test -n "$container" || exit 1
    ;;
  *) echo "Unsupported PostgreSQL target" >&2; exit 1;;
esac
sql="SELECT count(*) FROM information_schema.tables WHERE table_schema = 'operations'
AND table_name IN (
'operations_cases','operations_case_sequences','operations_case_stages',
'operations_teams','operations_team_members','operations_assignment_rules',
'operations_stage_assignments','operations_documents','operations_document_versions',
'operations_document_extraction_jobs','outbox_messages','inbox_messages',
'operations_case_quote_links','operations_case_quote_costs','operations_case_milestones',
'operations_case_tasks','operations_case_notes','operations_case_audit_entries');"
count="$(docker exec "$container" psql -X -A -t -v ON_ERROR_STOP=1 -U "$user" -d "$db" -c "$sql" | tr -d '\r')"
if [[ "$count" != "18" ]]; then
  echo "Operations schema incomplete: found $count of 18 expected tables ($COMPOSE_PROJECT)" >&2
  exit 1
fi
applied="$(docker exec "$container" psql -X -A -t -v ON_ERROR_STOP=1 -U "$user" -d "$db" -c \
  'SELECT count(*) FROM "__EFMigrationsHistory" WHERE "MigrationId" = '\''202611170310_OperationsFirstTables'\'';' | tr -d '\r')"
test "$applied" = "1" || { echo "Operations first-table migration not recorded" >&2; exit 1; }

schema_applied="$(docker exec "$container" psql -X -A -t -v ON_ERROR_STOP=1 -U "$user" -d "$db" -c \
  'SELECT count(*) FROM "__EFMigrationsHistory" WHERE "MigrationId" = '\''202611170420_ConfigSchemaAlignment'\'';' | tr -d '\r')"
test "$schema_applied" = "1" || { echo "Config-style schema migration DH-186 not recorded" >&2; exit 1; }
legacy="$(docker exec "$container" psql -X -A -t -v ON_ERROR_STOP=1 -U "$user" -d "$db" -c \
  "SELECT count(*) FROM information_schema.tables WHERE table_schema='public' AND table_name LIKE 'operations_%';" | tr -d '\r')"
test "$legacy" = "0" || { echo "Legacy public operations tables still present: $legacy" >&2; exit 1; }
contract="$(docker exec "$container" psql -X -A -t -v ON_ERROR_STOP=1 -U "$user" -d "$db" -c \
  "SELECT count(*) FROM information_schema.columns WHERE table_schema='operations' AND ((table_name='outbox_messages' AND column_name IN ('event_id','event_type','source_service','payload_json','status','created_at')) OR (table_name='inbox_messages' AND column_name IN ('id','event_id','event_type','source_service','consumer_service','status','created_at','payload_json')));" | tr -d '\r')"
test "$contract" = "14" || { echo "Operations framework messaging contract mismatch" >&2; exit 1; }
echo "Operations schema verified: 18 tables under operations, Config-compatible inbox/outbox and DH-186 migration ($COMPOSE_PROJECT)"
