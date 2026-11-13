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
