# DH-186 — Operations schema and messaging alignment with DholeConfigService

DholeConfigService establishes `config` with `modelBuilder.HasDefaultSchema("config")` and uses
`CustomCodeFramework.Postgres.EntityFramework.Configurations.OutboxMessageConfiguration` /
`InboxMessageConfiguration`. This release brings Operations to the same architecture:

- `OperationsDbContext : AppDbContextBase`, default schema `operations`.
- `operations.outbox_messages` and `operations.inbox_messages` use the shared
  `CustomCodeFramework.Messaging` entities and configurations.
- Inbox has one extra `payload_json` shadow field for authorized SignalR replay.
- The PostgreSQL migration uses `ALTER TABLE ... SET SCHEMA` on all 18 existing tables.
  PostgreSQL preserves row values, FKs, unique constraints, indexes and OIDs.
- The existing `public."__EFMigrationsHistory"` is intentionally retained,
  so historical EF migrations remain recorded. No duplicate history and no re-run.
- Original `operations_outbox` rows retain their `id` (also `event_id`);
  queued statuses remain Pending, already published remain Processed.
- Original `operations_inbox` receipts retain `event_id`, consumer and payload.
  Only the surrogate `id` is new, matching Config's inbox primary key.
- Redis stream payload fields and per-case SignalR stay compatible.
- Safety: existing target `operations` tables or missing public source table
  fail the transaction without silently overwriting or deleting data.

## Verify with PostgreSQL

```sql
SELECT table_schema, table_name
FROM information_schema.tables
WHERE table_schema = 'operations'
ORDER BY table_name;

SELECT "MigrationId" FROM public."__EFMigrationsHistory"
WHERE "MigrationId" = '202611170420_ConfigSchemaAlignment';

SELECT count(*) FROM operations.outbox_messages;
SELECT count(*) FROM operations.inbox_messages;
```

Both deployments invoke `scripts/verify_operations_schema.sh` after startup; it refuses to
mark a release ready unless all 18 tables, both framework messaging contracts, the migration
history and zero legacy `public.operations_*` tables are confirmed.
