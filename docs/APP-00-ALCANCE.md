# APP-00 — Base técnica verificable de Platform

**Estado:** propuesta de incremento técnico para ejecución mediante agente, sin lógica de negocio. **Repositorio:** `maverick0322/saas-platform`. **Arquitectura ratificada para Platform:** Vertical Slice Architecture. **Rama objetivo del PR:** `develop`.

## 1. Propósito

Demostrar que Platform puede restaurarse, compilarse, ejecutarse, probarse y empaquetarse como contenedor de forma independiente. No implementar reglas del catálogo de tenants ni las integraciones aún no ratificadas.

## 2. Alcance incluido

1. Solución .NET 10 de un solo proyecto ASP.NET Core mínimo `src/Saas.Platform.Api/`, preferiblemente `Microsoft.NET.Sdk.Web`, más un proyecto de pruebas `tests/Saas.Platform.Tests/`. No crear proyectos `Domain`, `Application` o `Infrastructure` vacíos.
2. Endpoint técnico `GET /health/live` que indique que el proceso está vivo (HTTP 200) y `GET /health/ready` con semántica inicial limitada a la disponibilidad del propio proceso (HTTP 200, sin sondear dependencias inexistentes). Utilizar health checks integrados; no presentarlos como pruebas de acceso a BD/Identity.
3. `ILogger` nativo y salida a consola, sin registro de secretos/datos clínicos. No instalar un stack de observabilidad.
4. Prueba HTTP de al menos un endpoint a través de `WebApplicationFactory` o `TestServer`, sin PostgreSQL real, Docker ni mocks de servicios de negocio. Si se prueban ambos endpoints, mejor; evitar tests cosméticos.
5. Dockerfile multietapa .NET 10 (SDK y ASP.NET runtime), `.dockerignore`, puerto 8080 no privilegiado y usuario no root si la imagen lo permite; confirmar ejecución de los health checks del contenedor cuando el entorno lo permita.
6. Workflow `.github/workflows/ci.yml`: PR hacia `develop` y `main`, y push en ramas `develop`/`main`; `dotnet restore`, `build` y `test` sin publicación, además de `docker build` de verificación. Usar runners alojados por GitHub y permisos mínimos, `contents: read`. Sin secretos para PR, sin GHCR todavía.
7. Actualizar README: prerequisitos, comandos locales, endpoints, pruebas, Docker, distinción CI vs GHCR vs GitOps y estado real. Registrar en docs el resultado de la validación y actualizar el checklist REPO-00 **solo con hechos comprobados**.
8. Agregar `GEMINI.md` en raíz con las políticas del repositorio.

**Forma de las funcionalidades:** no crear `Features/CreateTenant` ni slices de ejemplo. Cuando se apruebe el primer caso de uso, se agregará un slice autocontenido en `Features/` con endpoint, handler, validación y datos si los requiere. `GEMINI.md` documenta esta regla.

## 3. Fuera de alcance

- CRUD de tenants, planes, suspensión, activación, aprovisionamiento del admin inicial, estados administrativos implementados.
- Contratos gRPC/HTTP Platform–Identity candidatos; Identity, Clinical, Billing, BI u otro servicio.
- EF Core, PostgreSQL, migraciones, Testcontainers o configuración tenant por base de datos antes de tener persistencia autorizada.
- Autenticación/RBAC de negocio; JWT de prueba inventados; middleware de resolución de tenant.
- Redis, RabbitMQ, outbox, Saga, MediatR, librerías de mediador, CQRS distribuido, event sourcing.
- OpenAPI de negocio ficticia, librerías de dominio compartidas, múltiples proyectos de capa sin código real.
- Publicación GHCR, GitOps/Argo CD, Kubernetes, otra VM o entornos adicionales en APP-00.
- Cambios directos a `main`, `develop`, rulesets o repositorios ajenos.

## 4. Secuencia propuesta

**APP-00A — API y pruebas:** crear proyecto mínimo y tests; comprobar localmente restauración, build, tests y respuesta HTTP. **APP-00B / LAB-05 — Contenedor y CI:** agregar Dockerfile y workflow, validar en PR a `develop`. Separar commits según sea útil, pero un PR de incremento es suficiente. **APP-01 / LAB-06 posterior:** preparar y verificar publicación GHCR tras PR y promoción a `main`, sin confundirla con despliegue. **LAB-07 / LAB-08 / LAB-09 posteriores:** Kubernetes (LAB-07), Argo CD (LAB-08) y CD mediante GitOps en `saas-infra` (LAB-09).

## 5. Criterios de aceptación

- [ ] El nuevo código vive en `feature/app-00-platform-baseline` creada desde `develop` y ninguna rama protegida recibe push directo.
- [ ] La solución funciona con .NET 10 y no introduce servicios de negocio o paquetes injustificados.
- [ ] `dotnet restore`, `dotnet build` y `dotnet test` concluyen satisfactoriamente en el entorno disponible.
- [ ] Los health checks funcionan mediante prueba HTTP automatizada; el README explica qué significan y qué no prueban.
- [ ] `docker build` produce una imagen con runtime .NET 10 y su arranque local se comprueba si se dispone de Docker; si no, se registra `NOT_RUN` y se valida en CI.
- [ ] CI del PR ejecuta tests y construcción de imagen sin publicar en GHCR, y los checks quedan verdes; si aún no se ejecutó remotamente, estado `PENDING`.
- [ ] El diff no incluye contraseñas, claves, secretos, endpoints ni entidades no aprobadas.
- [ ] GEMINI.md y README reflejan exactamente el alcance y estado del incremento.

## 6. Evidencias

Guardar URL y SHA del PR, versión SDK, resultados de `dotnet build/test`, resultado de `docker build`, checks de Actions y los comandos curl o equivalentes para los endpoints. No reportar GHCR ni Argo CD como cumplidos dentro de APP-00.

## 7. Dependencias y pendientes

El contrato Platform–Identity v0.1 contiene D-01 a D-04 sin ratificar y no bloquea APP-00. Para APP-01 se requerirá revisar la promoción en `main`, permisos de GHCR y nombres estables de checks. Para GitOps se requerirá K3s/Argo CD disponible y manifiestos declarados en `saas-infra`.
