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

Estados operativos del tenant (`PENDIENTE_ADMIN`, `ACTIVA`, `SUSPENDIDA`) incorporados en el modelo de dominio y catálogo global de **PLAT-02A** (toda clínica inicia en `PENDIENTE_ADMIN`). `Identity READY` es **necesario pero no suficiente** para pasar a `ACTIVA` (`ADR-0002` / `ADR-0003`); a partir de **PLAT-03A**, la API expone opcionalmente la consulta gRPC interna `TenantAccessService.GetTenantAccessState` (v1), manteniendo fuera de alcance la activación efectiva, las rutas HTTP administrativas y los adaptadores externos del trabajador.

## Contrato HTTP administrativo (`PLAT-04A` / `PLAT-04B`)

- Especificación técnica: [`PLATFORM_ADMIN_HTTP_v1.md`](docs/contracts/PLATFORM_ADMIN_HTTP_v1.md) (**candidato v1** con `D-HTTP-01` a `D-HTTP-06` adoptadas en **PLAT-04B**).
- Especificación OpenAPI (`3.0.3`): [`platform-admin.v1.yaml`](docs/contracts/openapi/platform-admin.v1.yaml).
- **Habilitación explícita y segura por configuración (`AdminApi:Enabled`):** por defecto `false` (no expone rutas `/v1/tenants*`, preservando despliegues previos y `/health/*`). Cuando `AdminApi:Enabled=true`:
  - Requiere **exclusivamente clave pública RSA ≥ 2048 bits** en formato PEM `SubjectPublicKeyInfo` vía `Jwt:PublicKeyPem` o `Jwt:PublicKeyPemPath` (archivo montado recomendado en Kubernetes), `Jwt:Issuer=saas-identity`, `Jwt:Audience=vital-saas`, y rechaza arranque si existen secretos prohibidos (`Jwt:PrivateKeyPem`, `Jwt:PrivateKeyPemPath`, `Jwt:RefreshTokenHmacKey`, `Jwt:SigningKey`).
  - Exige JWT `RS256` (`ClockSkew=30s`, `MapInboundClaims=false`, `RoleClaimType=role`, `sub` UUID no vacío, `role=SUPER_ADMIN` sin `tenant_id`) y responde `401 Unauthorized` (`WWW-Authenticate: Bearer`) vs `403 Forbidden`.
  - Requiere `IdentityAuth:BaseAddress` y `IdentityAuth:TimeoutSeconds` (default `3`s) para verificar vigencia y rol global en `GET /v1/auth/me` antes de `POST /v1/tenants` (fallo cerrado `503` sin ejecutar aprovisionamiento síncrono de CloudNativePG, Clinical ni `ProvisionInitialClinicAdmin`).

## Estructura actual (PLAT-04B)

```text
Saas.Platform.sln
src/
  Saas.Platform.Api/
    Domain/
      AdministrativeValidation.cs
      ProvisioningOperationStatus.cs
      ProvisioningStage.cs
      ProvisioningStageState.cs
      Tenant.cs
      TenantActivationPrerequisites.cs
      TenantOperationalState.cs
      TenantProvisioningOperation.cs
      TenantValidationError.cs
    Features/
      Provisioning/
        IProvisioningStageExecutor.cs
        ProvisioningLeaseStore.cs
        ProvisioningOperationProcessor.cs
        ProvisioningWorkerHostedService.cs
        ProvisioningWorkerOptions.cs
        UnconfiguredProvisioningStageExecutor.cs
      Tenants/
        AdminHttp/
          AdminApiOptions.cs
          AdminIdentityClaimsValidator.cs
          AdminProblemDetailsWriter.cs
          IdentityMeAccountVerifier.cs
          IIdentityAccountVerifier.cs
          ProvisioningAdministrativeErrorCatalog.cs
          TenantAdminEndpoints.cs
          TenantAdminHttpModels.cs
        GetTenantAccessState/
          GetTenantAccessStateHandler.cs
          GetTenantAccessStateQuery.cs
          GetTenantAccessStateResult.cs
          InternalGrpcOptions.cs
          TenantAccessGrpcService.cs
        GetTenantById/
          GetTenantByIdHandler.cs
          GetTenantByIdQuery.cs
          TenantDetails.cs
          TenantProvisioningSummary.cs
        ListTenants/
          ListTenantsHandler.cs
          ListTenantsQuery.cs
          ListTenantsResult.cs
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
        20261124070500_AddProvisioningWorkerLeaseAndStageProgress.cs
        20261124070500_AddProvisioningWorkerLeaseAndStageProgress.Designer.cs
        20261124093133_AddTenantMaxActiveAccounts.cs
        20261124093133_AddTenantMaxActiveAccounts.Designer.cs
        PlatformDbContextModelSnapshot.cs
      PlatformDbContext.cs
      PlatformDbContextFactory.cs
    PlatformHostFactory.cs
    Program.cs
    Saas.Platform.Api.csproj
    appsettings.json
    appsettings.Development.json
    Properties/launchSettings.json
tests/
  Saas.Platform.Tests/
    AdminApiSecurityTests.cs
    AdminHttpPostgreSqlIntegrationTests.cs
    HealthEndpointsTests.cs
    MutableTimeProvider.cs
    PostgreSqlFixture.cs
    ProvisioningWorkerCoreTests.cs
    ProvisioningWorkerIntegrationTests.cs
    TenantAccessGrpcIntegrationTests.cs
    TenantCatalogIntegrationTests.cs
    TenantDomainValidationTests.cs
    TenantProvisioningIntegrationTests.cs
    TestJwtTokenFactory.cs
    Saas.Platform.Tests.csproj
Dockerfile
.dockerignore
.github/workflows/ci.yml
```

### API HTTP administrativa protegida (`PLAT-04B`)

Las rutas `POST /v1/tenants`, `GET /v1/tenants` y `GET /v1/tenants/{tenantId}` permanecen deshabilitadas por defecto (`AdminApi:Enabled=false`). Al habilitarlas (`AdminApi__Enabled=true`), requieren:
- `Jwt__PublicKeyPem` o `Jwt__PublicKeyPemPath` (clave pública RSA `>= 2048` bits únicamente), `Jwt__Issuer` (`saas-identity`) y `Jwt__Audience` (`vital-saas`).
- `IdentityAuth__BaseAddress` y `IdentityAuth__TimeoutSeconds` (`3` segundos por defecto) para la comprobación de cuenta activa global `SUPER_ADMIN` vía `GET /v1/auth/me` previa a `POST /v1/tenants` (`D-HTTP-01`).
- Decisiones `D-HTTP-01` a `D-HTTP-06` de [`PLATFORM_ADMIN_HTTP_v1.md`](docs/contracts/PLATFORM_ADMIN_HTTP_v1.md) implementadas para Platform (sin extenderlas unilateralmente a otros microservicios).

### Servidor gRPC interno Platform–Identity (`PLAT-03A`)

Cuando `InternalGrpc:Enabled=true` (`InternalGrpc__Enabled=true`), el host API de Platform habilita Kestrel con separación explícita de protocolos:
- **HTTP/1.1 (`Ports:Http`, por defecto `8080`):** endpoints técnicos `/health/live` y `/health/ready`.
- **HTTP/2 `h2c` (`InternalGrpc:Port`, por defecto `8092`):** servicio Protobuf v1 `vital.platform_identity.v1.TenantAccessService/GetTenantAccessState`, sin modificar `platform_identity.proto`.
- **Autenticación y fallo cerrado:** exige la metadata gRPC `x-internal-service-key` validada en tiempo constante (`CryptographicOperations.FixedTimeEquals`) frente a `InternalGrpc:InternalServiceKey` (mínimo 16 bytes UTF-8, suministrada exclusivamente por configuración segura o variables de entorno, nunca en código/Git). Si `InternalGrpc:Enabled=true` sin clave válida, el arranque falla explícitamente (`InvalidOperationException`).
- **Cuota por clínica (`max_active_accounts`):** persistida explícitamente en `platform.tenants.max_active_accounts` (`>= 1`, sin inventar planes ni valores globales silenciosos). Los registros previos a PLAT-03A permanecen en `PENDIENTE_ADMIN` con `max_active_accounts = NULL`; ante cuota ausente o falla de PostgreSQL, el servidor responde con fallo controlado (`FailedPrecondition` / `Unavailable`) sin conceder acceso.
- **Dependencias nuevas:** `Grpc.AspNetCore` (`2.71.0`) en `Saas.Platform.Api` (compilación servidor `.proto` v1) y `Grpc.Net.Client` (`2.71.0`) en `Saas.Platform.Tests`.

### Modo trabajador separado (`PLAT-02C`)

El mismo binario/imagen de Platform soporta dos modos explícitos mediante `Platform:RuntimeMode` (`Platform__RuntimeMode`) o `--mode=api` / `--mode=worker`:
- **`Api` (por defecto):** expone únicamente los endpoints técnicos HTTP (`/health/live`, `/health/ready`); **no** registra ni ejecuta `ProvisioningWorkerHostedService`.
- **`Worker`:** ejecuta un `IHost` genérico sin servidor HTTP con el núcleo durable del trabajador (`ProvisioningWorkerHostedService`). Por seguridad, `ProvisioningWorker:Enabled` está en `false` por defecto y en producción únicamente se registra `UnconfiguredProvisioningStageExecutor` (`SupportsRealProvisioning = false`), por lo que no realiza aprovisionamiento real ni modifica clínicas hasta conectar adaptadores reales y contratos ratificados.

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
- **Pruebas unitarias de dominio y trabajador (`TenantDomainValidationTests`, `ProvisioningWorkerCoreTests`, `CloudNativePgAdapterTests`, `CloudNativePgServiceDatabasesTests`):** verifican normalización, invariantes administrativas de `Tenant`, prerequisitos de activación (`CanActivate`), invariantes de `TenantProvisioningOperation`, sanitización de diagnósticos sin secretos/JWT/cadenas de conexión, configuración/backoff del trabajador, modelado/evaluación del `Cluster`, `DatabaseRole`, `Database`, `Secret` de autenticación/distribución en CloudNativePG (`DB-01C`) y separación segura entre modos `Api`, `Worker` y `ResumeOperation`.
- **Pruebas de integración con PostgreSQL real (`TenantCatalogIntegrationTests`, `TenantProvisioningIntegrationTests`, `ProvisioningWorkerIntegrationTests`, `CloudNativePgWorkerIntegrationTests`, `CloudNativePgServiceDatabasesIntegrationTests`, `TenantAccessGrpcIntegrationTests`):** utilizan **Testcontainers** (`postgres:17-alpine`) para verificar migraciones reproducibles sobre base vacía y sobre esquemas PLAT-01/PLAT-02A/PLAT-02C previos, persistencia atómica ACID de tenant + operación, adquisición atómica con `FOR UPDATE SKIP LOCKED`, exclusión concurrente, renovación periódica de *lease* durante etapas prolongadas, cancelación y rechazo de escrituras obsoletas (*fencing*) al perder el *lease*, recuperación conservadora (`VERIFICATION_REQUIRED`) tras caídas sin duplicar efectos externos, conteo exacto de intentos iniciados, reintentos limitados con espera exponencial, errores transitorios/terminales, confirmación durable de `InfrastructureInstance` y `ServiceDatabases` con detención controlada en `ClinicalPreparation` (`AdapterNotConfigured`), verificación en vivo sobre PostgreSQL 17 TLS de `pg_hba` previo a `LOGIN`, `REVOKE CONNECT ... FROM PUBLIC` por propietario sin superusuario y matriz negativa, reanudación controlada (`--mode=resume-operation`), rechazo de evidencia inválida o simulada, ausencia de activaciones falsas y llamadas gRPC HTTP/2 reales a `TenantAccessService/GetTenantAccessState`.

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
- **PLAT-02B (diseño del trabajador y contratos mínimos, PR #12):** especificación lógica de arrendamiento, recuperación, reintentos y contratos mínimos en [`docs/PLAT-02B-PROVISIONING-WORKER-DESIGN.md`](docs/PLAT-02B-PROVISIONING-WORKER-DESIGN.md).
- **PLAT-02C (núcleo durable del trabajador de aprovisionamiento):** adquisición atómica PostgreSQL con *lease* y *fencing* (`lease_version`), progreso durable por etapa, reintentos limitados con espera exponencial, migración incremental (`20261124070500_AddProvisioningWorkerLeaseAndStageProgress`) y host `Worker` separado de la API pública, deshabilitado por defecto y con adaptador `UnconfiguredProvisioningStageExecutor` (sin CloudNativePG, gRPC Identity, Clinical ni activación efectiva todavía).
- **PLAT-03A (servidor gRPC de estado operativo Platform–Identity):** implementación de `TenantAccessService.GetTenantAccessState` sobre HTTP/2 (`h2c`, puerto candidato `8092`) separado del tráfico HTTP/1.1 (`8080`), autenticación `x-internal-service-key` en tiempo constante, cuota configurable por clínica (`max_active_accounts`, migración `20261124093133_AddTenantMaxActiveAccounts`) y fallo cerrado sin alterar el `.proto` v1 ni el modo `Worker`.
- **PLAT-03B (validación en vivo Platform–Identity, `PASS`):** verificación de extremo a extremo vía gRPC HTTP/2 entre `saas-platform` (`0cd248f`) y `Eugene1805/saas-identity` (`a71c1e7`) con `Platform__Mode=Grpc` y bases PostgreSQL sintéticas desechables ([evidencia PLAT-03B](docs/PLAT-03B-VALIDATION.md)).
- **PLAT-04A (documental, contrato HTTP administrativo candidato):** definición y ajuste de [`PLATFORM_ADMIN_HTTP_v1.md`](docs/contracts/PLATFORM_ADMIN_HTTP_v1.md) y [`platform-admin.v1.yaml`](docs/contracts/openapi/platform-admin.v1.yaml) (`POST /v1/tenants`, `GET /v1/tenants`, `GET /v1/tenants/{tenantId}`, JWT `RS256` `IDENTITY-SEC-01`, `D-HTTP-01` vía `GET /v1/auth/me` en alta, `409` con `existingTenantId`, RFC 9457 `ProblemDetails`) integrados en `develop` (PR #16).
- **PLAT-04B (API HTTP administrativa de Platform):** implementación de `POST /v1/tenants`, `GET /v1/tenants` (`ListTenants`) y `GET /v1/tenants/{tenantId}` bajo `AdminApi:Enabled` (`false` por defecto), validación local JWT `RS256` (`IDENTITY-SEC-01`, sin secretos privados/HMAC), política `SUPER_ADMIN` (`401` con `WWW-Authenticate` vs `403`), comprobación síncrona de vigencia en `GET /v1/auth/me` previa al alta (`D-HTTP-01`), conflicto `409` con `existingTenantId` (`D-HTTP-03`) y mensajes administrativos controlados (`lastErrorSummary`) verificados con PostgreSQL real y dobles HTTP de Identity.
- **PLAT-04C (validación real API administrativa Platform–Identity, `INTEGRATION_PASS`):** verificación extremo a extremo (`31/31` escenarios aprobados) en Docker Compose aislado entre `saas-platform` (`efe1116`) y `Eugene1805/saas-identity` (`a374584`) con tokens `RS256` reales de `POST /v1/auth/platform/login`, verificación vía `GET /v1/auth/me`, fallo cerrado (`403`/`503`), preservación de `TenantAccessService.GetTenantAccessState` y sin dependencia circular ([evidencia PLAT-04C](docs/PLAT-04C-VALIDATION.md)).
- **PLAT-05A (adaptador real CloudNativePG para `InfrastructureInstance`, `IMPLEMENTATION_PASS` e `INTEGRATION_PASS` en `saas-infra`):** implementación del cliente Kubernetes in-cluster (`KubernetesClient`), manifiesto determinístico del recurso `postgresql.cnpg.io/v1 Cluster` según perfil `DB-01C`, evaluación estricta de propiedad/especificación/disponibilidad, persistencia de `infrastructure_resource_ref` y detención controlada en `ServiceDatabases` (`AdapterNotConfigured`), manteniendo deshabilitado el Worker por defecto ([diseño y operación PLAT-05A](docs/PLAT-05A-CNPG-INFRASTRUCTURE.md)).
- **PLAT-05B (aprovisionamiento automático de bases privadas por servicio `ServiceDatabases`, `IMPLEMENTATION_PASS` e `INTEGRATION_PASS` el 2026-11-26, cerrado en `PLAT-05B-CLOSE`):** reconciliación idempotente de `DatabaseRole`, `Database`, `Secret` de autenticación (`kubernetes.io/basic-auth`, `cnpg.io/reload=true`), normalización estricta en borde de deserialización para los 6 booleanos `omitempty` de `RoleConfiguration` en CloudNativePG `1.30.2`, verificación previa a `LOGIN` de reglas TLS `pg_hba`, revocación de `CONNECT` de `PUBLIC` como propietarios sin superusuario sobre PostgreSQL 17.12 TLS, matriz negativa de aislamiento, distribución consolidada con `{tenantId:D}.connection`, comando interno controlado `--mode=resume-operation --operation-id=<uuid>` y detención controlada en `ClinicalPreparation` (`AdapterNotConfigured`). Validado en K3s real (`saas-infra@571f0605`): operación `01a214af-fddc-75c8-9c5e-7f2317a4e6c2` / tenant `01a214af-fddc-713e-a908-c1f690486d90` con `ServiceDatabases=CONFIRMED`, `AttemptCount=3`, `LeaseVersion=3` y mecanismo temporal de recuperación retirado tras su uso único ([diseño y evidencia PLAT-05B](docs/PLAT-05B-SERVICE-DATABASES.md)).
- **Infraestructura (`saas-infra`, commit `819480a` según el último handoff):**
  - **LAB-07:** `PASS LOCAL`.
  - **LAB-08:** `PASS LOCAL`.
  - **LAB-09:** `PASS LOCAL`.
  - **LAB-10:** pendiente.
