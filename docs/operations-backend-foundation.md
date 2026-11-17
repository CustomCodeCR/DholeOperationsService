# DholeOperationsService — Backend foundation (DH-183)

## Contraste con DholeConfigService

Operations mantiene las capas Api, Application, Contracts, Domain, Infrastructure, Persistence y Workers de Config. El bootstrap existente de .NET 10, PostgreSQL, JWT, Redis Streams, SignalR y Outbox/Inbox permanece. Los comandos/handlers en Application delegan en un contrato de persistencia transaccional. No se copia la base de Config ni sus catálogos.

## Entregado en esta primera iteración

- Creación manual de expediente con UUID técnico y IDTRA transaccional, sin MAX+1. La serie de cinco dígitos debe indicarse explícitamente hasta que negocio defina su significado.
- Primera etapa APERTURAS, pendiente de validación. No se marca como validada ni se simula una QUO aceptada.
- Pool multiempresa con equipos, miembros, puntos de capacidad, disponibilidad, reglas por modalidad/ruta y asignación de menor carga proyectada. Bloqueo advisory transaccional por empresa/departamento y restricción única para asignaciones vigentes.
- Documentos representados por metadatos, referencias de Storage, versiones y solicitudes de extracción persistidas. Ningún binario se almacena en Operations PostgreSQL.
- Eventos de cambio via Outbox/Redis Streams; Inbox duradero; SignalR notifica solo el expediente autorizado y sin datos comerciales. Replay REST verifica permisos, sin PayloadJson.
- Migración SQL compatible que conserva las tablas existentes de Outbox/Inbox.

## Contratos nuevos (borrador técnico)

API: POST/GET /api/operations/cases, GET /api/operations/cases/{id}, POST /cases/{id}/stages/{stageId}/assign,
POST/GET /teams, POST /teams/{id}/members, POST /assignment-rules, POST /cases/{id}/documents,
POST /cases/{id}/document-versions/{versionId}/extractions, GET /cases/{id}/extraction-jobs.
Todos se ofrecen bajo /api/operations y requieren JWT/scopes del backend.

Eventos: operations.case.created.v1, operations.case.assigned.v1,
operations.document.registered.v1, operations.document.extraction.requested.v1.

## Integración documental pendiente de contrato

DataExtraction ya dispone de POST /api/data-extraction/documents/analyze, que requiere multipart con un archivo de hasta 20 MiB y JWT. Usa MarkItDown/OCR y produce evidencia, campos, advertencias y un indicador RequiresHumanReview. No acepta todavía una referencia StorageFileId como trabajo de cola asíncrono. Operations NO debe enviar una referencia UUID a ese endpoint como si fuera el binario, ni fingir que el evento solicitado se procesó.

La siguiente fase deberá:
1. Definir un contrato de servicio a servicio para leer desde Storage de manera autorizada y sin duplicación permanente.
2. Despachar trabajos durablemente a DataExtraction sin interferir con el pool de correos/tarifarios; limitar CPU, memoria, tamaño, páginas y concurrencia.
3. Conservar evidencia y procedencia campo→archivo→página; separar el resultado bruto del dato confirmado.
4. Comparar BL/HBL/MBL/AWB/Packing List/Invoice/Manifiesto/Carta Porte contra el expediente y la QUO, con unidades normalizadas.
5. Registrar REVIEW_REQUIRED; un revisor autorizado confirmará diferencias antes de mutar el expediente. La IA no sobrescribirá datos validados.
6. Aplicar métricas reales de exactitud por campo y rechazo. Ningún porcentaje de 99,9 % se presume sin corpus de pruebas.

## Preflight obligatorio antes de merge a develop

- Validar que Auth emite company_id y los scopes operations.case.read/create, operations.assignment.manage, operations.team.manage, operations.document.manage y operations.realtime.read. SuperAdmin es excepción explícita.
- Validar contrato de Storage para el UUID y ownership del archivo antes de habilitar adjuntos en usuarios finales.
- Configurar equipos reales, capacidad, peso por modalidad y reglas autorizadas: no se insertan empleados o pesos inventados.
- Probar migración desde una base con operations_outbox/inbox; restauración desde backup; dotnet restore/build/test.
- Probar acceso no autorizado a casos, Hub y replay, doble petición, concorrencia de secuencia y pool, desconexión SignalR, replay.
- Verificar que los PR a develop no provoquen despliegue en producción y que el flujo de master requiera autorización.
- Revisar y versionar el manifiesto global de DholeSystem.

## No implementado todavía

Apertura automática por aceptación real de Pricing, completitud de ocho tabs, revisión/aprobación documental, despacho asíncrono a DataExtraction/AI, workers de análisis, operaciones por país y conciliación financiera. Esas fases necesitan contratos y procedimientos verificados, no endpoints simulados.
