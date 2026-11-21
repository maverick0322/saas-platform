# APP-00 — Evidencia y estado de validación

- **Rama:** `feature/app-00-platform-baseline` (creada desde `develop` en `4d5b53c`)
- **Alcance:** base técnica mínima ASP.NET Core .NET 10 (health checks técnicos, pruebas HTTP en memoria, Dockerfile multietapa, `.dockerignore` y workflow CI sin publicación GHCR).
- **PR / SHA remoto:** `PENDING` (sin commit ni push todavía).

## Entorno local inspeccionado

- **SO:** Windows (`win-x64`)
- **SDK .NET instalado localmente:** `9.0.306` (no se encontró SDK `.NET 10.0` en la máquina)
- **Docker CLI:** `29.8.2` (daemon de Docker Desktop detenido; `npipe:////./pipe/dockerDesktopLinuxEngine` no disponible)

## Matriz de verificación

| Verificación | Comando | Estado | Detalle |
|---|---|---|---|
| Creación de solución | `dotnet new sln -n Saas.Platform` + `dotnet sln add` | `PASS` | `Saas.Platform.sln` creada con `Saas.Platform.Api` y `Saas.Platform.Tests`. |
| Restauración .NET 10 | `dotnet restore Saas.Platform.sln` | `FAIL` | Bloqueado localmente por entorno: `NETSDK1045` (SDK local `9.0.306` no soporta destino `net10.0`). |
| Compilación .NET 10 | `dotnet build Saas.Platform.sln --configuration Release --no-restore` | `NOT_RUN` | Dependiente de SDK `.NET 10.0` o ejecución en CI. |
| Pruebas automatizadas | `dotnet test Saas.Platform.sln --configuration Release --no-build` | `NOT_RUN` | Dependiente de SDK `.NET 10.0` o ejecución en CI. |
| Construcción Docker | `docker build --file Dockerfile --tag saas-platform:local .` | `NOT_RUN` | Daemon de Docker Desktop no está en ejecución localmente. |
| Smoke test contenedor | `docker run` + `curl /health/live` y `/health/ready` | `NOT_RUN` | Daemon de Docker Desktop no está en ejecución localmente. |
| CI remoto (GitHub Actions) | `.github/workflows/ci.yml` | `PENDING` | Pendiente de commit, push y apertura de PR hacia `develop`. |
| Publicación GHCR | N/A | `OUT_OF_SCOPE` | Corresponde a APP-01 / LAB-06 tras promoción a `main`. |
| Despliegue K3s / Argo CD / GitOps | N/A | `OUT_OF_SCOPE` | Corresponde a LAB-07 (Kubernetes), LAB-08 (Argo CD) y LAB-09 (CD mediante GitOps en `saas-infra`). |

## Dependencias introducidas

### `src/Saas.Platform.Api`
- **Ningún paquete NuGet externo.** Usa exclusivamente las capacidades nativas de `Microsoft.NET.Sdk.Web` (.NET 10): Minimal APIs, `AddHealthChecks`/`MapHealthChecks`, `ILogger` y `System.Text.Json`.

### `tests/Saas.Platform.Tests`
- `Microsoft.AspNetCore.Mvc.Testing` (`10.0.15`): aloja el pipeline HTTP real de `Program` en memoria vía `WebApplicationFactory<Program>`.
- `Microsoft.NET.Test.Sdk` (`18.12.0`): infraestructura estándar de descubrimiento y ejecución de pruebas para `dotnet test` / VSTest.
- `xunit.v3` (`3.2.2`): framework de pruebas mantenido (xUnit v3, con `<OutputType>Exe</OutputType>`).
- `xunit.runner.visualstudio` (`3.1.5`): adaptador de ejecución de xUnit v3 para `dotnet test` / VSTest / IDEs.

## Próximos pasos para completar la evidencia

1. Instalar **.NET 10 SDK** localmente (o ejecutar el workflow en GitHub Actions al abrir el PR) y confirmar `PASS` en `dotnet restore`, `dotnet build` y `dotnet test`.
2. Iniciar **Docker Desktop** localmente (o validar el job `docker-build` en GitHub Actions) y confirmar `PASS` en `docker build` y las respuestas HTTP 200 de `/health/live` y `/health/ready`.
3. Actualizar este archivo con la URL del PR, SHA del commit y estado final de los checks de Actions.
