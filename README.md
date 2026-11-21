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
- ASP.NET Core y .NET 10; PostgreSQL privado de Platform cuando se apruebe persistencia.
- Una aplicación desplegable y versionada de manera independiente.

Ver [`ADR-0001`](docs/adr/ADR-0001-vertical-slice-platform.md) y [`GEMINI.md`](GEMINI.md).

## Contrato Platform–Identity

[`Borrador v0.1 — candidato`](docs/contracts/PLATFORM_IDENTITY_FRONTERA_Y_CONTRATO_MINIMO_v0.1_CANDIDATO.md).

**El documento no está ratificado por ambos integrantes.** Los RPC, esquemas, rutas y mecanismos de autenticación propuestos **no están autorizados para implementación definitiva** hasta cerrar D-01 a D-04. La preparación técnica de APP-00 es independiente de ese acuerdo.

Estados mínimos del tenant **propuestos en el documento**: `PENDIENTE_ADMIN`, `ACTIVA` y `SUSPENDIDA`. No están implementados en APP-00.

## Estructura actual (APP-00)

```text
Saas.Platform.sln
src/
  Saas.Platform.Api/
    Program.cs
    Saas.Platform.Api.csproj
    appsettings.json
    appsettings.Development.json
    Properties/launchSettings.json
tests/
  Saas.Platform.Tests/
    HealthEndpointsTests.cs
    Saas.Platform.Tests.csproj
Dockerfile
.dockerignore
.github/workflows/ci.yml
```

No se crean carpetas vacías (`Features/`, `Domain/`, `Infrastructure/`, `Common/`) ni slices de ejemplo. Cuando se autorice el primer caso de uso real, se agregará como slice autocontenido en `Features/`.

## Prerrequisitos

- **.NET 10 SDK** (`10.0.x`) para restaurar, compilar, ejecutar y probar localmente.
- **Docker** (opcional localmente) para construir y verificar la imagen de contenedor.

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

`tests/Saas.Platform.Tests/` ejecuta pruebas HTTP reales en memoria contra `Program` mediante `WebApplicationFactory<Program>`, sin requerir PostgreSQL, Docker ni Kubernetes. Cubre `/health/live`, `/health/ready` y respuesta `404` ante rutas inexistentes.

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
- **Kubernetes (LAB-07), Argo CD (LAB-08) y CD mediante GitOps (LAB-09, posteriores):** la promoción de versión desplegada ocurre mediante PR en `saas-infra` y sincronización de Argo CD. Fusionar a `main` o publicar en GHCR **no** demuestra despliegue en K3s.

Ver [convenciones Git/CI/CD](docs/engineering/GIT-CI-CD.md), [alcance APP-00](docs/APP-00-ALCANCE.md), [evidencia APP-00](docs/APP-00-VALIDATION.md) y [evidencia APP-01](docs/APP-01-VALIDATION.md).

## Estado

**APP-00 integrado en `develop`; APP-01 / LAB-06 en preparación en `feature/app-01-ghcr-publication`.** Sin lógica de negocio, persistencia, autenticación, despliegue K3s ni integración con Identity. La publicación real en GHCR se verificará únicamente al promover `develop` → `main`.
