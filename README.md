# saas-platform

Servicio **Platform** del proyecto académico *Plataforma SaaS Gestión Hospitalaria*.

> Laboratorio de ocho semanas y dos integrantes. Solo datos sintéticos. No es un producto para datos clínicos reales.

## Responsabilidad

Platform es dueño del catálogo global de clínicas (tenants), su identificador inmutable, estado administrativo, planes, vigencias y límites declarados. Coordina el alta del administrador inicial **mediante Identity**, sin almacenar credenciales ni manejar cuentas de usuarios.

No gestiona pacientes, expedientes, sesiones, roles de Identity, cobros de pacientes ni KPI de BI.

## Arquitectura adoptada para Platform

- **Vertical Slice Architecture**: organizar las funcionalidades por caso de uso cuando se aprueben.
- Comandos y consultas mediante handlers ligeros; **sin** imponer un bus, broker, MediatR, CQRS distribuido ni event sourcing.
- Dominio explícito únicamente para reglas e invariantes reales.
- **Persistencia multitenant (`ADR-0002`)**: instancia PostgreSQL independiente por tenant administrada con **CloudNativePG**, base de datos propia por microservicio dentro de cada instancia y **base global independiente** para Platform; microservicios compartidos sin copias por tenant y resolución de conexiones fuera del dominio.
- **Consistencia y aprovisionamiento (`ADR-0003`)**: transacciones ACID locales, operaciones distribuidas con invariantes e idempotencia explícitas (autorizando **Saga** cuando esté justificada y considerando **Transactional Outbox** si aplica, sin RabbitMQ/CQRS distribuido/event sourcing sin aprobación) y ejecución privilegiada de aprovisionamiento mediante un **componente trabajador de Platform** con permisos Kubernetes separados de la API pública y frontera estricta con Argo CD.
- ASP.NET Core y .NET 10; una aplicación desplegable y versionada de manera independiente.

Ver [`ADR-0001`](docs/adr/ADR-0001-vertical-slice-platform.md), [`ADR-0002`](docs/adr/ADR-0002-multitenant-persistence.md), [`ADR-0003`](docs/adr/ADR-0003-transactional-consistency-and-provisioning.md) y [`GEMINI.md`](GEMINI.md).

## Contrato Platform–Identity

[`Borrador v0.1 — candidato`](docs/contracts/PLATFORM_IDENTITY_FRONTERA_Y_CONTRATO_MINIMO_v0.1_CANDIDATO.md).

**El documento no está ratificado por ambos integrantes.** Los RPC, esquemas, rutas y mecanismos de autenticación propuestos **no están autorizados para implementación definitiva** hasta cerrar D-01 a D-04. La preparación técnica de APP-00 es independiente de ese acuerdo.

Estados operativos del tenant (`PENDIENTE_ADMIN`, `ACTIVA`, `SUSPENDIDA`) incorporados en el modelo de dominio y catálogo global de **PLAT-02A** (toda clínica inicia en `PENDIENTE_ADMIN`). `Identity READY` es **necesario pero no suficiente** para pasar a `ACTIVA` (`ADR-0002` / `ADR-0003`); la activación efectiva, el trabajador de aprovisionamiento y la integración gRPC siguen fuera de alcance hasta sus incrementos aprobados.

## Estructura actual (PLAT-02A)

```text
Saas.Platform.sln
src/
  Saas.Platform.Api/
    Domain/
      AdministrativeValidation.cs
      ProvisioningOperationStatus.cs
      Tenant.cs
      TenantActivationPrerequisites.cs
      TenantOperationalState.cs
      TenantProvisioningOperation.cs
      TenantValidationError.cs
    Features/
      Tenants/
        GetTenantById/
          GetTenantByIdHandler.cs
          GetTenantByIdQuery.cs
          TenantDetails.cs
          TenantProvisioningSummary.cs
        RegisterTenant/
          RegisterTenantCommand.cs
          RegisterTenantHandler.cs
          RegisterTenantResult.cs
    Persistence/
      Configurations/
        TenantConfiguration.cs
        TenantProvisioningOperationConfiguration.cs
      Migrations/
        20261122180000_InitialTenantCatalog.cs
        20261122180000_InitialTenantCatalog.Designer.cs
        20261124053749_AddTenantOperationalStateAndProvisioningOperations.cs
        20261124053749_AddTenantOperationalStateAndProvisioningOperations.Designer.cs
        PlatformDbContextModelSnapshot.cs
      PlatformDbContext.cs
      PlatformDbContextFactory.cs
    Program.cs
    Saas.Platform.Api.csproj
    appsettings.json
    appsettings.Development.json
    Properties/launchSettings.json
tests/
  Saas.Platform.Tests/
    HealthEndpointsTests.cs
    PostgreSqlFixture.cs
    TenantCatalogIntegrationTests.cs
    TenantDomainValidationTests.cs
    TenantProvisioningIntegrationTests.cs
    Saas.Platform.Tests.csproj
Dockerfile
.dockerignore
.github/workflows/ci.yml
```

Las operaciones administrativas de catálogo (`RegisterTenant`, `GetTenantById`) están implementadas como **Vertical Slices** internos probables de forma autónoma. **No se exponen endpoints HTTP administrativos** en PLAT-01 / PLAT-02A porque la autenticación definitiva de SuperAdmin y los contratos con Identity siguen pendientes de ratificación; no se publican rutas administrativas anónimas ni se aceptan encabezados no confiables como prueba de identidad.

## Prerrequisitos

- **.NET 10 SDK** (`10.0.x`) para restaurar, compilar, ejecutar y probar localmente.
- **Docker** (en ejecución) para las pruebas de integración con PostgreSQL real mediante **Testcontainers** y para construir la imagen de contenedor. No se requiere K3s ni Argo CD para ejecutar `dotnet test`.

## Persistencia global y migraciones (PLAT-01 / PLAT-02A)

- Platform utiliza su propia base PostgreSQL global (esquema `platform`, tablas `platform.tenants` y `platform.tenant_provisioning_operations`), independiente de las instancias por tenant (`ADR-0002`).
- Cada clínica nueva inicia en `PENDIENTE_ADMIN` y registra atómicamente (en la misma transacción ACID) su operación durable inicial con `provisioning_request_id` único e inmutable y los datos estables del administrador inicial (diferenciados de `contact_email`).
- La cadena de conexión se suministra mediante configuración estándar o secretos externos en `ConnectionStrings:PlatformDatabase` (variable de entorno `ConnectionStrings__PlatformDatabase`). No se almacenan credenciales reales en Git ni en `appsettings.json`.
- El esquema se gestiona exclusivamente mediante migraciones versionadas de EF Core (`Persistence/Migrations/`); no se utiliza `EnsureCreated` ni se aplican migraciones automáticamente al arrancar la API o sobre bases de tenants.

```bash
# Aplicar migraciones manualmente contra una base PostgreSQL destino (requiere ConnectionStrings__PlatformDatabase)
dotnet ef database update --project src/Saas.Platform.Api/Saas.Platform.Api.csproj
```

## Comandos locales

```bash
dotnet restore Saas.Platform.sln
dotnet build Saas.Platform.sln --configuration Release --no-restore
dotnet test Saas.Platform.sln --configuration Release --no-build
dotnet run --project src/Saas.Platform.Api/Saas.Platform.Api.csproj
```

Por defecto, el perfil HTTP local escucha en `http://localhost:8080`.

## Endpoints técnicos

| Ruta | Método | Respuesta esperada | Qué verifica | Qué NO verifica en APP-00 |
|---|---|---|---|---|
| `/health/live` | `GET` | `200 OK` (`application/json`) | El proceso ASP.NET Core está en ejecución (`scope: "process-only"`). | No verifica base de datos, Identity ni red externa. |
| `/health/ready` | `GET` | `200 OK` (`application/json`) | Disponibilidad básica del propio proceso (`scope: "process-only"`). | No sondea PostgreSQL, Identity ni dependencias aún inexistentes. |

Ejemplo de comprobación local:

```bash
curl -i http://localhost:8080/health/live
curl -i http://localhost:8080/health/ready
```

## Pruebas automatizadas

`tests/Saas.Platform.Tests/` incluye:

- **Pruebas HTTP técnicas y de seguridad (`HealthEndpointsTests`):** ejecutan `Program` en memoria mediante `WebApplicationFactory<Program>` sin requerir PostgreSQL ni Kubernetes; verifican `/health/live`, `/health/ready`, `404` ante rutas inexistentes y la ausencia de endpoints administrativos anónimos.
- **Pruebas unitarias de dominio (`TenantDomainValidationTests`):** verifican normalización, invariantes administrativas de `Tenant`, prerequisitos de activación (`CanActivate`), invariantes de `TenantProvisioningOperation` y sanitización de diagnósticos sin secretos.
- **Pruebas de integración con PostgreSQL real (`TenantCatalogIntegrationTests`, `TenantProvisioningIntegrationTests`):** utilizan **Testcontainers** (`postgres:17-alpine`) para verificar migraciones reproducibles sobre base vacía y sobre esquema PLAT-01 previo, persistencia atómica ACID de tenant + operación, recuperación tras recrear `PlatformDbContext`, unicidad de `public_id` / `tenant_id` / `provisioning_request_id`, concurrencia de altas duplicadas, concurrencia optimista (`DbUpdateConcurrencyException`), registro de error recuperable sin secretos y restricciones `CHECK` / `FK` en PostgreSQL.

```bash
# Ejecutar toda la suite (requiere .NET 10 SDK y Docker activo para Testcontainers)
dotnet test Saas.Platform.sln --configuration Release

# Ejecutar únicamente pruebas sin Docker (dominio y endpoints técnicos)
dotnet test Saas.Platform.sln --configuration Release --filter "FullyQualifiedName!~IntegrationTests"
```

En GitHub Actions (`ubuntu-latest`), `dotnet test` ejecuta la suite completa, incluidas las pruebas de integración con PostgreSQL real vía Testcontainers (no se omiten silenciosamente).

## Docker

La imagen es multietapa (`mcr.microsoft.com/dotnet/sdk:10.0` → `mcr.microsoft.com/dotnet/aspnet:10.0`), expone el puerto no privilegiado `8080` y se ejecuta como usuario no root (`$APP_UID`).

```bash
docker build --file Dockerfile --tag saas-platform:local .
docker run --rm -p 8080:8080 saas-platform:local
```

## CI, GHCR y despliegue GitOps (separación de responsabilidades)

El workflow único [`.github/workflows/ci.yml`](.github/workflows/ci.yml) separa estrictamente la verificación de la publicación:

- **Triggers:** `pull_request` hacia `develop` y `main`, y `push` en `develop` y `main`.
- **CI de verificación (APP-00 / LAB-05):**
  - `build-and-test` (`permissions: contents: read`): `dotnet restore`, `build` y `test` en `.NET 10` (xUnit v3 / VSTest).
  - `docker-build` (`needs: build-and-test`, `permissions: contents: read`): construye la imagen `ghcr.io/maverick0322/saas-platform:<SHA>` con metadatos OCI (`org.opencontainers.image.source`, `revision`, `title`, `created`) y ejecuta el smoke test HTTP sobre `/health/live` y `/health/ready`.
  - En PR y en `push` a `develop`, el flujo termina aquí: **no se publica ninguna imagen** ni se conceden credenciales de publicación.
- **Publicación GHCR (APP-01 / LAB-06):**
  - `publish-ghcr` (`needs: [build-and-test, docker-build]`, `if: github.event_name == 'push' && github.ref == 'refs/heads/main'`, `permissions: packages: write`).
  - Recibe la **misma imagen ya construida y verificada** desde `docker-build` mediante un artefacto temporal de la misma ejecución (`docker save` / `docker load`, retención 1 día), sin reconstruirla.
  - Publica únicamente `ghcr.io/maverick0322/saas-platform:<FULL_COMMIT_SHA>` (sin etiqueta `latest`) autenticándose con `GITHUB_TOKEN`.
  - Registra la referencia, el SHA de origen y el digest inmutable (`ghcr.io/maverick0322/saas-platform@sha256:...`) en el resumen del workflow (`GITHUB_STEP_SUMMARY`).
- **Visibilidad y descarga desde GHCR:**
  - La etiqueta `org.opencontainers.image.source` vincula el paquete al repositorio `maverick0322/saas-platform`. La visibilidad del paquete se gestiona en GitHub Packages (público o privado según política del repositorio).
  - Si el paquete es público, se descarga sin autenticación; si es privado, requiere `docker login ghcr.io` con un token `read:packages`:
    ```bash
    docker pull ghcr.io/maverick0322/saas-platform:<FULL_COMMIT_SHA>
    # o preferiblemente por digest inmutable:
    docker pull ghcr.io/maverick0322/saas-platform@sha256:<DIGEST>
    ```
- **Kubernetes (LAB-07), Argo CD (LAB-08) y CD mediante GitOps (LAB-09):** la promoción de versión desplegada ocurre mediante PR en `saas-infra` y sincronización de Argo CD. Fusionar a `main` o publicar en GHCR **no** demuestra despliegue en K3s.

Ver [convenciones Git/CI/CD](docs/engineering/GIT-CI-CD.md), [alcance APP-00](docs/APP-00-ALCANCE.md), [evidencia APP-00](docs/APP-00-VALIDATION.md) y [evidencia APP-01](docs/APP-01-VALIDATION.md).

## Estado

- **APP-00 / LAB-05 (`PASS`):** base técnica .NET 10, health checks (`/health/live`, `/health/ready`) y pruebas HTTP xUnit v3 integrados en `develop` (PR #2).
- **APP-01 / LAB-06 (`GHCR_PASS`):** pipeline de publicación integrado en `develop` (PR #3) y promovido a `main` (PR #4, SHA `3c46822c722f889cd715daa6d05beb58e143fb61`, workflow [`44880025884`](https://github.com/maverick0322/saas-platform/actions/runs/44880025884)).
- **Imagen inmutable verificada:** `ghcr.io/maverick0322/saas-platform@sha256:104c651cd22324935c161314a28aeb7fd8728090710ad355b26cd37842b00746` (verificada mediante `sudo k3s crictl pull` en containerd: ID `e833cadbe2d3d`, 104 MB).
- **PLAT-00 (documental, integrado en `develop` vía PR #7):** formalización de arquitectura multitenant ([`ADR-0002`](docs/adr/ADR-0002-multitenant-persistence.md)) y de consistencia transaccional y aprovisionamiento ([`ADR-0003`](docs/adr/ADR-0003-transactional-consistency-and-provisioning.md)).
- **PLAT-01 (catálogo global y persistencia de Platform):** `PlatformDbContext` global con EF Core + PostgreSQL, migración inicial versionada (`20261122180000_InitialTenantCatalog`), vertical slices internos (`RegisterTenant`, `GetTenantById`) y pruebas de integración con Testcontainers. Rutas HTTP administrativas diferidas hasta ratificar autenticación de SuperAdmin y contratos con Identity.
- **PLAT-02A (estados operativos y aprovisionamiento durable):** estados `PENDIENTE_ADMIN`, `ACTIVA` y `SUSPENDIDA` en `Tenant` (inicio obligatorio en `PENDIENTE_ADMIN` y bloqueo de activación sin prerrequisitos completos), entidad durable `TenantProvisioningOperation` con `provisioning_request_id` estable, migración versionada incremental (`20261124053749_AddTenantOperationalStateAndProvisioningOperations`), registro atómico ACID y representación administrativa preparada en `GetTenantById` (sin trabajador, CloudNativePG, llamadas gRPC, activación efectiva ni rutas administrativas anónimas).
- **Infraestructura (`saas-infra`, commit `819480a` según el último handoff):**
  - **LAB-07:** `PASS LOCAL`.
  - **LAB-08:** `PASS LOCAL`.
  - **LAB-09:** `PASS LOCAL`.
  - **LAB-10:** pendiente.
