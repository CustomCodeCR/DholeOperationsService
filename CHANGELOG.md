## [0.4.1] - 2026-11-17 (DH-186 · Alineación con DholeConfigService)

- PATCH: corrige el esquema Operations de public a operations sin copiar ni perder registros, FKs o índices.
- Adopta entidades y configuraciones oficiales de Outbox/Inbox de CustomCodeFramework como DholeConfigService; conserva eventos pendientes, recibos y payload para SignalR.
- Redis Streams, workers, endpoints y scripts SQL actualizados; migración transaccional y rollback destructivo impedido.
- Operations 0.4.0 → 0.4.1; ecosistema 1.69.0 → 1.69.1; staging verificado con 18 tablas en operations y 6 pruebas PostgreSQL.

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
