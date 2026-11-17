#!/usr/bin/env bash
# DH-184: create the Operations database only if missing; no DROP and no data reset.
set -euo pipefail
: "${DHOLE_ENV_FILE:?DHOLE_ENV_FILE must point to a prepared environment file}"
: "${COMPOSE_PROJECT:?COMPOSE_PROJECT must identify staging or production}"
test -r "$DHOLE_ENV_FILE"
read_key() { grep -m1 "^$1=" "$DHOLE_ENV_FILE" | cut -d= -f2- | tr -d '\r' || true; }
db_name="$(read_key OPERATIONS_DB)"
db_name="${db_name:-dhole_operations}"
db_user="$(read_key POSTGRES_USER)"
if [[ ! "$db_name" =~ ^[a-z][a-z0-9_]{0,62}$ ]]; then
  echo "Invalid OPERATIONS_DB: expected a lowercase PostgreSQL identifier" >&2
  exit 1
fi
test -n "$db_user" || { echo "POSTGRES_USER is missing" >&2; exit 1; }
case "$COMPOSE_PROJECT" in
  dhole)
    pg_container="postgres"
    ;;
  dhole-staging)
    pg_container="$(docker ps -q --filter 'label=com.docker.compose.project=dhole-staging-infra' --filter 'label=com.docker.compose.service=postgres' | head -n1)"
    test -n "$pg_container" || { echo "Staging PostgreSQL container not found; refusing to use production" >&2; exit 1; }
    ;;
  *)
    echo "Unsupported COMPOSE_PROJECT; refusing cross-environment PostgreSQL access" >&2
    exit 1
    ;;
esac
docker exec "$pg_container" pg_isready -U "$db_user" >/dev/null
exists="$(docker exec "$pg_container" psql -X -A -t -v ON_ERROR_STOP=1 -U "$db_user" -d postgres -c "SELECT 1 FROM pg_database WHERE datname = '$db_name'")"
if [[ "$exists" != "1" ]]; then
  docker exec "$pg_container" createdb -U "$db_user" "$db_name"
  echo "Created Operations PostgreSQL database: $db_name ($COMPOSE_PROJECT)"
else
  echo "Operations PostgreSQL database already exists: $db_name ($COMPOSE_PROJECT)"
fi
docker exec "$pg_container" psql -X -A -t -v ON_ERROR_STOP=1 -U "$db_user" -d "$db_name" -c "SELECT 1" | grep -qx 1
