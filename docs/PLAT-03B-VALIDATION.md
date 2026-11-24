# Evidencia de validación PLAT-03B — Integración real Platform–Identity (gRPC v1)

**Fecha:** 2026-11-24  
**Resultado global:** `PASS`  
**Alcance:** Validación en vivo de extremo a extremo entre `saas-platform` y `saas-identity` mediante HTTP/2 (`h2c`) gRPC real (`TenantAccessService.GetTenantAccessState`) con bases PostgreSQL sintéticas desechables en Docker Compose. Sin cambios funcionales, sin modificaciones al `.proto` v1 ni al repositorio `saas-identity`.

---

## 1. Versiones y commits verificados

| Repositorio | Rama / Referencia | Commit SHA |
|---|---|---|
| `maverick0322/saas-platform` | `develop` (base) | `0cd248f796ec0a882903f0f4e20249ae0ce4622c` |
| `Eugene1805/saas-identity` | `HEAD` (solo lectura) | `a71c1e7aebbdc5282723bbce0f70c28d44d54b52` |

---

## 2. Topología Docker aislada y desechable

- **Proyecto Docker Compose aislado:** `plat03b-validation` (red bridge privada `plat03b-validation_default`, sin volúmenes persistentes reutilizados y con secretos efímeros generados en memoria solo para la sesión).
- **Bases de datos PostgreSQL (`postgres:17-alpine`, desechables):**
  - `platform-db`: base global de Platform (`saas_platform_val`), migrada con las 4 migraciones EF Core de `saas-platform` (`InitialTenantCatalog`, `AddTenantOperationalStateAndProvisioningOperations`, `AddProvisioningWorkerLeaseAndStageProgress`, `AddTenantMaxActiveAccounts`).
  - `identity-global-db`, `identity-norte-db`, `identity-valle-db`, `identity-aurora-db`: bases aisladas de `saas-identity`.
- **Servicios:**
  - `platform-api`: compilado desde `saas-platform/Dockerfile` con `Platform__RuntimeMode=Api`, `Ports__Http=8080`, `InternalGrpc__Enabled=true`, `InternalGrpc__Port=8092`, `ProvisioningWorker__Enabled=false` y clave efímera compartida en `InternalGrpc__InternalServiceKey`.
  - `identity-api`: compilado desde `Eugene1805/saas-identity/Dockerfile` (sin modificar archivos del repositorio) con `ASPNETCORE_ENVIRONMENT=Development`, `Platform__Mode=Grpc`, `Platform__GrpcAddress=http://platform-api:8092`, `Platform__InternalServiceKey` efímero igual al de Platform, `Platform__TimeoutSeconds=3`, `SeedDemoData=true` y `Email__Mode=LogOnly`.
  - `identity-api-badkey` (perfil `badkey`): segunda instancia de Identity configurada con una clave distinta en `Platform__InternalServiceKey` para probar rechazo de autenticación servicio-a-servicio.

### Datos sintéticos sembrados en `platform-db` (coincidentes con UUIDs de Identity)

| `id` (UUID) | `public_id` (slug) | `operational_state` inicial | `max_active_accounts` inicial |
|---|---|---|---|
| `10000000-0000-0000-0000-000000000001` | `clinica-norte` | `ACTIVA` | `17` |
| `10000000-0000-0000-0000-000000000002` | `clinica-valle` | `SUSPENDIDA` | `9` |
| `10000000-0000-0000-0000-000000000003` | `clinica-aurora` | `PENDIENTE_ADMIN` | `5` |
| `10000000-0000-0000-0000-000000000004` | `clinica-sin-cuota` | `ACTIVA` | `NULL` |

---

## 3. Escenarios ejecutados, evidencia y resultados

| # | Escenario | Acción / Entrada | Resultado observado | Estado |
|---|---|---|---|---|
| 1 | Salud de ambos servicios | `GET /health/ready` en Platform (`:18080`) e Identity (`:18081`) | Platform `200 OK` (`process-only`); Identity `200 OK` (`database: Connected`) | `PASS` |
| 2 | Clínica pendiente (`PENDIENTE_ADMIN`) | `POST /v1/auth/login` en `clinica-aurora` | HTTP `403` `TENANT_PENDING_ADMIN` | `PASS` |
| 3 | Clínica suspendida (`SUSPENDIDA`) | `POST /v1/auth/login` en `clinica-valle` (`admin@valle.vital.demo`) | HTTP `403` `TENANT_SUSPENDED` | `PASS` |
| 4 | Clínica activa + `/v1/auth/me` + cuota desde Platform | `POST /v1/auth/login`, `GET /v1/auth/me`, `GET /v1/staff` en `clinica-norte` | Login `200 OK`; `/v1/auth/me` `200 OK` (`tenantId: 10000000-...-0001`); `/v1/staff` `200 OK` con `maxActiveAccounts: 17` leído de Platform | `PASS` |
| 5 | Cambio dinámico de cuota en BD + bloqueo duro + aislamiento | `UPDATE platform.tenants SET max_active_accounts = 4` (sin reiniciar Identity) → `GET /v1/staff`, `POST /v1/staff`, y login de `admin@valle.vital.demo` en `clinica-norte` | `/v1/staff` refleja `maxActiveAccounts: 4`; `POST /v1/staff` responde HTTP `409` `QUOTA_REACHED` (`4/4 cuentas activas`); login cruzado responde HTTP `401` `INVALID_CREDENTIALS` | `PASS` |
| 6 | Cambio dinámico `ACTIVA` → `SUSPENDIDA` | `UPDATE platform.tenants SET operational_state = 'SUSPENDIDA'` → `GET /v1/auth/me` y `POST /v1/auth/refresh` | Ambos responden inmediatamente HTTP `403` `TENANT_SUSPENDED` | `PASS` |
| 7 | Cuota faltante (`NULL`) con fallo cerrado | `POST /v1/auth/login` en `clinica-sin-cuota` | HTTP `503` `TENANT_STATE_UNAVAILABLE`; log Identity: `GrpcTenantAccessStateAdapter` `gRPC StatusCode=FailedPrecondition` | `PASS` |
| 8 | Clínica inexistente (`NotFound` vs `503`) | `POST /v1/auth/login` en `clinica-inexistente-99` | HTTP `404` `TENANT_NOT_FOUND` (distinción limpia frente a `503`) | `PASS` |
| 9 | Clave interna gRPC incorrecta | `POST /v1/auth/login` vía `identity-api-badkey` (`clinica-norte` activa) | HTTP `503` `TENANT_STATE_UNAVAILABLE`; log Platform: `Rejected unauthenticated gRPC GetTenantAccessState call.`; log Identity: `gRPC StatusCode=Unauthenticated` | `PASS` |
| 10 | Indisponibilidad de Platform | `docker compose stop platform-api` → `POST /v1/auth/login` y `GET /v1/auth/me` | Ambos responden HTTP `503` `TENANT_STATE_UNAVAILABLE`; log Identity: `GrpcTenantAccessStateAdapter` `gRPC StatusCode=DeadlineExceeded` / `StatusCode=Unavailable` | `PASS` |

### Confirmación de adaptador gRPC real (no `Local`)

Los logs de `identity-api` e `identity-api-badkey` registraron explícitamente `Saas.Identity.Infrastructure.Platform.GrpcTenantAccessStateAdapter` (`QueryPlatformAsync`, `/src/src/Saas.Identity.Infrastructure/Platform/GrpcTenantAccessStateAdapter.cs:line 64`), y las actualizaciones directas en `platform-db` (`max_active_accounts` `17 -> 4` y `operational_state` `ACTIVA -> SUSPENDIDA`) surtieron efecto inmediato en `identity-api` sin reinicios.

---

## 4. Incompatibilidades y hallazgos

- **Incompatibilidades de contrato o transporte:** **ninguna detectada.** El contrato `.proto` v1, los códigos gRPC (`NotFound`, `Unauthenticated`, `FailedPrecondition`, `Unavailable`), la cabecera `x-internal-service-key`, `may_authenticate`, `max_active_accounts` y `evaluated_at_gmt6` interoperaron sin parches en ninguno de los dos servicios.
- **Pendientes fuera de alcance:**
  - Exponer el puerto gRPC `8092`, la configuración `InternalGrpc__*` y las políticas de red en `saas-infra` / K3s mediante un incremento dedicado.
  - Implementar y validar en vivo el cliente gRPC `ProvisionInitialClinicAdmin` y los adaptadores de trabajador (CloudNativePG y Clinical) en incrementos posteriores aprobados.

---

## 5. Procedimiento reproducible (sin secretos en Git)

1. Disponer de `saas-platform` en `develop` (`0cd248f`) y una copia de solo lectura de `Eugene1805/saas-identity` (`a71c1e7`).
2. Levantar 5 instancias temporales `postgres:17-alpine` (una para Platform y cuatro para Identity: global, norte, valle y aurora) en una red Docker Compose aislada, generando contraseñas y claves en variables de entorno efímeras en memoria.
3. Ejecutar `dotnet ef database update --project src/Saas.Platform.Api/Saas.Platform.Api.csproj` contra la base temporal de Platform y sembrar los registros sintéticos en `platform.tenants` alineados a los UUIDs `10000000-0000-0000-0000-000000000001..0004`.
4. Arrancar `platform-api` con `InternalGrpc__Enabled=true`, `InternalGrpc__Port=8092` e `InternalGrpc__InternalServiceKey` efímero, e `identity-api` con `Platform__Mode=Grpc`, `Platform__GrpcAddress=http://platform-api:8092`, la misma clave en `Platform__InternalServiceKey`, `SeedDemoData=true` y `Email__Mode=LogOnly`.
5. Ejecutar los 10 escenarios anteriores contra Identity (`login`, `/v1/auth/me`, `/v1/auth/refresh`, `/v1/staff`, clave errónea y parada de `platform-api`) y finalizar con `docker compose ... down -v --remove-orphans`.
