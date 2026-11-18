# Operations 1.2 — Preparación backend (DH-20261117-OPS-FOUNDATION-205)

Primer incremento aditivo, **no** entrega funcional completa de la especificación 1.2.
Se conserva el esquema PostgreSQL `operations`, Outbox/Inbox de CustomCodeFramework y la API REST existente.

## Implementado
- Registro autenticado, aislado por empresa, de solicitudes de conexión marítima. Su estado inicial es `REQUESTED`, no `ACTIVE`. Sin credenciales, URLs externas, llamadas, bookings ni permisos autoactivados.
- Lectura de capacidades persistidas de cada conexión; sin conexión devuelve `NOT_CONFIGURED`.
- Esquema inicial de capacidades para las once navieras del documento 1.2, referenciando proveedor SRM externo sin copiar su ficha.
- Persistencia aislada de progreso de Origen y bitácora privada. Solo responsable asignado, supervisor del equipo o SuperAdmin con scope pueden acceder a notas y confirmar salida.
- Confirmación de salida manual con evidencia Storage obligatoria, control optimista de versión, bloqueo PostgreSQL, deduplicación por EventId, alta de etapa Tránsito y Outbox duradero sin email al cliente.

## Contratos verificados en el código existente
- CRM: `GET /api/crm/accounts/{id}/operations-contacts` mediante proxy autenticado de Operations.
- CRM: `GET /api/crm/accounts/{id}` y `assigned-executive` existen, pero el segundo no devuelve siempre UUID del ejecutivo: NO utilizarlo como fuente automática de identidad hasta acordar contrato.
- Pricing: `AcceptedByClient` es la aceptación definitiva. La comunicación de apertura actual en Pricing es `notifications.notification.requested`, una **solicitud de correo**, no un evento de aceptación consumible sin ambigüedad. Se necesita un evento específico y snapshot de opción aceptada antes de crear IDTRA automáticamente.
- Config y SRM: catálogos y proveedores siguen siendo sus fuentes propias; sin FK cruzadas.
- Storage: se conserva UUID como referencia; el endpoint de salida exige comprobante pero la validación de propiedad del archivo vía contrato de Storage queda pendiente antes de exposición operativa.
- Notifications: ninguna nota interna se enviará como correo. Las comunicaciones reales requieren contrato de entrega de Notifications y su historial.
- Auth: scopes necesarios por endpoint, sin usuarios, membresías ni grants automáticos.
- Gateway: `/api/operations` existente; pendientes smoke tests de nueva ruta y WSS.
- Workers: Outbox/Inbox y realtime existentes no se sustituyen.

## Pendiente para considerar el MVP listo
Validación Aperturas por modalidad, CRM ejecutivo por cuenta, evento Pricing definitivo + subQUO y snapshot, creación/activación de etapa Origen desde validación, pools configurados, checklist, instrucciones especiales, destinatarios y envíos a cliente, permisos completos en Auth, contratos de Storage y DataExtraction/AI, UI de ocho tabs, pruebas de integración por roles, sandbox de navieras. La acción de salida presupone que ya existe una etapa Origen autorizada; no inventa una asignación.

## CI / promoción
Compilar y probar `DholeOperationsService.slnx`, ejecutar migración desde staging existente y verificar columnas/tablas/índices, probar reintentos, acceso cruzado, salidas concurrentes y no exposición privada. `develop` antes que `master`, con staging validado. `master` dispara deploy automático: no promover a producción sin autorización explícita.
