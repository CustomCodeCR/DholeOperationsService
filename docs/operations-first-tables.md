# DH-185 — Primera capa de tablas de Operations

Base: `dhole_operations`, **aislada** en PostgreSQL staging y producción.

## Tablas ya existentes (DH-183)

`operations_cases`, `operations_case_sequences`, `operations_case_stages`,
`operations_teams`, `operations_team_members`, `operations_assignment_rules`,
`operations_stage_assignments`, `operations_documents`,
`operations_document_versions`, `operations_document_extraction_jobs`,
`operations_outbox`, `operations_inbox`.

## Nuevas tablas (DH-185)

| Tabla | Uso | Integridad |
| --- | --- | --- |
| operations_case_quote_links | Vínculo QUO y alternativa aceptada | FK case, único primario por caso |
| operations_case_quote_costs | Snapshot informativo de costos y proveedor | FK vínculo, fuente de costo idempotente |
| operations_case_milestones | ETD/ETA y eventos planificados/reales | FK case y etapa opcional |
| operations_case_tasks | Pendientes del equipo con fechas y responsable | FK case y etapa; prioridad 1–5 |
| operations_case_notes | Minutas y observaciones, sin perder trazabilidad | FK case y etapa |
| operations_case_audit_entries | Auditoría de acciones y transiciones | event_id único; contenido jsonb |

**Sin datos ficticios, sin replicación automática de Pricing aún.** Las referencias CRM/QUO/SRM
permanecen IDs de otros servicios, sin FK entre bases independientes. Los importes capturados
son snapshots y no modifican cotizaciones comerciales. No se crean APIs de edición antes de
definir sus permisos y eventos idempotentes.

El layout de ocho tabs (Resumen, QUO, Coordinación Origen, Documentación, Tránsito,
Panamá, Costa Rica y Finanzas) **no** se confunde con etapas departamentales: su acceso
se implementará de forma explícita con Auth/scopes y equipos.

La migración `202611170310_OperationsFirstTables` es aditiva y se ejecuta
transaccionalmente con EF Core al arrancar API/Workers. `Down` destructivo está bloqueado.
Los workflows validan la presencia de tablas e historial de migración tras `/ready`.
