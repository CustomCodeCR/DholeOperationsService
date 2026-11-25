# 0.27.0 — candidato de Operations

## [0.27.1] - 2026-11-25
- Permit assigned pending Aperturas reviewers to edit operational opening data without changing the accepted quote.
- Move ETD, ETA and consolidation planning to the stage-authorized Origin endpoint with concurrency, audit and outbox.

## [0.27.0] - 2026-11-25

- Separación del peso estimado de QUO y del peso confirmado por Coordinación Origen, con control de permisos, versión, auditoría y Outbox.
- Normalización de medidas originales en kg, CBM y cm en el backend.
- Justificación requerida en solicitudes de revisión de discrepancias.
- Candidato parcial; pendiente de CI, staging y resto de criterios 13–18.

## [0.26.0] - 2026-11-25

- Reserve and reconcile explicit accepted QUO links on existing manual IDTRA using durable events, preserving the operational record and commercial snapshot separately. CI/staging pending.

## [0.25.3] - 2026-11-25

- Validate and persist selected opening services using Config catalog identities; classify additional transport and cargo fields. CI and staging pending.

## [0.25.2] - 2026-11-25

- Classify booking, BL, container and airline individually and gate aperture approval with server-side field validation.

## [0.23.2] - 2026-11-25

- Enqueue distinct idempotent System and Email notifications in the persisted Origin assignment transaction; use registered Auth email and audit missing addresses. Existing durable realtime events and pool retained. Regression tests added; CI/staging pending.

## [0.23.1] - 2026-11-25

- Add authorized Operations dashboard invalidations over the existing Hub, durable assigned-reviewer System notifications with idempotency and backlog safety, and retry-safe Redis fanout. CI and authenticated staging pending.

## [0.23.0] - 2026-11-24

- Consume canonical Pricing cost values when supplied, preserve historical snapshot compatibility, expose charge basis and stable detail references, and restrict sale/tax/terms to finance permission. Correct catalog Incoterm validation, sea-only terms, land/air carrier applicability, consolidation and positive volume completion. Regression tests added; CI and staging pending.

- Route validated openings to the configured Origin pool using persisted origin country and modality, with geographic specificity before workload and Auth eligibility; repair existing unassigned cases and avoid starvation.

## [0.22.0] - 2026-11-24

- Allow explicitly listed historical IDTRAs to share the same accepted QUO; retain business idempotency for ordinary quotes, global IDTRA uniqueness, distinct immutable snapshots and per-case pool assignment. Add a shared-quote reconciliation task.
- CI and staging pending.

## [0.21.3] - 2026-11-24

- Require a single Aperturas reviewer, select one eligible user by weighted active workload, and release directly to Origin upon approval. Reconcile untouched legacy pairs and preserve recorded approvals and audit history.
- CI and authenticated staging pending.

## [0.21.2] - 2026-11-24

- Assign only the active Aperturas review, rebalance untouched legacy pairs, allocate a distinct second reviewer after the first approval, and scope dashboard and validation decisions to the active assignee.
- Expose accepted Pricing Storage references from immutable snapshots, including existing IDTRAs.
- Candidate: CI and authenticated staging validation pending.

## [0.21.1] - 2026-11-24

- Restrict operational case visibility by current team and personal assignment; route manual openings directly to origin and retain dual validation for automatic openings.

## [0.21.0] - 2026-11-24

- MINOR: comparación de snapshot QUO aceptada contra datos operativos persistidos, normalización de peso/volumen, semáforo, solicitudes por huella de discrepancias con historial independiente y respuesta de Pricing por Inbox. No crea servicios ficticios ni modifica la cotización. Contactos operativos de CRM mediante contrato interno autorizado.

## [0.20.3] - 2026-11-24

- PATCH: recuperar automáticamente aperturas pendientes nuevas e históricas; validar dos operativos disponibles y autorizados en Auth, conservar doble revisión, carga ponderada, bloqueo por empresa, auditoría y Outbox. No reasignar ni aprobar casos existentes. Mantener autorización de replay para validadores.

## [0.20.2] - 2026-11-24

- PATCH: retornar número QUO en listado y proyectar datos comerciales y todos los rubros originales del snapshot, sin exponer JSON ni ventas a usuarios no autorizados. Los rubros proyectados siguen separados del original.

- PATCH: ordenar Trámites por SequenceNumber descendente antes de paginar, incluyendo IDTRA históricos y nuevos; desempate estable, filtros y ACL conservados. Dashboard de aperturas recientes mantiene orden cronológico.

## [0.20.1] - 2026-11-24

- PATCH: no bloquear apertura por rubros históricos incompatibles con el esquema financiero. Preservar snapshot completo, proyectar rubros compatibles (incluidos de cantidad cero) y crear tarea explícita de conciliación para excepciones.

## [0.20.0] - 2026-11-24 · DH-20261124-OPS-HISTORICAL-IDTRA

- MINOR candidato: recuperación de IDTRA histórico numérico único desde snapshot Pricing, preserva identificador original y eleva contador global bajo bloqueo transaccional; conflictos quedan pendientes sin renumerar.
- Configuración administrativa GET/PUT del próximo IDTRA con control de concurrencia, solo incrementos, motivo y auditoría Outbox. Se conserva REST y realtime existente.
- CI y staging pendientes; producción no autorizada.

## [0.19.1] - 2026-11-24 · DH-20261124-OPS-QUOTE-ACCEPTANCE-440

- PATCH: corrige la integración de Pricing aceptada con Operations: configura la URL/clave interna CRM, usa DholeSystem develop en staging y reintenta Inbox pendiente. Si la QUO histórica no tenía accountId, CRM resuelve solo una cuenta activa exacta; nunca inventa cliente ni empresa.
- Versiones coordinadas: Operations 0.19.0 → 0.19.1, CRM 1.44.0 → 1.44.1, Web/System 1.122.2 → 1.122.3. Pricing 1.28.0 y otros 12 componentes permanecen sin modificación.
- CI, staging y aceptación end-to-end pendientes. No se fusiona a master ni se despliega producción.

## [0.19.0] - 2026-11-23 · DH-20261123-OPS-AUTOSAVE-434

- MINOR candidato: borradores manuales PostgreSQL recuperables sin equipo/IDTRA; API segura de versión optimista, reconciliación de Orig en workers.
- Pricing→Operations: aceptación definitiva A/B vía Outbox/Inbox, un IDTRA por QUO padre, snapshot comercial inmutable, servicios, costos/proveedores y múltiples equipos; falla cerrado si CRM/Auth o empresa no se verifican.
- Servicios adicionales generan semáforo amarillo, revisión Pricing, auditoría, avisos realtime y resolución idempotente sin editar la QUO original. Dos validaciones de Aperturas asignan Origen y permiten recuperación.
- Migraciones no destructivas 434/435/436 y pruebas de PostgreSQL agregadas. CI build/test y versionamiento Operations aprobados; Redis cruzado y staging sin verificar.
- Operations 0.18.0 → 0.19.0; Web/System 1.120.11 → 1.122.0; Pricing 1.27.1 → 1.28.0; CRM 1.43.1 → 1.44.0; otros 12 componentes sin cambios.
- Integración bloqueada por CI snapshot EF de Pricing; únicamente rama intermedia, master intacto.

## [0.18.0] - 2026-11-23 · DH-20261123-OPS-APERTURES-P1-416

- MINOR candidate para Aperturas P1: previsualización protegida de completitud por campo con CONFIRMADO, PENDIENTE_PERMITIDO, PENDIENTE_BLOQUEANTE y NO_APLICA; consulta del borrador existente en PostgreSQL.
- Validación de FCA/WHS para todas las modalidades, recolecta con contacto/ventana, cada línea de equipo positiva, IMO/UN y sobrepeso. Peso real permanece pendiente para Coordinación Origen en aperturas manuales.
- IDTRA sin padding obligatorio, con migración no destructiva de conciliación del contador global histórico antes de emitir nuevos números. El contador sigue transaccional y el snapshot comercial de Pricing no se altera.
- Nuevas pruebas unitarias; build, integración PostgreSQL, CI y staging pendientes. Sin cambios en master ni producción.
- DholeOperationsService 0.17.0 → 0.18.0; DholeSystem/ecosistema 1.119.1 → 1.120.0; restantes componentes sin cambio.

## [0.17.0] - 2026-11-22 · DH-20261122-OPS-CASE-OVERVIEW-414

- MINOR: GET /api/operations/cases incorpora departamento/estado, equipo, responsable y estado de Origen; detalle incluye usuario creador y workspace devuelve ambos validadores de Aperturas.
- GET /cases/{id}/tabs permite consulta READ_ONLY de departamentos no iniciados a SuperUsuario/ SuperUser sin inventar permisos de modificación o etapas operativas.
- Verificación de roles JWT normalizados para SuperUsuario/SuperUser/SuperAdmin; las comprobaciones de escritura, validación, scopes, concurrencia y equipo permanecen en backend.
- Nuevas pruebas de acceso; sin migraciones ni cambios de nombres de tablas. PostgreSQL, Outbox/Inbox, REST, Redis Streams y SignalR preservados.

## [0.16.0] - 2026-11-22 · DH-20261122-OPERATIONS-ASSIGNMENT-SUMMARY-408

- MINOR: endpoint de lectura company-scoped `GET /api/operations/apertures/assignment-teams` muestra únicamente equipos de Aperturas activos y capacidad agregada para dos validadores, sin exponer usuarios.
- Permite al creador autorizado de su apertura manual en DRAFT seleccionar explícitamente un equipo de Aperturas y asignar dos validadores elegibles. Otros cambios de asignación conservan los permisos estrictos de gestión de equipos.
- Rechazos de equipos inválidos y pools incompletos devuelven códigos y mensajes específicos; conservan el IDTRA sin duplicaciones y no simulan asignación exitosa.
- Preserva la validación doble, comprobaciones Auth en tiempo real, Outbox/SignalR, PostgreSQL y contratos REST previos.
- Coordinado con DholeWeb/DholeSystem 1.116.0 y 14 componentes no modificados.

## [0.15.1] - 2026-11-22 (DH-20261122-OPERATIONS-PARITY-401 · Validación de equipos en apertura)

- PATCH: el validador de Aperturas requiere equipos para FTL y para embarques FCL/FTL multimodales; conserva LCL/LTL sin requisitos erróneos de equipo.
- Se agrega regresión que comprueba la validación de múltiples equipos y cantidades, sin modificar el contrato REST ni el snapshot comercial de Pricing.
- DholeOperationsService 0.15.0 → 0.15.1, ecosistema DholeWeb/System 1.111.1 → 1.112.0. Requiere CI/staging antes de promoción.

## [0.15.0] - 2026-11-21 (DH-20261121-OPERATIONS-OPENING-IDTRA-392 · Apertura manual con IDTRA global)

- MINOR: IDTRA generado desde contador PostgreSQL único (serie interna 00000) con formato visible IDTRA00001, IDTRA12345, IDTRA100000; sin serie editable ni límite artificial 999999; los IDTRA históricos conservan su cadena.
- Modelo de apertura manual extendido con opening_data_json (jsonb) en migración no destructiva. Guarda información operativa de la captura: modalidad, trayecto, Incoterm, mercancía, equipo múltiple, recolecta, WHS, proveedores y servicios. No crea costos ni modifica cotizaciones en Pricing.
- Las aperturas manuales nacen DRAFT; la asignación de validadores comprueba completitud básica por modalidad sin bloquear el guardado del borrador.
- Edición con concurrencia optimista y eventos Outbox/SignalR existentes.
- Operations 0.14.0 → 0.15.0; ecosistema Web/System 1.109.3 → 1.110.0.
- Pendientes: resolver automáticamente snapshots de QUO aceptada y catálogos completos SRM/CRM para la validación definitiva; no declarar la integración Pricing → Operations completada.
- Validaciones de CI y staging pendientes. Sin autorización de producción.

## [0.14.0] - 2026-11-20 (DH-20261120-OPERATIONS-TRAMITES-UX-332 · Expediente IDTRA unificado)

- MINOR: Trámites unifica consulta, búsqueda por IDTRA, QUO y modalidad, además de filtro por cuenta CRM, con autorización, paginación y consultas desde PostgreSQL.
- GET /api/operations/cases/{id}/tabs calcula permisos por tab y equipo/etapa; GET .../workspace devuelve etapas, equipos, QUO, hitos y tareas persistidas.
- PATCH /api/operations/cases/{id} permite corregir únicamente datos de Aperturas mientras siga pendiente; exige operations.case.update, relación efectiva con el expediente, versión optimista, auditoría y Outbox de invalidación SignalR.
- GET /api/operations/cases/{id}/commercial/costs distingue costos de datos de venta mediante permisos explícitos; no hay costos ficticios ni cambios comerciales implícitos.
- Operations 0.13.0 → 0.14.0; release global 1.105.1 → 1.106.0. No cambia esquemas de PostgreSQL ni datos de CRM/Pricing.
- No incluye la creación automática desde QUO aceptada ni captura extensiva de todos los departamentos: permanecen en fases siguientes y no se simulan.

## [0.13.0] - 2026-11-20 (DH-20261120-OPERATIONS-APERTURES-330 · Bandeja de Aperturas y Trámites)

- MINOR: filtros paginados en backend para IDTRA, estado y origen manual/automático en Trámites y cola de Aperturas, respetando empresa, scopes y asignaciones.
- Dashboard: indicadores separados para aperturas manuales y expedientes recibidos desde Pricing, con conteos realizados en PostgreSQL sin recortar a la página actual.
- GET /api/operations/apertures/create-context resuelve empresa desde el JWT y membresías actuales; el ejecutivo de apertura manual sigue resolviéndose desde CRM y no se suplanta por un GUID ficticio.
- Conserva arquitectura REST y eventos Outbox/Inbox/SignalR existentes. El evento de aceptación comercial definitivo y su consumidor automático continúan pendientes: el aviso por correo de Pricing no es evento de creación de expediente.
- Operations 0.12.0 → 0.13.0; ecosistema candidato 1.105.0; CI, pruebas de migración, staging y despliegue de producción pendientes.

## [0.12.0] - 2026-11-19 (DH-20261119-OPS-ROLE-TEAM-DASHBOARD-254 · Resumen operativo con permisos por equipo)

- MINOR: GET /api/operations/dashboard devuelve totales, estados de doble validación, equipos permitidos y últimas 30 aperturas.
- Las agregaciones, expedientes y teamId se filtran en PostgreSQL por company, scope y membresía; los identificadores de equipos ajenos se rechazan con 403.
- El endpoint no concede permisos ni crea usuarios o integrantes; reutiliza REST/JWT y conserva arquitectura realtime y outbox/inbox existente.
- DholeOperationsService 0.11.0 → 0.12.0; ecosistema 1.96.0. CI, integración y pruebas de staging pendientes. Producción no autorizada.

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

## [0.25.1] - 2026-11-25 · DH-20261125-OPS-PICKUP-COMPLETENESS

- PATCH: contacto y ventana de recolecta no forman parte de completitud de apertura; se conservan los datos históricos y la validación de dirección y puntos. Prueba FTL EXW sin contacto ni ventana.


## [0.25.0] - 2026-11-25 · DH-20261125-OPS-PRICING-PARITY

- MINOR: contrato de apertura v3 compatible con Pricing: POD opcional marítimo, excluido aéreo/terrestre; WHS FCA y puntos de recolecta/entrega. Mantiene reglas v2 e históricas.


## [0.24.0] - 2026-11-25 · DH-20261125-OPS-OPENING-VALIDATION

- MINOR: validación obligatoria en creación manual de compatibilidad de embarque, terminales declaradas, equipos, muellaje, peso estimado, dimensiones aéreas, volumen e Incoterm.
- Preserva la política histórica de expedientes y el peso real de Coordinación Origen. Candidato pendiente de CI y staging.


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
