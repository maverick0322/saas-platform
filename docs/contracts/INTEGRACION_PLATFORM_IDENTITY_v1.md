# Integración técnica Platform - Identity (gRPC v1)

Documento de entrega para el desarrollador y agente de `saas-platform`. Este repositorio **no** implementa código de servicio todavía (`APP-00`); aquí queda congelado el contrato Protobuf compartido y las especificaciones exactas que `saas-platform` debe implementar para conectarse con `saas-identity` (ya operativo en Docker).

> Contrato congelado: [`platform_identity.proto`](proto/platform_identity/v1/platform_identity.proto) (`package vital.platform_identity.v1`, `csharp_namespace = "Saas.Contracts.PlatformIdentity.V1"`).

---

## 1. Decisiones ratificadas (D-01 a D-05)

- **D-01 (RPC único idempotente):** `IdentityProvisioningService.ProvisionInitialClinicAdmin` encapsula en un solo RPC la creación de la cuenta inicial `ADMIN_CLINICA` por tenant usando `provisioning_request_id`.
- **D-02 (Contraseña temporal generada y enviada por Identity):** `saas-identity` genera una contraseña temporal aleatoria (12 caracteres: letra, número y símbolo) con `must_change_password = true` y la envía por correo electrónico transaccional al `admin_email`. **Platform nunca recibe, genera ni registra contraseñas.**
- **D-03 (Payloads y errores mínimos gRPC):** contrato `.proto` único con mapeo explícito de `StatusCode` (`InvalidArgument`, `Unauthenticated`, `FailedPrecondition`, `AlreadyExists`, `Unavailable`).
- **D-04 (Resolución y expiración de tokens):** `TenantAccessService.GetTenantAccessState` soporta búsqueda tanto por `tenant_id` (en `/v1/auth/refresh`, `/v1/auth/me` y `/v1/staff*`) como por `slug` (en `/v1/auth/login`). Si una clínica pasa a `SUSPENDIDA`, los Access Tokens JWT previos (máximo 15 min) expiran naturalmente mientras cualquier renovación/operación futura falla de inmediato.
- **D-05 (Cuota máxima `CU-17 A2`):** `GetTenantAccessStateResponse` incluye `max_active_accounts` (`int32`) para que `saas-identity` aplique el bloqueo duro al dar de alta o reactivar personal sin consultar la base de datos de Platform.

---

## 2. Lo que debe implementar `saas-platform`

### A. Cliente gRPC hacia `saas-identity` (`ProvisionInitialClinicAdmin`)

1. **Referencia Protobuf (.csproj de Infrastructure):**
   ```xml
   <PackageReference Include="Google.Protobuf" Version="3.*" />
   <PackageReference Include="Grpc.Net.Client" Version="2.*" />
   <PackageReference Include="Grpc.Tools" Version="2.*" PrivateAssets="All" />
   <Protobuf Include="...\platform_identity\v1\platform_identity.proto" GrpcServices="Client" />
   ```
2. **Dirección destino (HTTP/2 cleartext `h2c`):**
   - Red Docker compartida: `http://identity-api:8082`
   - Desarrollo local en host Windows: `http://localhost:5082`
3. **Autenticación servicio-a-servicio:**
   - Enviar cabecera de metadata gRPC: `x-internal-service-key`.
   - Secreto por defecto en entorno local Docker: `VitalInternalGrpcDevKey2026!` (inyectable por configuración/env var, nunca hardcodeado para producción).
4. **Timeout y reintentos (`CU-11 A3`):**
   - Deadline recomendado por llamada: **5 segundos** (`DateTime.UtcNow.AddSeconds(5)`).
   - Al registrar una clínica (`POST /v1/tenants`), Platform debe:
     1. Persistir primero el tenant en `PENDIENTE_ADMIN` junto con un `provisioning_request_id` único (UUID) en la base privada de Platform.
     2. Invocar `ProvisionInitialClinicAdmin` con ese `provisioning_request_id`.
     3. Transicionar el tenant a `ACTIVA` **únicamente** cuando reciba `PROVISIONING_STATUS_READY` y persista confirmación e `initial_admin_account_id`.
     4. Ante `Unavailable` o `DeadlineExceeded`, mantener `PENDIENTE_ADMIN` conservando exactamente el mismo `provisioning_request_id` para que `POST /v1/tenants/{tenantId}/retry-initial-admin` reintente de forma idempotente.
     5. Solo enviar el **mismo** `provisioning_request_id` si los datos (`tenant_id`, `admin_email`, `admin_full_name`) coinciden; si cambian, generar un nuevo `provisioning_request_id` solo cuando corresponda antes de completarse.

#### Tabla de códigos gRPC devueltos por `saas-identity`

| StatusCode gRPC | Causa | Acción recomendada en `saas-platform` |
|---|---|---|
| `OK` (`READY`) | Cuenta `ADMIN_CLINICA` habilitada y correo despachado (o reintento idempotente de una operación ya `READY`) | Persistir `initial_admin_account_id` y pasar tenant a `ACTIVA`. |
| `InvalidArgument` | `tenant_id` no es UUID, faltan campos o `admin_email` es inválido | Corregir datos antes de invocar; no reintentar igual. |
| `Unauthenticated` | Falta `x-internal-service-key` o clave incorrecta | Revisar secreto interno compartido. |
| `FailedPrecondition` | Mismo `provisioning_request_id` reutilizado con distinto `tenant_id`, `admin_email` o `admin_full_name` (`PROVISIONING_CONFLICT`) | Mantener `PENDIENTE_ADMIN` y generar nueva operación si hubo cambio administrativo legítimo. |
| `AlreadyExists` | Ya existe un `ADMIN_CLINICA` activo o el correo ya está registrado en ese tenant (`INITIAL_ADMIN_ALREADY_EXISTS` / `DUPLICATE_STAFF_EMAIL`) | Evitar duplicación y conciliar estado administrativo de la clínica. |
| `Unavailable` | Fallo al enviar el correo transaccional de clave temporal o indisponibilidad momentánea (`EMAIL_DELIVERY_FAILED`) | Conservar `PENDIENTE_ADMIN` y `provisioning_request_id`; el reintento genera nueva clave temporal para la misma cuenta sin duplicarla. |

---

### B. Servidor gRPC que expondrá `saas-platform` (`GetTenantAccessState`)

Cuando `saas-platform` arranque su servicio real en `APP-00`, deberá exponer el servicio Protobuf `TenantAccessService`:

1. **Puerto HTTP/2 (`h2c`) sugerido:**
   - Contenedor: `8092` (host `5092`). En `saas-identity`, se configura con:
     - `Platform__Mode=Grpc`
     - `Platform__GrpcAddress=http://platform-api:8092` (o `http://host.docker.internal:5092`)
     - `Platform__InternalServiceKey=<secreto compartido>`
     - `Platform__TimeoutSeconds=3`
2. **Seguridad y validación:**
   - Validar el header metadata `x-internal-service-key` y rechazar con `StatusCode.Unauthenticated` si no coincide.
3. **Semántica de resolución (`GetTenantAccessStateRequest`):**
   - Resolver `LookupCase.TenantId` (UUID de clínica) y `LookupCase.Slug` (slug en minúsculas).
   - Si no existe registro para ese `tenant_id` o `slug`, responder `StatusCode.NotFound`.
   - Devolver `may_authenticate = true` **únicamente** cuando `state == TENANT_OPERATIONAL_STATE_ACTIVA`.
   - Devolver `max_active_accounts` vigente del plan asignado a la clínica (`int32`, mínimo `1`).
   - **Prohibido devolver** datos de facturación, teléfono/correo personal del administrador o datos clínicos.
4. **Fallo cerrado en `saas-identity`:**
   - Ante `Unavailable`, `DeadlineExceeded` o `Unauthenticated`, `saas-identity` rechaza logins, refreshes y altas/reactivaciones de personal con HTTP `503 TENANT_STATE_UNAVAILABLE`.

---

## 3. Pruebas rápidas contra `saas-identity` en vivo (`grpcurl`)

`saas-identity` ya expone el servidor gRPC en `localhost:5082` (con `Grpc.AspNetCore.Server.Reflection` habilitado en Development) y modo de correo `LogOnly` en Docker (sin exponer contraseñas en logs ni respuestas).

### Listar servicios disponibles por gRPC Reflection

```powershell
docker run --rm --network saas-identity_default fullstorydev/grpcurl:latest -plaintext identity-api:8082 list
```

### Simular llamada de `saas-platform` para `clinica-aurora` (`PENDIENTE_ADMIN`)

```powershell
docker run --rm --network saas-identity_default fullstorydev/grpcurl:latest `
  -plaintext `
  -H "x-internal-service-key: VitalInternalGrpcDevKey2026!" `
  -d '{"tenant_id":"10000000-0000-0000-0000-000000000003","provisioning_request_id":"prov-aurora-001","clinic_slug":"clinica-aurora","clinic_name":"Unidad Clínica Aurora","admin_full_name":"Mtro. Ernesto Cárdenas","admin_email":"admin@aurora.vital.demo"}' `
  identity-api:8082 vital.platform_identity.v1.IdentityProvisioningService/ProvisionInitialClinicAdmin
```

Repetir exactamente el mismo comando debe retornar el **mismo** `initial_admin_account_id` con `status: PROVISIONING_STATUS_READY` sin crear cuentas duplicadas; enviar otro `admin_email` con el mismo `provisioning_request_id` debe responder `FailedPrecondition`.
