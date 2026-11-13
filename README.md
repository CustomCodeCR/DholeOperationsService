# DholeOperationsService

Servicio de operaciones del ecosistema Dhole, basado en la arquitectura de DholeConfigService, sin replicar su lógica de negocio.

## Proyectos
`Dhole.Operations.Api` (REST/gRPC/SignalR), `Application` (CQRS), `Contracts` (DTO/protobuf), `Domain` (Outbox/Inbox), `Infrastructure` (Redis), `Persistence` (PostgreSQL EF Core) y `Workers` (Outbox/Inbox). Incluye pruebas unitarias y de integración.

## Desarrollo
```bash
dotnet restore DholeOperationsService.slnx --configfile NuGet.Config
dotnet build DholeOperationsService.slnx -c Release --no-restore
dotnet test DholeOperationsService.slnx -c Release --no-build
```

## Entornos y seguridad
- `develop` despliega automáticamente en staging como DholeConfigService, después del CI y con los recursos de staging disponibles.
- `master` despliega en producción únicamente tras autorización explícita y habilitar `DHOLE_OPERATIONS_PRODUCTION_ENABLED=true`.
- Se usan los runners self-hosted y la infraestructura de DholeSystem. Los servicios se definen en el overlay `deploy/docker-compose.operations.yml`.
- Previo al despliegue se requiere PostgreSQL DB `dhole_operations`, Redis, JWT y las variables compartidas del servidor.
- El Gateway externo `/api/operations` se integra separadamente en DholeApiGateway cuando se autorice.
- No modificar ni usar `main`.

API: `/health`, `/ready`, `/hubs/operations`, `/api/operations/realtime/events`, gRPC en 8081. Autenticación JWT para realtime y datos. Redis Streams + Outbox/Inbox idempotente permiten sincronización desde REST.
