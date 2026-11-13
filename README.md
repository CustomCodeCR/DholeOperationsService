# DholeOperationsService

Servicio de operaciones del ecosistema Dhole.

Arquitectura base basada en DholeConfigService: API, Application, Contracts, Domain, Infrastructure, Persistence y Workers (.NET 10); despliegues independientes de staging (`develop`) y producción (`master`).

La implementación inicial de operaciones y la configuración de infraestructura deben seguir las políticas de `AGENTS.md`. Nunca usar `main` para este servicio.
