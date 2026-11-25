# Evidencia de validación PLAT-04C — Integración real de la API administrativa Platform–Identity

- **Fecha:** 2026-11-24
- **Dictamen:** **`INTEGRATION_PASS`**
- **Alcance:** Validación en vivo de extremo a extremo entre `saas-platform` (`AdminApi` + `InternalGrpc`) y `saas-identity` (`IDENTITY-SEC-01` RS256 + `Platform:Mode=Grpc`) mediante Docker Compose aislado y bases PostgreSQL 17 desechables. Sin cambios funcionales de código, sin nuevas migraciones, sin modificar `platform_identity.proto` ni usar dobles HTTP en reemplazo de Identity.

---

## 1. Versiones y commits verificados

| Componente / Repositorio | Rama / Referencia | Versión / Commit SHA |
| --- | --- | --- |
| `maverick0322/saas-platform` | `develop` (base) | `efe1116fe592ffcfc4635e08a4bbe8763c7cdaff` |
| `Eugene1805/saas-identity` | `main` (clon temporal de solo lectura) | `a374584b445e7a09f55e4d9535f09439c4f65797` |
| Motor de base de datos | Imagen oficial | `postgres:17-alpine` |
| Runtime .NET | Imágenes oficiales en `Dockerfile` | `mcr.microsoft.com/dotnet/sdk:10.0` / `aspnet:10.0` |

---

## 2. Topología del laboratorio aislado (`plat04c-validation`)

- **Proyecto Docker Compose:** `plat04c-validation` (red bridge aislada `plat04c-validation_default`, fuera del repositorio y eliminada al finalizar).
- **Exposición de puertos:** únicamente `127.0.0.1:18080 -> platform-api:8080` y `127.0.0.1:18081 -> identity-api:8080`. Ni PostgreSQL ni gRPC (`8092` / `8082`) se expusieron públicamente en el host.
- **Bases de datos PostgreSQL 17 desechables:**
  - `platform-db`: catálogo global `saas_platform_val`.
  - `identity-global-db`: cuentas globales (`superadmin@vital.demo`) y refresh tokens globales.
  - `identity-norte-db`, `identity-valle-db`, `identity-aurora-db`: bases aisladas de clínicas sintéticas según la configuración de `saas-identity`.
- **Servicios:**
  - `platform-api`: compilado desde `saas-platform/Dockerfile` (`Platform__RuntimeMode=Api`, `ProvisioningWorker__Enabled=false`, `AdminApi__Enabled=true`, `InternalGrpc__Enabled=true`).
  - `identity-api`: compilado desde `Eugene1805/saas-identity/Dockerfile` (`a374584b445e7a09f55e4d9535f09439c4f65797`, `ASPNETCORE_ENVIRONMENT=Development`, `Platform__Mode=Grpc`, `SeedDemoData=true`, `Email__Mode=LogOnly`).

---

## 3. Configuración reproducible (sin secretos)

Todos los secretos y material criptográfico se generaron de forma efímera por ejecución únicamente en un directorio temporal desechable (`%TEMP%\plat04c-validation\`):

1. **Material criptográfico efímero:**
   - Par RSA 2048 bits (`jwt-private.pem` / `jwt-public.pem` en formato SPKI PEM).
   - Par RSA 2048 bits adicional (`wrong-private.pem`) usado **exclusivamente** para generar el token manipulado/negativo del escenario `C.14`.
   - Secreto HMAC independiente de 32 bytes hex (`JWT_HMAC_KEY`) entregado solo a Identity.
   - Clave compartida gRPC efímera (`INTERNAL_GRPC_KEY`).

2. **Variables de `platform-api` (recibe exclusivamente la clave pública RSA):**
   - `ASPNETCORE_ENVIRONMENT=Production`
   - `ConnectionStrings__PlatformDatabase=<ephemeral-postgres-connection>`
   - `Platform__RuntimeMode=Api`, `Ports__Http=8080`
   - `AdminApi__Enabled=true`
   - `Jwt__Issuer=saas-identity`, `Jwt__Audience=vital-saas`, `Jwt__PublicKeyPemPath=/keys/jwt-public.pem`
   - `IdentityAuth__BaseAddress=http://identity-api:8080`, `IdentityAuth__TimeoutSeconds=3`
   - `InternalGrpc__Enabled=true`, `InternalGrpc__Port=8092`, `InternalGrpc__InternalServiceKey=<ephemeral-grpc-key>`
   - `ProvisioningWorker__Enabled=false`

3. **Variables de `identity-api`:**
   - `ASPNETCORE_ENVIRONMENT=Development`
   - `ConnectionStrings__IdentityGlobalDb=<ephemeral-identity-global-connection>`
   - `TenantDatabases__Connections__10000000-0000-0000-0000-000000000001..0003=<ephemeral-tenant-connections>`
   - `Jwt__Issuer=saas-identity`, `Jwt__Audience=vital-saas`
   - `Jwt__PrivateKeyPemPath=/keys/jwt-private.pem`, `Jwt__PublicKeyPemPath=/keys/jwt-public.pem`, `Jwt__RefreshTokenHmacKey=<ephemeral-hmac-key>`
   - `Platform__Mode=Grpc`, `Platform__GrpcAddress=http://platform-api:8092`, `Platform__InternalServiceKey=<ephemeral-grpc-key>`, `Platform__TimeoutSeconds=3`
   - `SeedDemoData=true`, `DemoSeed__Password=<ephemeral-demo-password>`, `Email__Mode=LogOnly`

---

## 4. Procedimiento de migraciones y arranque

1. Iniciar los 5 contenedores `postgres:17-alpine` y esperar estado `healthy`.
2. Aplicar explícitamente las 4 migraciones EF Core de Platform contra `platform-db` (sin `EnsureCreated` ni migraciones nuevas):
   - `20261122180000_InitialTenantCatalog`
   - `20261124053749_AddTenantOperationalStateAndProvisioningOperations`
   - `20261124070500_AddProvisioningWorkerLeaseAndStageProgress`
   - `20261124093133_AddTenantMaxActiveAccounts`
3. Sembrar en `platform-db` únicamente la clínica sintética `clinica-norte` (`10000000-0000-0000-0000-000000000001`, `ACTIVA`, `max_active_accounts = 17`) como fixture de laboratorio para las pruebas de login clínico real por gRPC (`C.15`, `E.28`).
4. Arrancar `platform-api` e `identity-api` y comprobar `/health/ready`:
   - Platform (`127.0.0.1:18080/health/ready`): `200 OK` (`{"status":"Healthy","service":"saas-platform","probe":"ready","scope":"process-only"}`)
   - Identity (`127.0.0.1:18081/health/ready`): `200 OK` (`{"status":"Healthy","service":"saas-identity","probe":"ready","scope":"database","database":"Connected"}`)

---

## 5. Matriz de escenarios obligatorios (31/31 `PASS`)

> **Distinción explícita:** todos los escenarios positivos y las verificaciones `C.13`, `C.15`–`C.20`, `D.21`–`D.27`, `E.28`–`E.31` se ejecutaron contra los contenedores reales de Platform e Identity con tokens emitidos realmente por Identity. Únicamente `C.14` utilizó un token manipulado/firmado con clave distinta como fixture negativo, y `C.17`–`C.20` + el alta inicial de `clinica-norte` y actualización de cuota en `E.28` usaron consultas SQL directas como **fixtures de validación** (sin constituir funcionalidades administrativas implementadas). Los tiempos indicados son observaciones de laboratorio, no garantías de rendimiento en producción.

| ID | Grupo | Petición / Operación | Resultado esperado | Resultado observado | Estado | Evidencia verificable (sanitizada) |
| --- | --- | --- | --- | --- | --- | --- |
| `A.1` | A | `POST /v1/auth/platform/login` (`superadmin@vital.demo`) | `200 OK` con Bearer JWT | `HTTP 200`, `tokenType=Bearer`, `role=SUPER_ADMIN` | `PASS` | Cuenta `sub=20000000-0000-0000-0000-000000000001` (`193.4 ms`) |
| `A.2` | A | Inspección del JWT emitido por Identity | JWT `RS256` de 3 segmentos | `alg=RS256`, `typ=JWT`, 3 segmentos | `PASS` | Header `{"alg":"RS256","typ":"JWT"}` |
| `A.3` | A | Validación de claims del JWT real | `iss=saas-identity`, `aud=vital-saas`, `role=SUPER_ADMIN`, `exp-iat=900s`, `sub` UUID, sin `tenant_id` | Todos los claims conformes; `tenant_id_present=False` | `PASS` | Claims presentes: `['aud','email','exp','full_name','iat','iss','jti','nbf','role','sub']` |
| `A.4` | A | `GET /v1/auth/me` en Identity con JWT real | `200 OK` global activo coincidente con `sub` | `HTTP 200`, `id==sub=True`, `tenantId=None`, `active=True`, `role=SUPER_ADMIN` | `PASS` | `HTTP 200` (`54.1 ms`) |
| `B.5` | B | `POST /v1/tenants` (`clinica-val-01`, cuota `12`) | `201 Created` + `Location: /v1/tenants/{id}` | `HTTP 201`, `Location=/v1/tenants/01a2110e-b8e3-76ae-81d8-f02464ac29e0` | `PASS` | `operationId=01a2110e-b8e3-7db2-9855-8530322f73fe` (`825.2 ms`) |
| `B.6` | B | Verificación SQL en `platform-db` tras `B.5` | Exactamente 1 tenant y 1 operación durable | `1` fila `PENDIENTE_ADMIN` (`12`) y `1` fila `PENDING` (`attempt_count=0`) | `PASS` | `tenants: 1\|PENDIENTE_ADMIN\|12`; `ops: 1\|PENDING\|0\|PENDING,PENDING,PENDING,PENDING` |
| `B.7` | B | Estado inicial y 4 etapas pendientes | `PENDIENTE_ADMIN`, `mayAuthenticate=false`, `PENDING`, 4 etapas `PENDING` | Exacto en HTTP `201` y en columnas PostgreSQL | `PASS` | `stages={infrastructureInstance,serviceDatabases,clinicalPreparation,identityPreparation}: PENDING` |
| `B.8` | B | `GET /v1/tenants?page=1&pageSize=20` | `200 OK` listado paginado determinista | `HTTP 200`, `totalCount=2`, `totalPages=1` | `PASS` | `slugs=['clinica-val-01', 'clinica-norte']` (`210.8 ms`) |
| `B.9` | B | `GET /v1/tenants/{id}` (`01a2110e-...`) | `200 OK` ficha administrativa completa | `HTTP 200`, `publicId=clinica-val-01`, `status=PENDING` | `PASS` | `contactEmail=contacto@val01.vital-demo.test` (`91.5 ms`) |
| `B.10` | B | `POST /v1/tenants` con slug `"  CLINICA-VAL-01  "` | `409 TENANT_PUBLIC_ID_CONFLICT` + `existingTenantId` | `HTTP 409`, `code=TENANT_PUBLIC_ID_CONFLICT`, `existingTenantId=01a2110e-b8e3-76ae-81d8-f02464ac29e0` | `PASS` | Coincide 1:1 con el ID de `B.5` (`49.5 ms`) |
| `B.11` | B | Verificación SQL anti-duplicados tras `B.10` | Exactamente 1 tenant y 1 operación | `tenants=1`, `operations=1` | `PASS` | Restricción `ux_tenants_public_id` sin duplicados |
| `B.12` | B | Conformidad OpenAPI `platform-admin.v1.yaml` | Claves exactas, enums textuales, UTC `Z`, sin campos internos | Cero campos internos expuestos; esquemas/fechas conformes | `PASS` | Verificado en respuestas `201`, `200` listado y `200` detalle; sin cuentas ni BD creadas |
| `C.13` | C | Peticiones sin token a los 3 endpoints admin | `401 Unauthorized` + `WWW-Authenticate: Bearer` | Los 3 endpoints devolvieron `401`, `WWW-Authenticate: Bearer` y `UNAUTHORIZED` | `PASS` | `POST->401`, `GET list->401`, `GET detail->401` |
| `C.14` | C | Token manipulado y token firmado con otra clave RSA *(fixture negativo)* | `401 Unauthorized` + `WWW-Authenticate: Bearer` | Ambos devolvieron `401` + `WWW-Authenticate: Bearer` | `PASS` | `manipulado->401`; `otra_clave_rsa->401` |
| `C.15` | C | Login clínico real (`admin@norte.vital.demo`) vs 3 endpoints admin | Login `200 OK`; `403 FORBIDDEN` en los 3 endpoints | `clinic_login=200`; `POST->403`, `GET list->403`, `GET detail->403` | `PASS` | Rechazo de JWT clínico real con `code=FORBIDDEN` |
| `C.16` | C | Cabeceras falsificadas `X-Role: SUPER_ADMIN` | `401` sin token; `403` con token clínico | `sin_token=401`, `jwt_clinico=403` | `PASS` | Cabeceras del cliente ignoradas |
| `C.17` | C | Fixture SQL `active = FALSE` para SuperAdmin en `identity-global-db` | `/v1/auth/me` rechaza la cuenta | `/v1/auth/me` devolvió `HTTP 403` (`ACCOUNT_DISABLED`) | `PASS` | Fixture aplicado exclusivamente para validación |
| `C.18` | C | `POST /v1/tenants` con JWT previo tras desactivar cuenta | `403 FORBIDDEN` vía `/v1/auth/me` real | `HTTP 403`, `code=FORBIDDEN` | `PASS` | Rechazo en vivo (`8.9 ms`), sin propagar cuerpo de Identity |
| `C.19` | C | `GET /v1/tenants` y detalle con JWT previo (`D-HTTP-01`) | `200 OK` por validación `RS256` local | `list=200`, `detail=200` | `PASS` | Comportamiento stateless esperado en lecturas |
| `C.20` | C | Restaurar fixture SQL `active = TRUE` en `identity-global-db` | `/v1/auth/me` recupera `200 OK` | `/v1/auth/me HTTP 200` | `PASS` | Cuenta sintética restaurada |
| `D.21` | D | `docker compose stop identity-api` | `identity-api` detenido | `exit=0` | `PASS` | Servicio detenido temporalmente |
| `D.22` | D | `POST /v1/tenants` con JWT vigente e `identity-api` detenido | `503 SERVICE_UNAVAILABLE` dentro de plazo compatible con `3s` | `HTTP 503`, `code=SERVICE_UNAVAILABLE` (`3010.1 ms` observados en lab) | `PASS` | Fallo cerrado coherente con timeout `3s` + `HttpClient` |
| `D.23` | D | `GET /v1/tenants` y detalle con `identity-api` detenido | `200 OK` por validación `RS256` local | `list=200` (`25.4 ms`), `detail=200` (`6.1 ms`) | `PASS` | Lecturas autónomas ante caída de Identity |
| `D.24` | D | SQL en `platform-db` tras rechazos `403`/`503` | `0` filas para `clinica-blocked-01` / `clinica-down-01` | `rejected_rows=0`, `total_tenants=2`, `total_ops=1` | `PASS` | Sin transacciones parciales ni huérfanas |
| `D.25` | D | `docker compose start identity-api` (sin cambiar claves/secretos) | `/health/ready = 200 OK` | `exit=0`, `ready=True` | `PASS` | Claves RSA y HMAC conservadas |
| `D.26` | D | Recuperación `POST /v1/tenants` (`clinica-rec-02`) + `GET` detalle | `201 Created` y `200 OK` | `post=201` (`207.0 ms`), `get=200` (`id=01a2110e-e291-7b3e-a49a-68298bbeb27d`) | `PASS` | Flujo recuperado automáticamente |
| `D.27` | D | `restart platform-api identity-api` y verificación de persistencia | Servicios `200` y datos intactos en HTTP/SQL | `ready=True`, `http_total=3`, `sql_t=3`, `sql_o=2` | `PASS` | Persistencia en volúmenes PostgreSQL preservada |
| `E.28` | E | Confirmación de consultas gRPC reales (`GrpcTenantAccessStateAdapter`) | Identity consulta `TenantAccessService.GetTenantAccessState` (no `Local`) | Cambio SQL `max_active_accounts 17 -> 9` reflejado inmediato en `GET /v1/staff`; al parar `platform-api`, `/v1/auth/login` devuelve `503 TENANT_STATE_UNAVAILABLE` con log `GrpcTenantAccessStateAdapter` | `PASS` | Evidencia directa en HTTP y logs de `identity-api` |
| `E.29` | E | Login clínico real contra `clinica-val-01` (`PENDIENTE_ADMIN`) | `403 TENANT_PENDING_ADMIN` vía gRPC | `HTTP 403`, `code=TENANT_PENDING_ADMIN` (`182.3 ms`) | `PASS` | Clínica recién registrada no autenticó operativamente |
| `E.30` | E | No circularidad de `SUPER_ADMIN` entre Platform e Identity | `/v1/auth/platform/login` + `/v1/auth/me` responden `200` aun con `platform-api` detenido; `POST /v1/tenants` con `201` sin llamadas gRPC circulares | Con `platform-api` detenido: `platform/login=200`, `/me=200` (`SUPER_ADMIN`, `tenantId=None`); `POST /v1/tenants` (`clinica-nocirc-03`) respondió `201` (`431.2 ms`) sin consultar `GetTenantAccessState` | `PASS` | Flujo acíclico confirmado |
| `E.31` | E | Preservación del contrato `.proto` v1 | `git diff docs/contracts/proto/` vacío | `diff_len=0` | `PASS` | `platform_identity.proto` intacto |

---

## 6. Evidencia sanitizada de logs y persistencia final

### Extractos sanitizados de logs

`platform-api`:
```text
platform-api-1  | info: Saas.Platform.Api[0] Saas.Platform.Api initialized (AdminApi=True, InternalGrpc=True, GrpcPort=8092).
platform-api-1  | info: Saas.Platform.Api.Features.Tenants.RegisterTenant.RegisterTenantHandler[0] Registered tenant 01a2110e-b8e3-76ae-81d8-f02464ac29e0 (clinica-val-01) in state PENDIENTE_ADMIN with durable provisioning operation 01a2110e-b8e3-7db2-9855-8530322f73fe (01a2110e-b8e3-7db2-9855-8530322f73fe).
platform-api-1  | info: TenantAdminEndpoints[0] Administrative tenant created by ActorSubjectId=20000000-0000-0000-0000-000000000001 TenantId=01a2110e-b8e3-76ae-81d8-f02464ac29e0 PublicId=clinica-val-01 OperationId=01a2110e-b8e3-7db2-9855-8530322f73fe TraceId=00-ffc4e0624f22306d4506ebbc3601e7f4-d197eb862c6b1458-00 CreatedAtUtc=11/25/2026 04:55:07 +00:00.
platform-api-1  | warn: TenantAdminEndpoints[0] Administrative tenant registration conflict by ActorSubjectId=20000000-0000-0000-0000-000000000001 PublicId=clinica-val-01 ExistingTenantId=01a2110e-b8e3-76ae-81d8-f02464ac29e0 TraceId=00-810b94593e5ef33f14fa5eda213fdb05-eab8690e446f869f-00.
```

`identity-api` (confirmación del adaptador `GrpcTenantAccessStateAdapter` real al detener `platform-api` en `E.28`):
```text
identity-api-1  | warn: Saas.Identity.Infrastructure.Platform.GrpcTenantAccessStateAdapter[0]
identity-api-1  |       Fallo al consultar GetTenantAccessState en Platform (slug=clinica-norte, gRPC StatusCode=DeadlineExceeded). Aplicando fallo cerrado.
identity-api-1  |       Grpc.Core.RpcException: Status(StatusCode="DeadlineExceeded", Detail="")
identity-api-1  |          at Saas.Identity.Infrastructure.Platform.GrpcTenantAccessStateAdapter.QueryPlatformAsync(GetTenantAccessStateRequest request, String lookupLabel, CancellationToken cancellationToken) in /src/src/Saas.Identity.Infrastructure/Platform/GrpcTenantAccessStateAdapter.cs:line 64
```

### Estado final verificado en `platform-db` antes del teardown

- `platform.tenants`: **4 filas** (`clinica-norte` fixture en `ACTIVA`; `clinica-val-01`, `clinica-rec-02`, `clinica-nocirc-03` en `PENDIENTE_ADMIN`).
- `platform.tenant_provisioning_operations`: **3 filas** (`PENDING`, `attempt_count = 0`), correspondientes exactamente a las 3 altas administrativas realizadas vía `POST /v1/tenants`.

---

## 7. Hallazgos, observaciones operativas y limitaciones

1. **Defectos funcionales entre servicios:** **ninguno detectado.** Todos los flujos `IDENTITY-SEC-01` RS256, `GET /v1/auth/me`, HTTP administrativo (`201`/`200`/`409`+`existingTenantId`/`401`+`WWW-Authenticate`/`403`/`503`) y gRPC `TenantAccessService.GetTenantAccessState` interoperaron conforme a contrato sin parches de código.
2. **Observación operativa de montaje PEM (`$APP_UID`):** herramientas como `openssl genpkey` generan claves privadas con permiso `0600` (`root`), lo cual produce `UnauthorizedAccessException` en contenedores no-root (`app` / `$APP_UID`). En despliegues Kubernetes/Docker con archivos PEM montados (incluyendo la clave pública de Platform y la privada de Identity), debe asegurarse permiso legible por `$APP_UID` (`0640`/`0644` según propietario/FS del secreto montado).
3. **Observación operativa sobre logs gRPC en `Production`:** `TenantAccessGrpcService` registra advertencias y errores (`LogWarning`/`LogError`) y no emite logs `Information` por cada llamada gRPC exitosa (comportamiento adecuado para evitar ruido), por lo que en `E.28` la comprobación en vivo se respaldó además con la actualización dinámica de cuota en `platform-db` (`17 -> 9`) leída inmediatamente por `GET /v1/staff` y la caída controlada de `platform-api` (`GrpcTenantAccessStateAdapter`).
4. **Limitaciones conocidas del alcance actual:**
   - `ProvisioningWorker:Enabled` permanece en `false`; las clínicas registradas quedan en `PENDIENTE_ADMIN` hasta habilitar adaptadores reales (CloudNativePG, Clinical y `ProvisionInitialClinicAdmin`).
   - Las operaciones `GET /v1/tenants` y `GET /v1/tenants/{tenantId}` usan validación local `RS256` (`D-HTTP-01`), conservando la ventana stateless de hasta `15 minutos + 30 segundos` tras logout o desactivación de cuenta.

---

## 8. Procedimiento de limpieza aplicado

Tras registrar los resultados anteriores, el laboratorio aislado se eliminó por completo sin afectar otros recursos ni repositorios:

1. `docker compose -p plat04c-validation -f <temp>/compose.yaml down -v --remove-orphans --rmi local`
2. Eliminación del directorio temporal `%TEMP%\plat04c-validation\` (clon temporal, claves efímeras, `.env` y scripts).
3. Verificación con `docker ps -a --filter name=plat04c`, `docker volume ls --filter name=plat04c` y `docker network ls --filter name=plat04c` (todos vacíos), confirmando `Eugene1805/saas-identity` y `platform_identity.proto` intactos.
