## [0.7.0] - 2026-11-17 (DH-20261117-OPS-DUAL-VALIDATION-208 · Equipos y doble validación Aperturas)

- MINOR: pool por equipo, 2 revisores distintos, decisiones individuales, cola inicial y auditoría persistente PostgreSQL y AuditLogs por Outbox/Streams.
- Auth verifica usuarios internos activos antes de incorporarlos; los scopes y los usuarios se conceden manualmente.
- Operations 0.6.0 → 0.7.0; global 1.77.1 → 1.78.0. Despliegue producción NO autorizado.

## [0.6.0] - 2026-11-17 (DH-20261117-OPS-FOUNDATION-205 · Preparación Operations v1.2)

- MINOR: registro seguro de conexiones y capacidades marítimas, sin activar llamadas externas o credenciales no verificadas.
- Nueva persistencia de estado Origen, confirmación idempotente de salida y tránsito con Outbox, y notas internas restringidas a equipo/supervisión.
- No modifica Pricing/CRM/SRM ni crea usuarios; prepara contratos y documenta dependencias verificadas y bloqueos del MVP.
- Operations 0.5.0 → 0.6.0; ecosistema 1.75.0 → 1.76.0. CI/staging pendientes, sin producción autorizada.

## [0.5.0] - 2026-11-17 (Responsables operativos desde CRM)

- MINOR DH-20261117-CRM-OPS-CONTACTS-204: consulta GET /api/operations/cases/{id}/operations-contacts, que valida permisos sobre expediente y consulta al CRM los encargados activos de la cuenta (sin replicar datos personales).
- Utiliza Bearer de usuario y permisos de CRM. Requiere Crm__ApiBaseUrl configurado en API de Operations; fail-closed 503 cuando CRM no está disponible. Se conserva SignalR/outbox y los endpoints REST existentes.
- Operations 0.4.1 → 0.5.0; ecosistema 1.73.0 → 1.74.0. Pendientes CI/staging antes de master; sin despliegue de producción.

## [0.4.1] - 2026-11-17 (DH-186 · Alineación con DholeConfigService)

- PATCH: corrige el esquema de Operations de public a operations sin copiar ni perder expedientes, proveedores, QUO, documentos, índices o claves foráneas.
- Sustituye la mensajería casera por OutboxMessage/InboxMessage y sus configuraciones oficiales de CustomCodeFramework como DholeConfigService; tablas operations.outbox_messages y operations.inbox_messages, preservando event ID, estatus y payload de replay.
- Redis Streams, consumidores, endpoints REST, SignalR y scripts SQL utilizan la nueva estructura. Migración aditiva transaccional verificable; no se autoriza rollback destructivo.
- Operations 0.4.0 → 0.4.1; global 1.69.0 → 1.69.1.

## [0.4.0] - 2026-11-17 (DH-185 · Primeras tablas de operaciones)

- MINOR: seis tablas relacionales adicionales para QUO y costos/proveedores referenciados, hitos, tareas, notas y auditoría; se mantienen las doce tablas anteriores y sus registros.
- Migración EF Core aditiva transaccional, claves foráneas, índices, índices parciales de unicidad y columnas `jsonb` para auditoría. Sin migraciones destructivas.
- CI de PostgreSQL valida inserción, relaciones, importe decimal, QUO principal único y reejecución segura; staging/producción verifican 18 tablas e historial EF antes de aprobar el deploy.
- DholeOperationsService 0.3.1 → 0.4.0, ecosistema 1.68.0 → 1.69.0. Datos de Pricing aún no importados automáticamente.

## [0.3.1] - 2026-11-17 (DH-184 · Operations PostgreSQL bootstrap + gateway readiness)

- PATCH: provisionamiento idempotente y no destructivo de dhole_operations en staging y producción antes de arrancar API y workers; bloquea nombres SQL inválidos y valida PostgreSQL.
- /health y /ready entregan versión real del ensamblado para monitoreo sin confundir la versión registrada con la desplegada.
- CI y pruebas de bootstrap; ecosistema Dhole 1.67.0 → 1.68.0.

## [0.3.0] - 2026-11-16 (DH-183 · Operations backend foundation)

- MINOR: operaciones manuales con IDTRA transaccional y primer estado de Aperturas, arquitectura por capas análoga a Config.
- Pool de equipos multiempresa, capacidad y asignación automática ponderada; bloqueos PostgreSQL y auditoría de eventos duraderos.
- Documento/versión/Storage y cola de extracción preparada; la ejecución del parser DataExtraction/AI y validación humana queda para la fase contractual.
- SignalR por expediente autorizado y replay REST sanitizado; no se difunden datos comerciales a Clients.All.
- Migración aditiva preservando operations_outbox/inbox, pruebas iniciales de validación y versión global Dhole 1.67.0.
- Candidato en rama intermedia: dotnet build/test, migraciones y staging pendientes; sin promoción ni despliegue a producción.


## [0.2.0] - 2026-11-14 (API Manager discovery)

- MINOR: inventario de rutas Operations solo para integraciones autenticadas; conserva idempotencia de eventos y SignalR.
- Global Dhole 1.56.0.

# Changelog

## [0.1.2] - 2026-11-13

- PATCH DH-20261113-OPERATIONS-NO-FLAGS-128: elimina la condición por variable `DHOLE_OPERATIONS_PRODUCTION_ENABLED` que omitía el despliegue de master.
- Staging (`develop`) y producción (`master`) ejecutan build, deploy y comprobación de salud automáticamente, como DholeConfigService, sin habilitación manual.
- Añade CI de regresión que rechaza flags de despliegue en Operations. Mantiene REST, SignalR, Redis Streams y Outbox/Inbox; versión 0.1.1 → 0.1.2, ecosistema 1.46.2 → 1.46.3.
- El despliegue de producción procede solamente tras promoción autorizada del release a master.


## [0.1.1] - 2026-11-12

- PATCH DH-20261112-OPERATIONS-STAGING-127: habilita el workflow automático de staging al integrar en `develop`, igual que DholeConfigService; el despliegue de producción permanece sujeto a la compuerta de autorización.
- Sin cambios de contratos públicos, Outbox, Inbox, SignalR ni REST. Componente 0.1.0 → 0.1.1 y ecosistema 1.46.1 → 1.46.2.
- Requiere validación de infraestructura y prueba de readiness en staging antes de promover a `master`.


## [0.1.0] - 2026-11-12

- Inicializa DholeOperationsService a partir de la arquitectura de DholeConfigService (.NET 10), con proyectos por capas y pruebas.
- Configura Dockerfiles, CI y workflows de despliegue independientes para develop/staging y master/producción, con aprobación de entorno.
- Añade cimientos realtime: SignalR, Redis Streams, persistencia Outbox/Inbox, idempotencia y replay REST.
- Versión del ecosistema 1.46.1; componente 0.1.0. Versión inicial candidata: CI y staging pendientes; producción no autorizada.
