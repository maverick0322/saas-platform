# PLAT-04A — Contrato HTTP Administrativo de Platform (`v1` Candidato)

- **Estado:** Candidato (`1.0.0-candidate`) ajustado con decisiones incorporadas de forma preliminar para revisión y aprobación formal del responsable antes de `PLAT-04B`.
- **Alcance de este incremento:** exclusivamente documental. No introduce endpoints HTTP, middleware JWT, migraciones, cambios de dominio ni dependencias de ejecución.
- **Especificación OpenAPI asociada:** [`platform-admin.v1.yaml`](openapi/platform-admin.v1.yaml) (OpenAPI `3.0.3`).
- **Base verificada en `saas-platform`:** rama `develop`, commit `4182c2b229a8161510d27d793901c9864b92fff4`.
- **Contrato emisor de identidad de referencia (`IDENTITY-SEC-01`):** `Eugene1805/saas-identity` (`main`, commit `a374584b445e7a09f55e4d9535f09439c4f65797`, `docs/IDENTITY-SEC-01-JWT-CONTRACT.md`).

---

## 1. Referencias y distinción normativa

Este documento separa explícitamente los **acuerdos ya aprobados e integrados** de los **documentos históricos o candidatos**:

| Artefacto | Estado normativo | Papel respecto a este contrato |
| --- | --- | --- |
| [`ADR-0001`](../adr/ADR-0001-vertical-slice-platform.md) | **Aprobado** | Organización por Vertical Slices ligeros sin mediadores ni buses distribuidos. |
| [`ADR-0002`](../adr/ADR-0002-multitenant-persistence.md) | **Aprobado** | Catálogo global de Platform separado de las instancias PostgreSQL por clínica (CloudNativePG). |
| [`ADR-0003`](../adr/ADR-0003-transactional-consistency-and-provisioning.md) | **Aprobado** | Consistencia ACID local, operación durable e inexistencia de activación con aprovisionamiento incompleto (`Identity READY` necesario pero no suficiente). |
| [`INTEGRACION_PLATFORM_IDENTITY_v1.md`](INTEGRACION_PLATFORM_IDENTITY_v1.md) y [`platform_identity.proto`](proto/platform_identity/v1/platform_identity.proto) | **Aprobado y congelado** | Contrato gRPC v1 Platform–Identity con **los acuerdos técnicos `D-01` a `D-05` ya ratificados**; servidor `TenantAccessService.GetTenantAccessState` implementado en `PLAT-03A` y validado en vivo en `PLAT-03B` ([`PLAT-03B-VALIDATION.md`](../PLAT-03B-VALIDATION.md)). |
| [`PLAT-02B-PROVISIONING-WORKER-DESIGN.md`](../PLAT-02B-PROVISIONING-WORKER-DESIGN.md) | **Aprobado para núcleo (`PLAT-02C`)** | Etapas ordenadas, estados de operación/etapa, leases, *fencing* y sanitización ya implementados en código. |
| `IDENTITY-SEC-01` (`saas-identity/docs/IDENTITY-SEC-01-JWT-CONTRACT.md`) | **Aprobado e integrado en `saas-identity`** | Contrato JWT Bearer `RS256`, claves, claims, `SUPER_ADMIN` sin `tenant_id` y limitaciones stateless vigentes. |
| [`PLATFORM_IDENTITY_FRONTERA_Y_CONTRATO_MINIMO_v0.1_CANDIDATO.md`](PLATFORM_IDENTITY_FRONTERA_Y_CONTRATO_MINIMO_v0.1_CANDIDATO.md) | **Antecedente histórico no vinculante** | Borrador previo a `ADR-0002`/`0003`; los acuerdos técnicos `D-01` a `D-05` de integración gRPC fueron **ya ratificados** posteriormente en `INTEGRACION_PLATFORM_IDENTITY_v1.md`. |
| **`PLATFORM_ADMIN_HTTP_v1.md` + `platform-admin.v1.yaml`** | **Candidato v1 ajustado (`PLAT-04A`)** | Contrato mínimo para consumo del frontend y revisión final previa a su implementación en `PLAT-04B`. |

---

## 2. Alcance y exclusiones de `v1`

### 2.1 Operaciones incluidas (exclusivamente)

| Método y ruta | Propósito | Autorización | Respaldo actual en `saas-platform` |
| --- | --- | --- | --- |
| `POST /v1/tenants` | Registrar clínica en catálogo global junto con su operación durable inicial | `SUPER_ADMIN` global (JWT `RS256` local + verificación síncrona vía `GET /v1/auth/me`) | `RegisterTenantHandler` / `RegisterTenantCommand` (`PLAT-02A`/`03A`) |
| `GET /v1/tenants` | Listar clínicas de forma resumida y paginada | `SUPER_ADMIN` global (validación JWT `RS256` local) | Nuevo slice mínimo de solo lectura a implementar en `PLAT-04B` |
| `GET /v1/tenants/{tenantId}` | Consultar ficha administrativa y progreso de aprovisionamiento de una clínica | `SUPER_ADMIN` global (validación JWT `RS256` local) | `GetTenantByIdHandler` / `TenantDetails` / `TenantProvisioningSummary` |

Los endpoints técnicos existentes (`GET /health/live` y `GET /health/ready`) permanecen fuera de esta API administrativa y mantienen su acceso técnico habitual.

### 2.2 Exclusiones explícitas

Quedan **fuera** de `v1` y **no** deben implementarse en `PLAT-04A`:

- Suspensión y reactivación de clínicas (`SUSPENDIDA` / `ACTIVA`).
- Modificación posterior de `maxActiveAccounts` o de planes/suscripciones/facturación.
- Reintentos administrativos manuales (`retry-initial-admin`) o reactivación forzada.
- Activación efectiva de tenants, integración CloudNativePG, gRPC `ProvisionInitialClinicAdmin` o integración con `Clinical`.
- Búsqueda avanzada, filtros complejos, ordenamientos dinámicos, caché distribuida o CQRS distribuido.
- Cualquier modificación en `saas-identity`, `Gateway`, `Clinical`, `saas-infra` o `platform_identity.proto`.

---

## 3. Contrato de autenticación y autorización (`IDENTITY-SEC-01`)

### 3.1 Validación criptográfica y configuración de Platform

Platform adopta íntegramente `IDENTITY-SEC-01`:

| Parámetro | Valor obligatorio en Platform |
| --- | --- |
| Esquema HTTP | `Authorization: Bearer <jwt>` |
| Algoritmo admitido | **`RS256` exclusivamente** (`SecurityAlgorithms.RsaSha256`). Rechazar explícitamente `HS256`, `none` u otros algoritmos sin fallback. |
| Clave pública | Clave RSA de **al menos 2048 bits** en formato PEM `SubjectPublicKeyInfo` (`-----BEGIN PUBLIC KEY----- ... -----END PUBLIC KEY-----`), inyectada mediante `Jwt:PublicKeyPem` (`Jwt__PublicKeyPem` o ruta segura equivalente). |
| Secreto prohibido en Platform | Platform **nunca** debe recibir la clave privada RSA (`Jwt:PrivateKeyPem`) ni `Jwt:RefreshTokenHmacKey`. Si la configuración suministrada contiene bloque de clave privada, el arranque debe fallar de forma explícita. |
| Issuer (`iss`) | `saas-identity` |
| Audience (`aud`) | `vital-saas` |
| Vigencia predeterminada del access token | `15 minutos` (`900` segundos) |
| Tolerancia temporal (`ClockSkew`) | `30 segundos` |
| Mapeo de claims (.NET) | `MapInboundClaims = false` (deshabilitado globalmente y en `JwtBearerOptions`) |
| Claim de rol (`RoleClaimType`) | `role` |

### 3.2 Reglas obligatorias de claims y autorización administrativa

Todas las operaciones de `/v1/tenants*` exigen una cuenta global con rol `SUPER_ADMIN` validada localmente a partir de los claims del JWT verificado:

1. **Token auténtico y vigente:** firma `RS256` verificada contra la clave pública de `saas-identity`, con `iss == "saas-identity"`, `aud == "vital-saas"`, `nbf` y `exp` vigentes dentro de `ClockSkew = 30s`.
2. **Claim `sub` válido:** presente, no vacío y convertible a un `Guid` distinto de `Guid.Empty` (`00000000-0000-0000-0000-000000000000`).
3. **Rol cerrado válido:** el claim `role` debe pertenecer al conjunto cerrado definido en `IDENTITY-SEC-01` (`SUPER_ADMIN`, `ADMIN_CLINICA`, `ESPECIALISTA`, `AUXILIAR`). Cualquier valor distinto, ausente o múltiple constituye un contexto de identidad inválido.
4. **Coherencia estricta entre `role` y `tenant_id`:**
   - Si `role == "SUPER_ADMIN"`, el claim `tenant_id` **debe estar ausente** (o no existir en el token). Un token con `role == "SUPER_ADMIN"` que incluya `tenant_id` contiene **claims contradictorios** y debe rechazarse.
   - Si `role` corresponde a una cuenta clínica (`ADMIN_CLINICA`, `ESPECIALISTA`, `AUXILIAR`), el claim `tenant_id` debe existir y ser un `Guid` no vacío; de lo contrario, el contexto de identidad es inválido.
5. **Autoridad requerida:** tras superar la validación de identidad, la cuenta debe poseer exactamente `role == "SUPER_ADMIN"`.
6. **Comprobación reforzada en mutaciones administrativas sensibles (`POST /v1/tenants`, decisión `D-HTTP-01`):**
   - Adicionalmente a la validación local del JWT `RS256`, Platform ejecuta una consulta síncrona de autorización contra `GET /v1/auth/me` de `saas-identity` (reenviando el mismo Bearer token recibido) con un **timeout corto y configurable** (por ejemplo `IdentityAuth:TimeoutSeconds`, valor propuesto `3` segundos, nunca sin límite).
   - Platform debe validar estrictamente la respuesta `200 OK` de Identity (`AccountProfileDto`) comprobando que:
     - `id` coincide exactamente con el `sub` autenticado en el JWT,
     - `active == true`,
     - `tenantId == null` (cuenta global sin ámbito de clínica), y
     - `role == "SUPER_ADMIN"`.
   - **Fallo cerrado y aislamiento de respuestas:**
     - Si `GET /v1/auth/me` responde `401 Unauthorized` → Platform devuelve **`401 Unauthorized`** (`code: "UNAUTHORIZED"`, con cabecera `WWW-Authenticate: Bearer`) generado por Platform.
     - Si `GET /v1/auth/me` responde `403 Forbidden`, o devuelve `200 OK` con `active == false` o `role != "SUPER_ADMIN"` → Platform devuelve **`403 Forbidden`** (`code: "FORBIDDEN"`) generado por Platform.
     - Si `saas-identity` no está disponible, agota el timeout, falla por red/TLS, responde `5xx`, devuelve JSON inválido o devuelve una respuesta incoherente (p. ej. `id != sub`, `tenantId != null`, esquema inválido o estado inesperado) → Platform falla de forma cerrada devolviendo **`503 Service Unavailable`** (`code: "SERVICE_UNAVAILABLE"`) generado por Platform.
     - **Prohibido** reenviar indiscriminadamente cuerpos de error, detalles internos o cabeceras internas provenientes de `saas-identity`.
   - **Separación estricta respecto al aprovisionamiento:** la llamada a `GET /v1/auth/me` es **exclusivamente** una verificación síncrona de autorización y vigencia de cuenta previa a ejecutar `RegisterTenantHandler`. Durante `POST /v1/tenants`, Platform **no** realiza llamadas síncronas a `CloudNativePG`, `Clinical` ni al RPC gRPC `ProvisionInitialClinicAdmin` de `saas-identity`.
7. **Lecturas administrativas (`GET /v1/tenants`, `GET /v1/tenants/{tenantId}`):** utilizan validación criptográfica local `RS256` sin llamada síncrona a `GET /v1/auth/me`.

### 3.3 Distinción entre `401 Unauthorized` y `403 Forbidden`

Toda respuesta `401 Unauthorized` emitida por Platform **debe incluir la cabecera `WWW-Authenticate: Bearer`** (sin incluir detalles internos ni revelar reglas internas en la cabecera).

| Escenario recibido por Platform | Código HTTP | `code` (Problem Details) | Criterio |
| --- | --- | --- | --- |
| Sin cabecera `Authorization`, esquema distinto de `Bearer` o JWT malformado | `401 Unauthorized` | `UNAUTHORIZED` | Autenticación ausente o sintácticamente inválida (+ `WWW-Authenticate: Bearer`). |
| Firma inválida, algoritmo distinto de `RS256` (`HS256`/`none`), `iss`/`aud` incorrectos o token expirado (`> 30s` de `ClockSkew`) | `401 Unauthorized` | `UNAUTHORIZED` | Token no auténtico o no vigente (+ `WWW-Authenticate: Bearer`). |
| Falta `sub` o `sub` no es UUID no vacío | `401 Unauthorized` | `UNAUTHORIZED` | Contexto de identidad inválido (+ `WWW-Authenticate: Bearer`). |
| `role` ausente o desconocido (no pertenece al catálogo cerrado de `IDENTITY-SEC-01`) | `401 Unauthorized` | `UNAUTHORIZED` | Rol desconocido / token incoherente (+ `WWW-Authenticate: Bearer`). |
| Claims contradictorios (`role == "SUPER_ADMIN"` con `tenant_id`, o rol clínico sin `tenant_id` UUID válido) | `401 Unauthorized` | `UNAUTHORIZED` | Contradicción de identidad (+ `WWW-Authenticate: Bearer`). |
| En `POST /v1/tenants`, `GET /v1/auth/me` rechaza autenticación (`401`) | `401 Unauthorized` | `UNAUTHORIZED` | Sesión/token rechazado por Identity (+ `WWW-Authenticate: Bearer`). |
| Token `RS256` válido de una cuenta clínica (`ADMIN_CLINICA`, `ESPECIALISTA`, `AUXILIAR`) con `sub` y `tenant_id` válidos, o en `POST /v1/tenants` cuenta marcada con `active == false` / `role != "SUPER_ADMIN"` / `403` en `/v1/auth/me` | `403 Forbidden` | `FORBIDDEN` | Identidad reconocida sin autoridad o vigencia administrativa suficiente. |

### 3.4 Confianza cero en cabeceras, defensa en profundidad y trazabilidad

- **Prohibido confiar en cabeceras del cliente o proxy:** Platform **nunca** debe tomar `role`, `sub`, `tenant_id`, correo o identidad administrativa desde cabeceras como `X-Role`, `X-User-Id`, `X-Tenant-Id`, `X-Forwarded-User` o campos del cuerpo HTTP.
- **Responsabilidad de Gateway vs Platform:** aunque un Gateway o Ingress realice validación previa en el borde, **no sustituye** la verificación `RS256` y la autorización interna de Platform. Platform valida cada petición de forma autónoma antes de ejecutar cualquier handler.
- **Trazabilidad administrativa mínima (`audit trail` en logs estructurados):** toda alta administrativa en `POST /v1/tenants` (y los rechazos de autorización o conflicto relevantes) debe registrar el `sub` autenticado (`actorAccountId`), el `tenantId` (si se creó o se identificó en conflicto), el `publicId` normalizado, el `traceId` y la marca temporal UTC. Queda **estrictamente prohibido** registrar tokens JWT, cabeceras `Authorization`, credenciales, contraseñas o cargas sensibles.

---

## 4. Revocación y vigencia de cuentas (limitación de JWT stateless)

### 4.1 Limitación actual documentada en `IDENTITY-SEC-01`

Conforme a la sección 9 de `IDENTITY-SEC-01`:

- Los access tokens `RS256` emitidos por `saas-identity` son **stateless** y tienen una vigencia predeterminada de **15 minutos** (`900` segundos) más hasta **30 segundos** de `ClockSkew` (ventana máxima de `15 minutos y 30 segundos`).
- Cuando un usuario o administrador ejecuta `POST /v1/auth/logout`, se detecta reutilización de refresh token o se desactiva una cuenta, `saas-identity` revoca los refresh tokens en su PostgreSQL e impide renovaciones futuras; sin embargo, **un access token ya emitido sigue siendo criptográficamente válido hasta su expiración natural** ante validadores puramente locales.

### 4.2 Alternativas evaluadas y modelo adoptado en candidato v1 (`D-HTTP-01`)

| Dimensión | Alternativa A — Validación local `RS256` hasta expiración (adoptada en **lecturas `GET`**) | Alternativa B — Validación local `RS256` + comprobación vía `GET /v1/auth/me` en Identity (adoptada en **mutaciones sensibles como `POST /v1/tenants`**) |
| --- | --- | --- |
| **Seguridad frente a cuenta deshabilitada (`active == false`)** | Una cuenta `SUPER_ADMIN` deshabilitada podría consultar lecturas durante la ventana restante de su token (`<= 15m 30s`). | Verifica en la BD global de Identity que la cuenta correspondiente al `sub` continúe existente, global (`tenantId == null`), con `role == "SUPER_ADMIN"` y `active == true` antes de ejecutar la mutación. |
| **Alcance real respecto a `logout` y bloqueo** | Tras `logout`, el access token sigue siendo aceptado localmente hasta expirar si el cliente o un atacante conserva ese JWT. | **`GET /v1/auth/me` comprueba la vigencia actual (`active == true`) y rol/ámbito de la cuenta, pero NO equivale a revocación inmediata de cada access token tras `logout`** (ya que `/v1/auth/logout` revoca refresh tokens y no mantiene denylist por `jti`) **ni implementa comprobación de bloqueo temporal de cuenta** (que el handler actual `/v1/auth/me` de Identity no evalúa). |
| **Acoplamiento en tiempo de ejecución** | **Nulo** respecto a disponibilidad síncrona de Identity durante las lecturas HTTP administrativas (solo requiere clave pública `RS256`). | **Acoplamiento síncrono HTTP** Platform → Identity en cada mutación administrativa sensible, acotado con timeout corto configurable. |
| **Implicación cualitativa de latencia** | Verificación criptográfica local en memoria sin llamadas de red adicionales ni consultas síncronas entre servicios. | Añade una llamada HTTP interna hacia `saas-identity` y la lectura en su base PostgreSQL global previa al handler de Platform (no se presentan cifras numéricas al no existir mediciones en carga). |
| **Comportamiento ante fallos de Identity** | Las lecturas administrativas siguen operando aunque Identity esté temporalmente inaccesible. | **Fallo cerrado obligatorio:** ante timeout, error de red, `5xx`, cuerpo inválido o respuesta incoherente de `/v1/auth/me`, Platform rechaza la mutación con `503 Service Unavailable` (`SERVICE_UNAVAILABLE`) sin ejecutar cambios ni filtrar cuerpos crudos de Identity. |

- No se introducen Redis, listas negras distribuidas de `jti` ni buses de eventos de revocación en esta fase.

---

## 5. Convenciones transversales del contrato HTTP

### 5.1 Serialización y enums

- Formato de intercambio: `application/json` en `camelCase`.
- Las enumeraciones de dominio se serializan **exclusivamente como códigos de texto estables en mayúsculas**, nunca como enteros numéricos de C# ni mediante pares redundantes (`status` + `statusCode`):
  - **`operationalState`**: `PENDIENTE_ADMIN`, `ACTIVA`, `SUSPENDIDA` (`TenantOperationalStateExtensions.ToCode()`).
  - **`status` / `provisioningStatus`**: `PENDING`, `IN_PROGRESS`, `FAILED_RETRYABLE`, `FAILED_TERMINAL`, `COMPLETED` (`ProvisioningOperationStatusExtensions.ToCode()`).
  - **`stages.*`**: `PENDING`, `VERIFICATION_REQUIRED`, `CONFIRMED` (`ProvisioningStageStateExtensions.ToCode()`).

### 5.2 Formato temporal y justificación (`D-HTTP-04`)

- Todos los instantes devueltos por esta API administrativa se expresan en **ISO-8601 UTC (`YYYY-MM-DDTHH:mm:ss.fffZ`, RFC 3339)** y conservan el sufijo explícito `*AtUtc` (`createdAtUtc`, `updatedAtUtc`, `lastAttemptAtUtc`, `nextAttemptAtUtc`, `completedAtUtc`).
- **Justificación técnica:**
  1. **Coherencia interna y con PostgreSQL:** `Tenant`, `TenantProvisioningOperation`, `TenantDetails` y `TenantProvisioningSummary` ya exponen `DateTimeOffset` normalizados a UTC (`ToUniversalTime()`) y almacenados en columnas `timestamptz`. Mantener UTC con sufijo `Z` evita incongruencias entre nombres de propiedades (`*AtUtc`) y desplazamiento horario.
  2. **Compatibilidad con el frontend:** la aplicación cliente recibe un instante inequívoco en RFC 3339 y lo formatea a la zona horaria operativa requerida para visualización (por ejemplo GMT-6) sin lógica de desfase manual.
  3. **Convivencia limpia con gRPC v1:** el contrato gRPC congelado (`platform_identity.proto` v1) conserva su campo específico `evaluated_at_gmt6` con desplazamiento `-06:00` para la frontera interna Platform–Identity; la API HTTP administrativa externa no altera dicho `.proto` y adopta el estándar HTTP RFC 3339 en UTC.

### 5.3 Formato de errores (`Problem Details`) y códigos HTTP (`D-HTTP-05`)

Todos los errores `4xx` y `5xx` utilizan `application/problem+json` (RFC 9457) manteniendo compatibilidad con el mapeo adoptado en `saas-identity`:

| Campo | Tipo | Obligatorio | Descripción |
| --- | --- | --- | --- |
| `type` | `string` (URI) | Sí | Referencia estable al tipo de problema (ej. `https://vital.internal/problems/validation-error`). |
| `title` | `string` | Sí | Resumen corto legible del error HTTP. |
| `status` | `integer` | Sí | Código de estado HTTP numérico (`400`, `401`, `403`, `404`, `409`, `503`). |
| `detail` | `string` | Sí | Explicación segura del error sin detalles internos ni PII innecesaria. |
| `instance` | `string` | Sí | Ruta de la petición afectada (ej. `/v1/tenants`). |
| `code` | `string` | Sí | Código estable de máquina en mayúsculas (`UPPER_SNAKE_CASE`). |
| `traceId` | `string` | Sí | Identificador de correlación de la traza/petición para diagnóstico en logs. |
| `errors` | `ValidationErrorItem[]` | Solo en `400` de validación/paginación | Lista de errores por campo (`field`, `code`, `message`), mapeada desde `TenantValidationError` o validación de consulta. |
| `existingTenantId` | `string` (`uuid`, nullable) | Solo en `409` (`TENANT_PUBLIC_ID_CONFLICT`) | Identificador de la clínica ya registrada para el `publicId` normalizado en conflicto (`null` solo en condiciones excepcionales de concurrencia). |

#### Catálogo cerrado de respuestas HTTP y `code`

| HTTP Status | Media Type | Cabeceras clave | `code` estables posibles | Cuándo ocurre |
| --- | --- | --- | --- | --- |
| `200 OK` | `application/json` | — | — | Consulta exitosa en `GET /v1/tenants` o `GET /v1/tenants/{tenantId}`. |
| `201 Created` | `application/json` | `Location: /v1/tenants/{id}` | — | Clínica y operación inicial persistidas atómicamente en `POST /v1/tenants`. |
| `400 Bad Request` | `application/problem+json` | — | `VALIDATION_ERROR`, `INVALID_TENANT_ID`, `INVALID_PAGINATION`, `INVALID_REQUEST_BODY` | Datos de entrada inválidos, JSON malformado/tipos incompatibles, `tenantId` inválido o parámetros de paginación inválidos o extremos. |
| `401 Unauthorized` | `application/problem+json` | `WWW-Authenticate: Bearer` | `UNAUTHORIZED` | Token ausente, firma/algoritmo inválido (`RS256` requerido), expirado, `sub` inválido, rol desconocido, claims contradictorios o rechazo `401` desde `GET /v1/auth/me`. |
| `403 Forbidden` | `application/problem+json` | — | `FORBIDDEN` | Identidad válida pero sin rol `SUPER_ADMIN` o sin vigencia activa (`active == false`) en `POST /v1/tenants`. |
| `404 Not Found` | `application/problem+json` | — | `TENANT_NOT_FOUND` | No existe clínica registrada para el `tenantId` indicado. |
| `409 Conflict` | `application/problem+json` | — | `TENANT_PUBLIC_ID_CONFLICT` | Ya existe una clínica registrada con el mismo `publicId` normalizado (`ux_tenants_public_id`); incluye `existingTenantId`. |
| `503 Service Unavailable` | `application/problem+json` | — | `SERVICE_UNAVAILABLE` | Base de datos global de Platform no disponible o fallo cerrado por indisponibilidad/respuesta inválida/incoherente de `GET /v1/auth/me` en `POST /v1/tenants`. |

> **Regla de no exposición:** las respuestas HTTP **nunca** deben devolver secretos, tokens JWT, cadenas de conexión PostgreSQL, mensajes crudos de `PostgresException` / SQL, cuerpos crudos reenviados de `saas-identity` ni *stack traces*.

---

## 6. Especificación detallada de operaciones

### 6.1 `POST /v1/tenants` — Registrar nueva clínica

Crea de forma atómica una nueva clínica en el catálogo global de Platform y su operación inicial durable de aprovisionamiento, tras superar tanto la validación JWT local `RS256` como la comprobación síncrona de vigencia/rol en `GET /v1/auth/me`.

#### Solicitud (`CreateTenantRequest`) y orden normalización → validación

Reutiliza exactamente los 6 campos de `RegisterTenantCommand`. **Importante:** en todos los campos textuales, la **normalización se ejecuta antes** de validar longitud, patrón, formato de correo y restricción de unicidad (`ux_tenants_public_id`), replicando exactamente el comportamiento de `Tenant.TryRegister`, `TenantProvisioningOperation.TryValidateInitialAdmin` y `AdministrativeValidation`:

| Campo JSON | Tipo | Obligatorio | Normalización previa | Validación exacta aplicada tras normalizar |
| --- | --- | --- | --- | --- |
| `publicId` | `string` | Sí | `Trim().ToLowerInvariant()` | No vacío; longitud `3..64`; patrón `^[a-z0-9]+(?:-[a-z0-9]+)*$` (ASCII minúsculas, dígitos y guiones simples internos; sin puntos, espacios ni guiones bajos). Único globalmente (`ux_tenants_public_id`). |
| `name` | `string` | Sí | `Trim()` | No vacío; longitud `2..150`; sin caracteres de control (`char.IsControl`). |
| `contactEmail` | `string` | Sí | `Trim().ToLowerInvariant()` | No vacío; máximo `254` caracteres; formato de correo electrónico válido sin espacios ni control y dominio con `.` sin iniciar ni terminar en `.`. |
| `initialAdminFullName` | `string` | Sí | `Trim()` | No vacío; longitud `2..150`; sin caracteres de control (`char.IsControl`). |
| `initialAdminEmail` | `string` | Sí | `Trim().ToLowerInvariant()` | No vacío; máximo `254` caracteres; mismas reglas de formato de correo electrónico que `contactEmail`; es **independiente** de `contactEmail` (pueden coincidir o diferir). |
| `maxActiveAccounts` | `integer` (`int32`) | Sí | — | Entero positivo mayor o igual a `1` (`>= 1`). |

> En la deserialización JSON de `PLAT-04B` debe evitarse ignorar silenciosamente tipos incompatibles; cuerpos vacíos o JSON malformados deben responder `400 Bad Request` (`INVALID_REQUEST_BODY`). Para errores de validación por campo se devuelven nombres `camelCase` (`publicId`, `name`, `contactEmail`, `initialAdminFullName`, `initialAdminEmail`, `maxActiveAccounts`).

#### Semántica de negocio, atomicidad y aprovisionamiento asíncrono

1. **Verificación de autorización previa (no aprovisionamiento):** Platform valida el JWT `RS256` localmente y confirma mediante `GET /v1/auth/me` (con timeout corto configurable y fallo cerrado) que el mismo `sub` corresponde a una cuenta activa (`active == true`), global (`tenantId == null`) y con `role == "SUPER_ADMIN"`.
2. **Transacción ACID local:** `RegisterTenantHandler` persiste en una única transacción de PostgreSQL:
   - `Tenant`: `operationalState = "PENDIENTE_ADMIN"`, `mayAuthenticate = false`, `id` = UUIDv7 generado por Platform.
   - `TenantProvisioningOperation`: `status = "PENDING"`, `attemptCount = 0`, `manualInterventionRequired = false`, `provisioningRequestId` = UUIDv7 en formato `"D"` inmutable generado por Platform, y las 4 etapas (`infrastructureInstance`, `serviceDatabases`, `clinicalPreparation`, `identityPreparation`) en `"PENDING"`.
3. **Sin llamadas síncronas de aprovisionamiento:** durante `POST /v1/tenants` **no** existe ninguna llamada síncrona a `CloudNativePG`, `Clinical` ni al RPC gRPC `ProvisionInitialClinicAdmin` de `saas-identity`.
4. **Creación exitosa (`201 Created`) != aprovisionamiento finalizado:** recibir `201 Created` significa exclusivamente que la clínica quedó registrada en `PENDIENTE_ADMIN` y que la intención de aprovisionamiento quedó persistida de forma durable para el trabajador (`PLAT-02C`). No implica que la instancia PostgreSQL dedicada ni la cuenta `ADMIN_CLINICA` en Identity existan todavía ni habilita autenticación operativa (`mayAuthenticate` es `false`). El frontend debe consultar el avance posterior mediante `GET /v1/tenants/{tenantId}`.
5. **Respuesta HTTP `201 Created`:**
   - Incluye cabecera `Location: /v1/tenants/{tenantId}`.
   - Devuelve el cuerpo completo `TenantDetailsResponse`.

#### Slug duplicado, `existingTenantId` y reintentos HTTP (`D-HTTP-03`)

- Si el `publicId` normalizado (`Trim().ToLowerInvariant()`) ya existe en `platform.tenants`, la restricción única `ux_tenants_public_id` (PostgreSQL `23505`) provoca rollback limpio y Platform responde **`409 Conflict`** con `code: "TENANT_PUBLIC_ID_CONFLICT"` y el campo `existingTenantId` (UUID de la clínica existente para ese `publicId` normalizado, o `null` si excepcionalmente dejara de existir al resolverla en concurrencia extrema).
- **Uso seguro de `existingTenantId` en el frontend:** con `existingTenantId`, el frontend puede consultar `GET /v1/tenants/{existingTenantId}` para inspeccionar y verificar el detalle administrativo del recurso existente. Sin embargo, el cliente **no debe asumir automáticamente** que ese registro corresponde necesariamente a su solicitud previa (otro operador `SUPER_ADMIN` pudo haber registrado ese `publicId` previamente o con otros datos de clínica o administrador inicial).
- **Diferenciación respecto a `Idempotency-Key`:** `initialProvisioning.provisioningRequestId` es una **clave interna durable de Platform** (generada en la transacción de registro para que el trabajador reintente de forma idempotente hacia downstream). En `v1` **no** se exige ni se procesa cabecera HTTP `Idempotency-Key`.

#### Ejemplo completo `POST /v1/tenants`

**Solicitud:**

```http
POST /v1/tenants HTTP/1.1
Host: 127.0.0.1:8080
Authorization: Bearer <JWT_RS256_SUPER_ADMIN>
Content-Type: application/json

{
  "publicId": "clinica-norte",
  "name": "Clínica Norte",
  "contactEmail": "contacto@clinica-norte.vital-demo.test",
  "initialAdminFullName": "Dra. Sofía Navarro",
  "initialAdminEmail": "admin.norte@clinica-norte.vital-demo.test",
  "maxActiveAccounts": 15
}
```

**Respuesta `201 Created`:**

```http
HTTP/1.1 201 Created
Location: /v1/tenants/019abc12-3456-7000-8000-000000000101
Content-Type: application/json; charset=utf-8

{
  "id": "019abc12-3456-7000-8000-000000000101",
  "publicId": "clinica-norte",
  "name": "Clínica Norte",
  "contactEmail": "contacto@clinica-norte.vital-demo.test",
  "operationalState": "PENDIENTE_ADMIN",
  "mayAuthenticate": false,
  "maxActiveAccounts": 15,
  "createdAtUtc": "2026-11-24T18:30:00.000Z",
  "updatedAtUtc": "2026-11-24T18:30:00.000Z",
  "initialProvisioning": {
    "operationId": "019abc12-3456-7000-8000-000000000102",
    "provisioningRequestId": "019abc12-3456-7000-8000-000000000102",
    "status": "PENDING",
    "initialAdminFullName": "Dra. Sofía Navarro",
    "initialAdminEmail": "admin.norte@clinica-norte.vital-demo.test",
    "attemptCount": 0,
    "manualInterventionRequired": false,
    "lastErrorCode": null,
    "lastErrorSummary": null,
    "createdAtUtc": "2026-11-24T18:30:00.000Z",
    "updatedAtUtc": "2026-11-24T18:30:00.000Z",
    "lastAttemptAtUtc": null,
    "nextAttemptAtUtc": null,
    "completedAtUtc": null,
    "stages": {
      "infrastructureInstance": "PENDING",
      "serviceDatabases": "PENDING",
      "clinicalPreparation": "PENDING",
      "identityPreparation": "PENDING"
    }
  }
}
```

---

### 6.2 `GET /v1/tenants` — Listar clínicas (representación resumida y paginada, `D-HTTP-02`)

Devuelve la vista tabular administrativa mínima de clínicas registradas en Platform usando validación JWT `RS256` local.

#### Parámetros de consulta y manejo seguro de valores extremos

| Parámetro | Tipo | Default | Restricciones | Error si incumple |
| --- | --- | --- | --- | --- |
| `page` | `integer` (`int32`) | `1` | Entero `int32` entre `1` e `int32.MaxValue` (`2147483647`) | `400 Bad Request` (`INVALID_PAGINATION`) |
| `pageSize` | `integer` (`int32`) | `20` | Entero `int32` entre `1` y `50` | `400 Bad Request` (`INVALID_PAGINATION`) |

- **Orden determinista obligatorio:** `createdAtUtc DESC, id DESC` (las clínicas más recientes primero; `id` desempata de forma estable).
- **Manejo seguro de valores extremos:**
  1. Cualquier valor no numérico, decimal, vacío, `<= 0`, `pageSize > 50` o fuera de rango `int32` (p. ej. `page=99999999999`) debe rechazarse tempranamente en el enlace HTTP con **`400 Bad Request`** (`code: "INVALID_PAGINATION"`).
  2. Si `page` es un `int32` válido muy grande (p. ej. `page=2147483647`, donde `(page - 1) * pageSize` desbordaría `int32`), la implementación en `PLAT-04B` debe calcular `totalCount` primero y evaluar el offset en aritmética `long` (o cortocircuitar cuando `((long)(page - 1) * pageSize) >= totalCount`) devolviendo `200 OK` con `items: []` sin ejecutar consultas con `OFFSET` desbordado ni provocar excepciones en PostgreSQL.
- **Sin filtros complejos:** `v1` no define búsqueda libre, filtros dinámicos ni `sortBy`.

#### Estructura de respuesta (`TenantListResponse` / `TenantListItem`)

Cada elemento de `items` expone únicamente:

| Campo | Tipo | Nullable | Descripción |
| --- | --- | --- | --- |
| `id` | `string` (`uuid`) | No | Identificador interno inmutable de la clínica. |
| `publicId` | `string` | No | Slug público normalizado de la clínica. |
| `name` | `string` | No | Nombre administrativo visible de la clínica. |
| `operationalState` | `TenantOperationalState` | No | `PENDIENTE_ADMIN` \| `ACTIVA` \| `SUSPENDIDA`. |
| `mayAuthenticate` | `boolean` | No | `true` únicamente cuando `operationalState == "ACTIVA"`. |
| `maxActiveAccounts` | `integer` (`int32`) | **Sí** | Cuota máxima de cuentas activas (`null` solo en filas legadas previas a `PLAT-03A`). |
| `provisioningStatus` | `ProvisioningOperationStatus` | **Sí** | Estado técnico general (`PENDING` \| `IN_PROGRESS` \| `FAILED_RETRYABLE` \| `FAILED_TERMINAL` \| `COMPLETED`), o `null` en registros legados de `PLAT-01`. |
| `manualInterventionRequired` | `boolean` | No | Indica si el aprovisionamiento requiere intervención administrativa (`false` cuando no existe operación o no requiere intervención). |
| `createdAtUtc` | `string` (`date-time`) | No | Fecha de creación UTC (`Z`). |
| `updatedAtUtc` | `string` (`date-time`) | No | Fecha de última actualización UTC (`Z`). |

#### Ejemplo completo `GET /v1/tenants` (`200 OK`)

```http
GET /v1/tenants?page=1&pageSize=20 HTTP/1.1
Host: 127.0.0.1:8080
Authorization: Bearer <JWT_RS256_SUPER_ADMIN>
```

```json
{
  "items": [
    {
      "id": "019abc12-3456-7000-8000-000000000101",
      "publicId": "clinica-norte",
      "name": "Clínica Norte",
      "operationalState": "PENDIENTE_ADMIN",
      "mayAuthenticate": false,
      "maxActiveAccounts": 15,
      "provisioningStatus": "PENDING",
      "manualInterventionRequired": false,
      "createdAtUtc": "2026-11-24T18:30:00.000Z",
      "updatedAtUtc": "2026-11-24T18:30:00.000Z"
    },
    {
      "id": "019a0000-0000-7000-8000-000000000001",
      "publicId": "clinica-legado-p01",
      "name": "Clínica Histórica Prueba",
      "operationalState": "PENDIENTE_ADMIN",
      "mayAuthenticate": false,
      "maxActiveAccounts": null,
      "provisioningStatus": null,
      "manualInterventionRequired": false,
      "createdAtUtc": "2026-11-22T18:00:00.000Z",
      "updatedAtUtc": "2026-11-22T18:00:00.000Z"
    }
  ],
  "page": 1,
  "pageSize": 20,
  "totalCount": 2,
  "totalPages": 1
}
```

---

### 6.3 `GET /v1/tenants/{tenantId}` — Consultar detalle administrativo de una clínica

Reutiliza la consulta existente `GetTenantByIdHandler` (`TenantDetails` y `TenantProvisioningSummary`) bajo validación JWT `RS256` local.

#### Parámetro de ruta

- `tenantId` (`string`, formato `uuid`): identificador único del tenant. Si el formato no es UUID válido o equivale a `00000000-0000-0000-0000-000000000000`, responder `400 Bad Request` (`INVALID_TENANT_ID`). Si no existe, responder `404 Not Found` (`TENANT_NOT_FOUND`).

#### Esquema `TenantDetailsResponse`

| Campo | Tipo | Nullable | Origen / Significado |
| --- | --- | --- | --- |
| `id` | `string` (`uuid`) | No | `TenantDetails.Id` |
| `publicId` | `string` | No | `TenantDetails.PublicId` |
| `name` | `string` | No | `TenantDetails.Name` |
| `contactEmail` | `string` (`email`) | No | `TenantDetails.ContactEmail` |
| `operationalState` | `TenantOperationalState` | No | `TenantDetails.OperationalState.ToCode()` (`PENDIENTE_ADMIN` \| `ACTIVA` \| `SUSPENDIDA`) |
| `mayAuthenticate` | `boolean` | No | `TenantDetails.MayAuthenticate` |
| `maxActiveAccounts` | `integer` (`int32`) | **Sí** | `TenantDetails.MaxActiveAccounts` (`null` en filas legadas pre-PLAT-03A) |
| `createdAtUtc` | `string` (`date-time`) | No | `TenantDetails.CreatedAtUtc` |
| `updatedAtUtc` | `string` (`date-time`) | No | `TenantDetails.UpdatedAtUtc` |
| `initialProvisioning` | `TenantProvisioningSummaryResponse` | **Sí** | `TenantDetails.InitialProvisioning` (`null` en filas legadas pre-PLAT-02A) |

#### Esquema `TenantProvisioningSummaryResponse` (dentro de `initialProvisioning`)

| Campo | Tipo | Nullable | Significado |
| --- | --- | --- | --- |
| `operationId` | `string` (`uuid`) | No | Identificador de la operación durable (`TenantProvisioningOperation.Id`). |
| `provisioningRequestId` | `string` | No | Identificador estable e inmutable de la intención de aprovisionamiento (`TenantProvisioningOperation.ProvisioningRequestId`). |
| `status` | `ProvisioningOperationStatus` | No | `PENDING` \| `IN_PROGRESS` \| `FAILED_RETRYABLE` \| `FAILED_TERMINAL` \| `COMPLETED`. |
| `initialAdminFullName` | `string` | No | Nombre completo previsto para la cuenta `ADMIN_CLINICA` inicial. |
| `initialAdminEmail` | `string` (`email`) | No | Correo electrónico del administrador inicial (diferenciado de `contactEmail`). |
| `attemptCount` | `integer` (`int32`) | No | Número de intentos iniciados/ejecutados por el trabajador (`>= 0`). |
| `manualInterventionRequired` | `boolean` | No | `true` cuando un fallo terminal o verificación obligatoria detiene reintentos automáticos y requiere intervención de operador. |
| `lastErrorCode` | `string` | **Sí** | Código técnico controlado (`^[A-Z0-9_-]{2,64}$`), nunca código crudo SQL sin mapear. |
| `lastErrorSummary` | `string` | **Sí** | **Mensaje controlado diseñado explícitamente para exposición administrativa** (por ejemplo, catálogo controlado por `lastErrorCode` o resumen administrativo curado, máx. `500` caracteres). La sanitización por expresiones regulares (`TenantProvisioningOperation.SanitizeDiagnosticSummary`) actúa únicamente como **defensa en profundidad** en persistencia; nunca debe dependerse exclusivamente de regex sobre diagnósticos internos o excepciones crudas para exponer este campo por HTTP. |
| `createdAtUtc` | `string` (`date-time`) | No | Alta de la operación en UTC (`Z`). |
| `updatedAtUtc` | `string` (`date-time`) | No | Última actualización de estado en UTC (`Z`). |
| `lastAttemptAtUtc` | `string` (`date-time`) | **Sí** | Instante del último intento registrado (`null` si `attemptCount == 0`). |
| `nextAttemptAtUtc` | `string` (`date-time`) | **Sí** | Instante programado para el próximo reintento en UTC (`null` salvo estado `FAILED_RETRYABLE`). |
| `completedAtUtc` | `string` (`date-time`) | **Sí** | Instante de finalización cuando `status == "COMPLETED"`. |
| `stages` | `ProvisioningStagesResponse` | No | Progreso durable por etapa ordenada: `infrastructureInstance`, `serviceDatabases`, `clinicalPreparation`, `identityPreparation` (cada una en `PENDING` \| `VERIFICATION_REQUIRED` \| `CONFIRMED`). |

#### Campos internos explícitamente **excluidos** de HTTP

No deben serializarse entidades EF Core ni exponerse ninguno de los siguientes atributos internos:

- `Tenant.ConcurrencyStamp`, `TenantProvisioningOperation.ConcurrencyStamp` (tokens internos de concurrencia optimista).
- `LeaseOwner`, `LeaseUntilUtc`, `LeaseVersion` (estado de arrendamiento y *fencing* distribuido del trabajador).
- `InfrastructureResourceRef` (nombre interno de clúster/recurso CloudNativePG), `ClinicalMigrationVersion` (versión interna de esquema), `InitialAdminAccountId` (reservado internamente para trazabilidad/activación).
- Cadenas de conexión PostgreSQL, credenciales, contraseñas temporales, JWTs, mensajes internos de excepciones o detalles SQL.

#### Ejemplos completos `GET /v1/tenants/{tenantId}`

**Caso 1 — `200 OK` con fallo terminal que requiere intervención administrativa:**

```json
{
  "id": "019abc12-3456-7000-8000-000000000201",
  "publicId": "clinica-sur",
  "name": "Clínica Sur",
  "contactEmail": "contacto@clinica-sur.vital-demo.test",
  "operationalState": "PENDIENTE_ADMIN",
  "mayAuthenticate": false,
  "maxActiveAccounts": 10,
  "createdAtUtc": "2026-11-24T17:00:00.000Z",
  "updatedAtUtc": "2026-11-24T17:15:10.000Z",
  "initialProvisioning": {
    "operationId": "019abc12-3456-7000-8000-000000000202",
    "provisioningRequestId": "019abc12-3456-7000-8000-000000000202",
    "status": "FAILED_TERMINAL",
    "initialAdminFullName": "Dr. Héctor Méndez",
    "initialAdminEmail": "admin.sur@clinica-sur.vital-demo.test",
    "attemptCount": 3,
    "manualInterventionRequired": true,
    "lastErrorCode": "IDENTITY_PROVISIONING_CONFLICT",
    "lastErrorSummary": "Identity reported a payload conflict for the provisioning request and requires administrative review.",
    "createdAtUtc": "2026-11-24T17:00:00.000Z",
    "updatedAtUtc": "2026-11-24T17:15:10.000Z",
    "lastAttemptAtUtc": "2026-11-24T17:15:10.000Z",
    "nextAttemptAtUtc": null,
    "completedAtUtc": null,
    "stages": {
      "infrastructureInstance": "CONFIRMED",
      "serviceDatabases": "CONFIRMED",
      "clinicalPreparation": "CONFIRMED",
      "identityPreparation": "VERIFICATION_REQUIRED"
    }
  }
}
```

**Caso 2 — `200 OK` para registro legado de `PLAT-01` (sin cuota ni operación inicial):**

```json
{
  "id": "019a0000-0000-7000-8000-000000000001",
  "publicId": "clinica-legado-p01",
  "name": "Clínica Histórica Prueba",
  "contactEmail": "contacto@legado-p01.vital-demo.test",
  "operationalState": "PENDIENTE_ADMIN",
  "mayAuthenticate": false,
  "maxActiveAccounts": null,
  "createdAtUtc": "2026-11-22T18:00:00.000Z",
  "updatedAtUtc": "2026-11-22T18:00:00.000Z",
  "initialProvisioning": null
}
```

---

### 6.4 Ejemplos completos de respuestas de error (`Problem Details`)

**`400 Bad Request` — Errores de validación de dominio en `POST /v1/tenants`:**

```json
{
  "type": "https://vital.internal/problems/validation-error",
  "title": "Validation failed",
  "status": 400,
  "detail": "One or more validation errors occurred while processing the tenant registration.",
  "instance": "/v1/tenants",
  "code": "VALIDATION_ERROR",
  "traceId": "00-7e3f1a9b2c4d5e6f7a8b9c0d1e2f3a4b-1a2b3c4d5e6f7a8b-00",
  "errors": [
    {
      "field": "publicId",
      "code": "format",
      "message": "PublicId must use lowercase ASCII letters, digits, and single internal hyphens (without dots or spaces)."
    },
    {
      "field": "maxActiveAccounts",
      "code": "range",
      "message": "MaxActiveAccounts must be at least 1."
    }
  ]
}
```

**`401 Unauthorized` — Token ausente, firma/algoritmo inválido, claims contradictorios o rechazo `401` desde `GET /v1/auth/me`:**

```http
HTTP/1.1 401 Unauthorized
WWW-Authenticate: Bearer
Content-Type: application/problem+json; charset=utf-8

{
  "type": "https://vital.internal/problems/unauthorized",
  "title": "Unauthorized",
  "status": 401,
  "detail": "A valid RS256 Bearer token issued by saas-identity with coherent identity claims is required.",
  "instance": "/v1/tenants",
  "code": "UNAUTHORIZED",
  "traceId": "00-7e3f1a9b2c4d5e6f7a8b9c0d1e2f3a4b-2222222222222222-00"
}
```

**`403 Forbidden` — Cuenta clínica autenticada sin rol `SUPER_ADMIN` o cuenta global inactiva en `POST /v1/tenants`:**

```json
{
  "type": "https://vital.internal/problems/forbidden",
  "title": "Forbidden",
  "status": 403,
  "detail": "Only active global SUPER_ADMIN accounts are authorized to access administrative tenant endpoints.",
  "instance": "/v1/tenants",
  "code": "FORBIDDEN",
  "traceId": "00-7e3f1a9b2c4d5e6f7a8b9c0d1e2f3a4b-3333333333333333-00"
}
```

**`404 Not Found` — Clínica inexistente en `GET /v1/tenants/{tenantId}`:**

```json
{
  "type": "https://vital.internal/problems/tenant-not-found",
  "title": "Tenant not found",
  "status": 404,
  "detail": "No tenant was found for the specified identifier.",
  "instance": "/v1/tenants/019abc12-3456-7000-8000-000000009999",
  "code": "TENANT_NOT_FOUND",
  "traceId": "00-7e3f1a9b2c4d5e6f7a8b9c0d1e2f3a4b-4444444444444444-00"
}
```

**`409 Conflict` — Slug `publicId` duplicado en `POST /v1/tenants` (con `existingTenantId` para verificación):**

```json
{
  "type": "https://vital.internal/problems/tenant-public-id-conflict",
  "title": "Tenant public identifier conflict",
  "status": 409,
  "detail": "A tenant with public identifier 'clinica-norte' is already registered.",
  "instance": "/v1/tenants",
  "code": "TENANT_PUBLIC_ID_CONFLICT",
  "traceId": "00-7e3f1a9b2c4d5e6f7a8b9c0d1e2f3a4b-5555555555555555-00",
  "existingTenantId": "019abc12-3456-7000-8000-000000000101"
}
```

**`503 Service Unavailable` — Dependencia requerida no disponible o respuesta inválida/incoherente de `GET /v1/auth/me` (fallo cerrado):**

```json
{
  "type": "https://vital.internal/problems/service-unavailable",
  "title": "Service unavailable",
  "status": 503,
  "detail": "A required dependency is temporarily unavailable to complete the administrative operation safely.",
  "instance": "/v1/tenants",
  "code": "SERVICE_UNAVAILABLE",
  "traceId": "00-7e3f1a9b2c4d5e6f7a8b9c0d1e2f3a4b-6666666666666666-00"
}
```

---

## 7. Matriz breve de trazabilidad con la implementación existente

| Elemento del contrato HTTP v1 | Respaldo existente en `saas-platform` / `saas-identity` | Trabajo requerido al implementar `PLAT-04B` |
| --- | --- | --- |
| `POST /v1/tenants` (entrada, normalización previa, ACID y conflicto con `existingTenantId`) | `RegisterTenantCommand`, `RegisterTenantHandler`, `RegisterTenantResult`, `Tenant.TryRegister`, `TenantProvisioningOperation.TryCreateInitial`, `ux_tenants_public_id` | Añadir endpoint HTTP (`MapPost("/v1/tenants", ...)`), DTO de entrada/salida, log estructurado mínimo con `sub` y ampliar `RegisterTenantResult` / `RegisterTenantHandler` para resolver y exponer `ExistingTenantId` en conflicto sobre `ux_tenants_public_id`. |
| Validaciones de campos (`publicId`, `name`, `contactEmail`, `initialAdminFullName`, `initialAdminEmail`, `maxActiveAccounts`) | `Tenant.cs`, `TenantProvisioningOperation.cs`, `AdministrativeValidation.cs` (`3..64`, regex slug, `2..150`, `<= 254`, `>= 1`, normalización previa a validar) | Reutilizar directamente los handlers/validadores existentes; normalizar `Field` de `TenantValidationError` a `camelCase` en la capa HTTP y rechazar tipos incompatibles en JSON. |
| `GET /v1/tenants/{tenantId}` | `GetTenantByIdHandler`, `GetTenantByIdQuery`, `TenantDetails`, `TenantProvisioningSummary`, `TenantDetailsMapper` | Añadir endpoint HTTP (`MapGet("/v1/tenants/{tenantId}", ...)`), validación de `tenantId` UUID no vacío y proyección DTO sin campos internos ni enums numéricos. |
| `GET /v1/tenants` (paginado acotado y seguro) | Modelo `Tenant`, `TenantProvisioningOperation` y `PlatformDbContext` (`AsNoTracking` + `GroupJoin`) | Crear nuevo Vertical Slice ligero `ListTenants` (`page >= 1`, `1 <= pageSize <= 50`, orden `CreatedAtUtc DESC, Id DESC`, `totalCount` y cortocircuito sin `OFFSET` desbordado). |
| `lastErrorSummary` controlado y exclusión de campos internos | `TenantDetails` / `TenantProvisioningSummary` ya omiten `Lease*`, `ConcurrencyStamp` e `InfrastructureResourceRef`; `SanitizeDiagnosticSummary` sanitiza patrones sensibles en persistencia | Mapear/emitir mensajes controlados específicos para exposición administrativa (no depender únicamente de sanitización regex sobre diagnósticos internos) y mantener proyección DTO cerrada. |
| Autenticación `RS256` + comprobación `GET /v1/auth/me` en mutación | `IDENTITY-SEC-01` (`RS256`, RSA >= 2048 bits, `role=SUPER_ADMIN`, sin `tenant_id`) y `GET /v1/auth/me` (`AccountProfileDto` en `saas-identity`) | Configurar `JwtBearer` `RS256` con `WWW-Authenticate: Bearer`, política `SUPER_ADMIN` (`401` vs `403`) y cliente HTTP corto/configurable hacia `GET /v1/auth/me` en `POST /v1/tenants` (verificando `id == sub`, `active == true`, `tenantId == null`, `role == "SUPER_ADMIN"` con fallo cerrado `503`). |

---

## 8. Decisiones incorporadas en este candidato (pendientes de ratificación formal previa a `PLAT-04B`)

Las siguientes decisiones fueron ajustadas conforme a la revisión del responsable y quedan documentadas explícitamente como línea base candidata para `PLAT-04B`:

| ID | Decisión incorporada | Criterio exacto en el contrato `v1` |
| --- | --- | --- |
| **`D-HTTP-01`** | **Alternativa B en mutaciones sensibles; validación local `RS256` en lecturas** | `GET /v1/tenants` y `GET /v1/tenants/{tenantId}` validan JWT `RS256` localmente. `POST /v1/tenants` valida localmente el JWT **y** consulta `GET /v1/auth/me` en Identity con timeout corto configurable y fallo cerrado (`503` si indisponible/inválido/incoherente, `401` si `401`, `403` si `403`/`active == false`), confirmando `id == sub`, `active == true`, `tenantId == null`, `role == "SUPER_ADMIN"`, sin reenviar cuerpos crudos ni atribuirle revocación de `logout` o bloqueo de cuenta. |
| **`D-HTTP-02`** | **Paginación sencilla, acotada y segura** | `page` default `1` (`1..int32.MaxValue`), `pageSize` default `20` (`1..50`), orden determinista `createdAtUtc DESC, id DESC`, rechazo `400 INVALID_PAGINATION` ante extremos inválidos y evaluación segura sin `OFFSET` desbordado en páginas altas. |
| **`D-HTTP-03`** | **Unicidad por `publicId` sin `Idempotency-Key` y conflicto `409` con `existingTenantId`** | Se conserva la restricción `ux_tenants_public_id` sin cabecera HTTP `Idempotency-Key`, agregando `existingTenantId` en el `ProblemDetails` de `409 TENANT_PUBLIC_ID_CONFLICT` para consulta/verificación vía `GET /v1/tenants/{existingTenantId}`, sin asumir que pertenezca automáticamente al mismo intento del cliente. |
| **`D-HTTP-04`** | **Timestamps ISO-8601 UTC con sufijo `Z`** | Todas las marcas temporales externas conservan sufijo `*AtUtc` y formato RFC 3339 UTC (`Z`), diferenciadas explícitamente de `evaluated_at_gmt6` del contrato gRPC v1 congelado. |
| **`D-HTTP-05`** | **Enums textuales estables y `Problem Details` RFC 9457** | Códigos de estado en cadenas mayúsculas sin valores numéricos ni propiedades `*Code` duplicadas; errores RFC 9457 con `code`, `traceId`, `errors` y cabecera `WWW-Authenticate: Bearer` en `401`. |
| **`D-HTTP-06`** | **Nulabilidad explícita para registros legados** | Se conservan `maxActiveAccounts: null` (filas pre-`PLAT-03A`) y `initialProvisioning: null` / `provisioningStatus: null` (filas pre-`PLAT-02A`). |
