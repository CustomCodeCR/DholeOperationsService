## [0.13.0] - 2026-11-20 (DH-20261120-OPERATIONS-APERTURES-330 · Bandeja de Aperturas y Trámites)

- MINOR: filtros paginados en backend para IDTRA, estado y origen manual/automático en Trámites y cola de Aperturas, respetando empresa, scopes y asignaciones.
- Dashboard: indicadores separados para aperturas manuales y expedientes recibidos desde Pricing, con conteos realizados en PostgreSQL sin recortar a la página actual.
- GET /api/operations/apertures/create-context resuelve empresa desde el JWT y membresías actuales; el ejecutivo de apertura manual sigue resolviéndose desde CRM y no se suplanta por un GUID ficticio.
- Conserva arquitectura REST y eventos Outbox/Inbox/SignalR existentes. El evento de aceptación comercial definitivo y su consumidor automático continúan pendientes: el aviso por correo de Pricing no es evento de creación de expediente.
- Operations 0.12.0 → 0.13.0; ecosistema candidato 1.105.0; CI, pruebas de migración, staging y despliegue de producción pendientes.

## [0.12.0] - 2026-11-19 (DH-20261119-OPS-ROLE-TEAM-DASHBOARD-254 · Dashboard Aperturas por equipo)

- MINOR: endpoint autorizado GET /api/operations/dashboard, totalizados por permiso de expediente y equipo, con validaciones y últimos 30 casos.
- Filtrado PostgreSQL por usuario, empresa y rol; se rechaza teamId no permitido con 403. Conserva REST y SignalR.
- Promoción selectiva desde develop validado en staging, Operations 0.11.0 → 0.12.0; versión global 1.96.0. Sin incluir otros cambios de develop.

## [0.11.0] - 2026-11-19 (DH-20261119-OPS-TEAM-BULK-251 · Selección múltiple de equipos)

- MINOR: nuevo endpoint POST /api/operations/teams/{teamId}/members/bulk, con validación de todos los usuarios mediante Auth y una única transacción de persistencia con auditoría.
- Asignación idempotente: no reinicia los integrantes existentes; rechaza lotes repetidos, vacíos o inválidos.
- La pertenencia a Aperturas exige el rol autorizado; las decisiones de validación conservan la verificación separada de scopes de revisión.
- DholeOperationsService 0.10.0 → 0.11.0; ecosistema 1.94.0. Release coordinado con Auth, Operations, Web y System. CI y staging pendientes.

## [0.10.0] - 2026-11-19 (DH-20261119-OPS-AUDIT-ROLE-247 · Roles permitidos de Operations)

- MINOR: verifica la elegibilidad por rol devuelta por Auth antes de permitir altas/modificaciones de integrantes y la doble validación de Aperturas. Solo Operador Logístico o Supervisor Operativo son asignables.
- Conserva REST, SignalR, outbox/inbox, ACL y auditoría; DholeOperationsService 0.9.0→0.10.0, ecosistema 1.90.0→1.91.0. Release coordinado con Auth, Web y System.

## [0.9.0] - 2026-11-19 (DH-20261119-OPERATIONS-SCOPES-CONTEXT-246 · Auth scopes y contexto empresarial Operations)

- MINOR: corrige el 403 de Teams/initialize cuando Auth emite JWT de usuario interno sin company_id; selecciona únicamente la empresa operativa existente si es única.
- En un entorno sin empresas, solo SuperUsuario puede inicializar un espacio operativo estable; se serializa con advisory lock y se conservan historial AuditLogs, Outbox y los permisos requeridos.
- En instalaciones multiempresa ambiguas devuelve 409 y no mezcla tenants. La resolución segura se aplica a equipos, integrantes, auditoría y Aperturas.
- DholeOperationsService 0.8.0 → 0.9.0; ecosistema 1.89.0 → 1.90.0. Release coordinado con DholeAuthService. CI/staging pendientes; producción no autorizada.

## [0.8.0] - 2026-11-18 (DH-20261118-OPERATIONS-TEAM-UX-241 · catálogo de equipos estándar)

- MINOR: inicializador idempotente por empresa de diez equipos operativos, sin altas de personas, membresías, roles ni permisos automáticos.
- Configura reglas iniciales de Aperturas y pool de Origen USA FCL/LCL/AIR, sin reglas internacionales ambiguas. Respeta reglas ya creadas y serializa concurrencia por empresa.
- Auth continúa validando elegibilidad para cada integrante. Cada equipo/regla nueva genera historial auditado, Outbox duradero y señal de inicialización para la arquitectura realtime.
- Operations 0.7.0 → 0.8.0; DholeWeb 1.84.0 → 1.85.0; ecosistema 1.89.0. CI/staging pendientes; no despliegue de producción autorizado.

## [0.7.0] - 2026-11-17 (DH-20261117-OPS-DUAL-VALIDATION-208 · Equipos y doble validación Aperturas)

- MINOR: pool por equipo, 2 revisores distintos, decisiones individuales, cola inicial y auditoría persistente PostgreSQL y AuditLogs por Outbox/Streams.
- Auth verifica usuarios internos activos antes de incorporarlos; los scopes y los usuarios se conceden manualmente.
- Operations 0.6.0 → 0.7.0; global 1.77.1 → 1.78.0. Despliegue producción NO autorizado.

## [0.6.0] - 2026-11-17 (DH-20261117-OPS-FOUNDATION-205 · Preparación backend Operations v1.2)

- MINOR: registro de conexiones multicarrier de estado REQUESTED sin habilitar APIs externas ni credenciales ficticias.
- Migración aditiva para estado de Origen, salida idempotente a Tránsito mediante Outbox y bitácora interna restringida.
- Resolución del vendedor responsable mediante cuenta CRM autenticada, sin tomar el ejecutivo proporcionado por el cliente como fuente de verdad.
- Operations 0.5.0 → 0.6.0; release validado en CI y staging. Promoción coordinada a master por autorización expresa.

## [0.5.0] - 2026-11-17 (Responsables operativos desde CRM)

- MINOR DH-20261117-CRM-OPS-CONTACTS-204: consulta GET /api/operations/cases/{id}/operations-contacts, que valida permisos sobre expediente y consulta al CRM los encargados activos de la cuenta (sin replicar datos personales).
- Utiliza Bearer de usuario y permisos de CRM. Requiere Crm__ApiBaseUrl configurado en API de Operations; fail-closed 503 cuando CRM no está disponible. Se conserva SignalR/outbox y los endpoints REST existentes.
- Operations 0.4.1 → 0.5.0; ecosistema 1.73.0 → 1.74.0. Pendientes CI/staging antes de master; sin despliegue de producción.

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
