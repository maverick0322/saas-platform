# PLAT-05B — Aprovisionamiento automático de bases privadas por servicio (`ServiceDatabases`)

**Estado:** `INTEGRATION_PASS` (`2026-11-26`, verificado en K3s `1.37.2`, CloudNativePG `1.30.2` y PostgreSQL `17.12`; cerrado en `PLAT-05B-CLOSE`)  
**Repositorio:** `maverick0322/saas-platform`  
**Ramas / commits relevantes:** `develop` (`c8041c145e2292665cef67e5c848239bd95ad3aa`), cierre en `chore/plat-05b-close`  
**Referencias externas:** `maverick0322/saas-infra@571f0605` ([`docs/INFRA-PLAT-05B.md`](https://github.com/maverick0322/saas-infra/blob/571f06056f1e58dea0a58b50ef9f274793dbbdf1/docs/INFRA-PLAT-05B.md)), `Eugene1805/saas-identity@a374584b`, CloudNativePG `v1.30.2`

---

## 1. Resultado del preflight técnico (PostgreSQL 17 real)

Antes de implementar la etapa `ServiceDatabases`, se ejecutó un ensayo desechable sobre `postgres:17-alpine` con TLS activo (`ssl = on`, `password_encryption = 'scram-sha-256'`) y el orden real de `pg_hba.conf` generado por CloudNativePG `1.30.2` (`FIXED RULES` → `USER-DEFINED RULES` → `DEFAULT RULES`):

1. **Propiedad y ACL sin superusuario:** los roles `identity_owner` y `clinical_owner` creados con `NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS` ejecutaron con éxito `REVOKE CONNECT ON DATABASE <propia> FROM PUBLIC` sobre TLS (`has_database_privilege('public', <propia>, 'CONNECT') = false`, propietario `true`, otro rol `false`).
2. **Aislamiento de esquema y frontera entre bases:** ambos propietarios crearon y consultaron tablas en su propia base; al intentar `REVOKE CONNECT` sobre la base del otro servicio PostgreSQL devolvió `WARNING: no privileges could be revoked`. `pg_hba_file_rules` permaneció inaccesible sin privilegios (`permission denied for view pg_hba_file_rules`).
3. **Verificación previa a `LOGIN`:** aun cuando un rol está en `NOLOGIN`, PostgreSQL 17 evalúa primero `pg_hba.conf`: con las reglas activas rechazó las 5 sondas iniciales con SQLSTATE `28000` (`pg_hba.conf rejects connection`), mientras que sin dichas reglas devolvió error de autenticación (`28P01`), permitiendo distinguir de forma fiable `CNPG_PGHBA_NOT_APPLIED` sin utilizar credenciales de superusuario ni leer `Secret` administrativos del operador.
4. **Matriz negativa post-`LOGIN`:** las 10 combinaciones negativas (acceso sin TLS, acceso cruzado entre bases y acceso a `app`/`postgres`/`template1`) fueron rechazadas explícitamente.

---

## 2. Diseño implementado y alcance estricto

- **Servicios obligatorios únicos:**
  - **Identity:** base `identity`, rol propietario `identity_owner`.
  - **Clinical:** base `clinical`, rol propietario `clinical_owner`.
- **Nombres Kubernetes determinísticos (namespace del `Cluster`, máx. 49 caracteres):**
  - `DatabaseRole`: `role-{tenantId:N}-identity`, `role-{tenantId:N}-clinical` (`ensure: present`, `databaseRoleReclaimPolicy: retain`, sin privilegios administrativos).
  - `Database`: `db-{tenantId:N}-identity`, `db-{tenantId:N}-clinical` (`ensure: present`, `databaseReclaimPolicy: retain`, propietario independiente).
  - `Secret` de autenticación CNPG (`kubernetes.io/basic-auth` + etiqueta `cnpg.io/reload=true`): `pg-auth-{tenantId:N}-identity`, `pg-auth-{tenantId:N}-clinical` (contraseñas CSPRNG de 32 bytes independientes por servicio).
- **Reglas `pg_hba` exactas en nuevas clínicas (`CloudNativePg:ServiceDatabases:Enabled=true`):**
  ```text
  hostssl identity identity_owner all scram-sha-256
  hostssl clinical clinical_owner all scram-sha-256
  host all identity_owner,clinical_owner all reject
  ```
  `CloudNativePgClusterEvaluator.HasRequiredServicePgHbaRules` exige exactamente esas tres reglas en ese orden tras normalizar espacios, rechazando reglas adicionales, alteración de orden o reglas permisivas con cualquier dirección o usuario.
- **Distribución consolidada para consumidores (`NpgsqlConnectionStringBuilder` con `SslMode=Require` y host `<writeService>.<namespace>.<ServiceDnsSuffix>`):**
  - Contrato de identificación mínimo del `Secret` preexistente de distribución: `type: Opaque`, etiquetas obligatorias `app.kubernetes.io/managed-by: saas-platform`, `vital.saas/purpose: tenant-db-connections` y `vital.saas/service: identity|clinical`, y ausencia de etiquetas CNPG (`cnpg.io/cluster`, `cnpg.io/reload`).
  - **Identity (contrato vigente `a374584b`):** clave `{tenantId:D}.connection` en el `Secret` consolidado configurado (`TenantConnectionResolver.TryResolveFromSecretsDirectory`).
  - **Clinical (contrato técnico candidato equivalente):** clave `{tenantId:D}.connection` en su `Secret` consolidado separado, ya que no existe aún repositorio `Clinical` implementado. La verificación real de lectura por parte de ambos consumidores corresponde a `PLAT-05C`.
- **Qué NO hace:** no usa superusuario ni lee Secrets administrativos de CNPG; no regenera contraseñas si el rol ya está activo o ya existe una conexión distribuida; no modifica `Clinical`, `Identity`, `saas-infra`, migraciones ni `.proto`; no activa clínicas (`PENDIENTE_ADMIN`, `mayAuthenticate=false`); `ClinicalPreparation` e `IdentityPreparation` continúan devolviendo `AdapterNotConfigured`.

---

## 3. Secuencia real e idempotente de aprovisionamiento (`ServiceDatabases`)

1. Parsear `InfrastructureResourceRef` (`{namespace}/{clusterName}@{uid}`), consultar el `Cluster` en Kubernetes y exigir coincidencia exacta de `UID`, etiquetas de propiedad/operación, estado `Ready` y coincidencia exacta/ordenada de las 3 reglas `pg_hba`.
2. Reconciliar ambos `DatabaseRole` inicialmente con `login: false` (conservando roles ya activados de forma compatible si se reintenta una ejecución parcial), exigiendo `status.applied == true` y `status.observedGeneration >= metadata.generation`.
3. Reconciliar ambos `Database` con su propietario correspondiente, exigiendo `status.applied == true` y `status.observedGeneration >= metadata.generation`.
4. Reconciliar los `Secret` `pg-auth-{tenantId:N}-{service}` (`kubernetes.io/basic-auth`, `cnpg.io/reload=true`): reutiliza Secrets compatibles existentes; antes de crear una contraseña nueva comprueba que el rol siga inactivo (`NOLOGIN`, sin `passwordSecret`) y que el `Secret` `Opaque` de distribución no contenga ya una entrada para el tenant (deteniendo con `CNPG_AUTH_SECRET_CONFLICT` si falta el Secret en ese estado, sin regenerar ni mutar el rol). Además exige que las dos credenciales resultantes sean independientes entre sí.
5. **Verificación previa a `LOGIN`:** comprobar en vivo mediante Npgsql (`VerifyPreLoginPgHbaEnforcementAsync`) que PostgreSQL rechaza con `pg_hba.conf rejects connection` conexiones sin TLS, cruzadas y hacia `app`/`postgres` mientras los roles siguen `NOLOGIN`. Solo tras aprobar, actualizar `DatabaseRole` a `login: true` + `passwordSecret` (esperando reconciliación).
6. Conectarse vía TLS (`SslMode=Require`) como cada propietario y ejecutar `REVOKE CONNECT ON DATABASE <propia> FROM PUBLIC`.
7. Verificar por catálogo SQL (`pg_database`, `pg_roles`, `has_database_privilege`) que solo el propietario tiene `CONNECT`, `PUBLIC` y el otro rol tienen `false` y el rol carece de privilegios elevados.
8. Ejecutar la matriz negativa completa de 10 sondas post-`LOGIN` (sin TLS, cruzado, `app`, `postgres`, `template1`).
9. Actualizar los `Secret` consolidados de distribución (`Opaque` verificados) mediante concurrencia optimista (`resourceVersion` + reintento ante HTTP `409 Conflict`), preservando todas las entradas de otros tenants y rechazando entradas o Secretos incompatibles sin sobrescribirlos (`CNPG_DISTRIBUTION_SECRET_CONFLICT`).

---

## 4. Comprobaciones de seguridad

- Cero persistencia de contraseñas, DSNs, tokens ni valores de `Secret` en la base global de Platform.
- Sin logs ni respuestas HTTP/gRPC con credenciales o cuerpos HTTP crudos de Kubernetes (`ProvisioningAdministrativeErrorCatalog` expone resúmenes controlados).
- Separación estricta API vs Worker: `CreateApiApplication` no registra clientes de Kubernetes, verificadores SQL ni comandos de reanudación.
- Distinción explícita en sondas SQL negativas entre rechazos de autorización (`28000 pg_hba.conf rejects connection` / `42501`) y errores de red, DNS o indisponibilidad (`PG_INFRASTRUCTURE_UNREACHABLE`), que nunca se aceptan como aislamiento válido.

---

## 5. Configuración y semántica de reanudación controlada

### Configuración (`CloudNativePg:ServiceDatabases`)
Deshabilitado por defecto en [`appsettings.json`](../src/Saas.Platform.Api/appsettings.json):
- `Enabled` (`false`), `ServiceDnsSuffix` (`svc`), `PostgreSqlPort` (`5432`), `SqlConnectTimeoutSeconds` (`5`), `SqlCommandTimeoutSeconds` (`10`), `SecretDistributionMaxConcurrencyRetries` (`5`), `IdentityDistributionSecret` (`Namespace`, `Name`), `ClinicalDistributionSecret` (`Namespace`, `Name`).

### Reanudación controlada (`--mode=resume-operation --operation-id=<uuid>`)
- Comando interno explícito en [`Program.cs`](../src/Saas.Platform.Api/Program.cs) / [`ResumeProvisioningOperationHandler.cs`](../src/Saas.Platform.Api/Features/Provisioning/ResumeProvisioningOperationHandler.cs), sin endpoint HTTP ni barrido automático al arranque.
- `TryResumeAdapterStoppedOperationAsync` ejecuta un `UPDATE` atómico que exige: operación exacta, tenant en `PENDIENTE_ADMIN`, estado `FAILED_RETRYABLE`, `manual_intervention_required = TRUE`, `last_error_code = 'ADAPTER_NOT_CONFIGURED'`, ausencia de *lease* activo, `attempt_count < maxTotalAttempts` y soporte real en configuración para la siguiente etapa pendiente.
- Preserva `id`, `provisioning_request_id`, `tenant_id`, `attempt_count`, etapas confirmadas e `infrastructure_resource_ref`, habilitando únicamente la adquisición ordinaria por *lease*.
- **Evaluación de presupuesto de intentos:** con el valor conservador `MaxAutomaticRetries = 3` (`MaxTotalAttempts = 4`), un flujo lineal sin reintentos consume 1 intento en PLAT-05A (`attempt_count = 1`), 1 en PLAT-05B (`attempt_count = 2`), y deja presupuesto para `ClinicalPreparation` (`3`) e `IdentityPreparation` (`4`). Si un entorno requiere tolerancia adicional tras reintentos reales en etapas previas, debe ajustarse explícitamente `ProvisioningWorker:MaxAutomaticRetries` por configuración.

---

## 6. Evidencia de pruebas y resultado durable esperado

- **Pruebas unitarias y de integración:** todas las `147` pruebas previas conservadas intactas, más la suite nueva de PLAT-05B en [`CloudNativePgServiceDatabasesTests.cs`](../tests/Saas.Platform.Tests/CloudNativePgServiceDatabasesTests.cs) y [`CloudNativePgServiceDatabasesIntegrationTests.cs`](../tests/Saas.Platform.Tests/CloudNativePgServiceDatabasesIntegrationTests.cs) (incluyendo verificación Npgsql TLS en vivo sobre `postgres:17-alpine`, persistencia durable y guardas de reanudación).
- **Estado durable tras PLAT-05B satisfactorio:** `Tenant = PENDIENTE_ADMIN`, `mayAuthenticate = false`, `InfrastructureInstance = CONFIRMED`, `ServiceDatabases = CONFIRMED`, `ClinicalPreparation = PENDING`, `IdentityPreparation = PENDING`, operación en `FAILED_RETRYABLE`, `last_error_code = ADAPTER_NOT_CONFIGURED`, `manual_intervention_required = true`, `next_attempt_at_utc = NULL`.

---

## 7. Handoff hacia infraestructura (`saas-infra`) y limitaciones pendientes

1. **RBAC mínimo efectivo para el ensayo de un único tenant:**
   - En `saas-tenant-databases`:
     - `clusters` (`get, create`).
     - `databaseroles` (`get, create, update`).
     - `databases` (`get, create`).
     - `secrets` (`get` restringido por `resourceNames` a `pg-auth-<tenantIdN>-identity` y `pg-auth-<tenantIdN>-clinical`, y `create` sin `resourceNames` por diseño de Kubernetes RBAC).
   - En los namespaces de consumidores (`Identity` y `Clinical`): `Role` + `RoleBinding` con `secrets` (`get, update`) restringidos por `resourceNames` a los dos nombres exactos de los `Secret` consolidados precreados.
   - **Admisión vs lectura (`GET`):** Kubernetes RBAC no restringe `CREATE` mediante `resourceNames`, por lo que `saas-infra` debe acotar la creación de `Secret` mediante `ValidatingAdmissionPolicy` (`pg-auth-*`, tipo `kubernetes.io/basic-auth`, etiquetas de propiedad, `cnpg.io/reload=true`, así como patrón determinístico en `DatabaseRole`/`Database`). Dicha política de admisión sobre `CREATE` **no** limita permisos de `GET`, por lo que `GET` de `Secrets` de autenticación se acota explícitamente mediante `resourceNames` en el `Role`. Sin acceso a Secrets administrativos CNPG, sin `delete`, `pods/exec`, `Jobs` ni gestión de `namespaces`.
2. **`Secrets` de distribución precreados:** deben existir previamente fuera de Argo CD con `type: Opaque` y las etiquetas `app.kubernetes.io/managed-by: saas-platform`, `vital.saas/purpose: tenant-db-connections` y `vital.saas/service: identity|clinical` (sin etiquetas `cnpg.io/*`), montándose en los despliegues consumidores en **solo lectura** (sin `subPath`, permitiendo actualización atómica de kubelet y lectura de `{tenantId:D}.connection`).
3. **Propuesta puntual para el Cluster conservado de PLAT-05A (`pg-01a214affddc713ea908c1f690486d90`):** el Worker no muta Clusters existentes. Antes de reanudar la operación conservada (`01a214af-fddc-75c8-9c5e-7f2317a4e6c2`), `saas-infra` debe aplicar un patch controlado en `saas-tenant-databases` preservando UID, PVC e imagen:
   ```yaml
   spec:
     postgresql:
       pg_hba:
         - hostssl identity identity_owner all scram-sha-256
         - hostssl clinical clinical_owner all scram-sha-256
         - host all identity_owner,clinical_owner all reject
   ```
   y verificar el rollout ordenado/estado `Cluster in healthy state`.
4. **Limitaciones históricas superadas y seguimiento de cierre:** la integración real en K3s con CloudNativePG `1.30.2` quedó validada el `2026-11-26` (Sección 9); permanecen fuera de este incremento el consumo real por `Identity` y `Clinical` (`PLAT-05C`), la validación de CA del servidor PostgreSQL y las etapas posteriores (`ClinicalPreparation` e `IdentityPreparation`).

---

## 8. Incidente histórico de serialización CNPG (`PLAT-05B-FIX-2`) y retiro del mecanismo temporal (`PLAT-05B-CLOSE`)

### 8.1 Causa raíz y auditoría de esquema CloudNativePG `v1.30.2`

Durante la primera ejecución en K3s real de `PLAT-05B` (intento `2`), CloudNativePG `1.30.2` reconcilió correctamente el `DatabaseRole` inicial de Identity (`role-01a214affddc713ea908c1f690486d90-identity`, `UID: eee4ce4c-f650-471a-8e59-01cc13ac0be9`), confirmándose de forma independiente en PostgreSQL que `identity_owner` estaba en `NOLOGIN` y sin privilegios administrativos. Sin embargo, Kubernetes devolvió el objeto omitiendo las 6 propiedades booleanas de `RoleConfiguration` con valor `false` debido a las etiquetas Go `json:"...,omitempty"` en `api/v1/cluster_types.go`:

- `login`, `superuser`, `createdb`, `createrole`, `replication`, `bypassrls`.

El parser anterior (`KubernetesJsonParser.GetBoolOrNull`) interpretaba propiedades ausentes como `null`, provocando que `CloudNativePgDatabaseRoleSnapshot.Evaluate` (`!= false`) detuviera falsamente la operación en `FAILED_TERMINAL` (`CNPG_ROLE_CONFLICT`).

**Auditoría acotada sobre `DatabaseRole` y `Database` (`cloudnative-pg/cloudnative-pg@v1.30.2`):**

- Los **únicos** campos normalizados a `false` cuando faltan en el JSON son esos 6 booleanos en [`ServiceDatabaseTarget.cs`](../src/Saas.Platform.Api/Features/Provisioning/ServiceDatabaseTarget.cs) (`CloudNativePgDatabaseRoleSnapshot.TryParse`). Si el valor JSON presente es `null` explícito o un tipo no booleano, `TryParse` falla de forma cerrada (`false`).
- `ensure` (`+kubebuilder:default:="present"`) y `databaseRoleReclaimPolicy` / `databaseReclaimPolicy` (`+kubebuilder:default:=retain`) poseen valores por defecto en el esquema OpenAPI del CRD aplicados en admisión por Kubernetes, por lo que no requieren normalización especulativa.
- `status.applied` (`*bool`) y `status.observedGeneration` (`int64`) conservan su semántica original sin normalizar ausencia a `false`.

### 8.2 Uso satisfactorio y retiro definitivo de `--mode=recover-plat05b-serialization`

- Con el correctivo **`PLAT-05B-FIX-2`** (`PR #21`, commit `0f992f49`, integrado en `develop` en `c8041c14`, CI post-merge `45776954150` `SUCCESS` con `187/187` pruebas), el mecanismo excepcional `--mode=recover-plat05b-serialization --operation-id=01a214af-fddc-75c8-9c5e-7f2317a4e6c2` se ejecutó una única vez con código de salida `0`, preservando `attempt_count = 2` y `lease_version = 2` sin modificar SQL manualmente ni ampliar el presupuesto de reintentos (`MaxTotalAttempts = 4`).
- Posteriormente, la adquisición ordinaria del Worker en el intento `3` (`lease_version = 3`) detectó y **reutilizó** el `DatabaseRole` existente de Identity (`UID: eee4ce4c-f650-471a-8e59-01cc13ac0be9`) sin recrear tenant, operación, `Cluster`, PVC ni rol, completando `ServiceDatabases`.
- **Cierre (`PLAT-05B-CLOSE`):** cumplido su propósito único en laboratorio, todo el mecanismo temporal (`RecoverPlat05bSerializationIncidentHandler`, `TryRecoverPlat05bSerializationIncidentAsync`, `Plat05bSerializationIncidentContract`, modo CLI `--mode=recover-plat05b-serialization` y sus `3` pruebas exclusivas) fue **eliminado definitivamente**. El comando ordinario `--mode=resume-operation` continúa rechazando `FAILED_TERMINAL`, y la suite queda en **`184/184`** pruebas aprobadas (reducción exclusivamente atribuible al retiro de las `3` pruebas del mecanismo temporal).

---

## 9. Evidencia de integración real (`INTEGRATION_PASS`, 2026-11-26) y cierre

Según la evidencia independiente registrada en [`saas-infra/docs/INFRA-PLAT-05B.md`](https://github.com/maverick0322/saas-infra/blob/571f06056f1e58dea0a58b50ef9f274793dbbdf1/docs/INFRA-PLAT-05B.md):

- **Preparación de seguridad:** `32/32` comprobaciones RBAC efectivas aprobadas, `22/22` pruebas *dry-run* de admisión aprobadas, las 4 políticas adicionales de admisión validadas sin advertencias, `17` recursos de manifiesto sin deriva y reglas `pg_hba` requeridas verificadas.
- **Aceptación funcional y aislamiento en PostgreSQL `17.12` (CNPG `1.30.2` sobre K3s `1.37.2`):**
  - Exactamente `2` `DatabaseRole` y `2` `Database` reconciliados (`identity` con propietario `identity_owner`, `clinical` con propietario `clinical_owner`), ambos con `LOGIN`, sin privilegios administrativos y con `PUBLIC CONNECT` revocado.
  - Credenciales independientes, entradas de distribución coincidentes, `2` sesiones TLS exitosas con propietario/base esperados y `10` sondas negativas rechazadas por `pg_hba` o ACL (acceso cruzado, sin TLS y hacia `app`, `postgres` y `template1`).
  - `3` `Cluster` PostgreSQL saludables, `3` PVC `Bound`, UID (`84fb9b6c-b40a-4c85-b822-810907498f73`) y PVC originales del tenant preservados, y Argo CD (`cloudnativepg` y `saas-platform`) `Synced/Healthy`.
- **Estado durable final verificado:**
  - Tenant `01a214af-fddc-713e-a908-c1f690486d90`: `PENDIENTE_ADMIN`, `mayAuthenticate = false`.
  - Operación `01a214af-fddc-75c8-9c5e-7f2317a4e6c2`: `InfrastructureInstance = CONFIRMED`, `ServiceDatabases = CONFIRMED`, `ClinicalPreparation = PENDING`, `IdentityPreparation = PENDING`, `status = FAILED_RETRYABLE`, `last_error_code = ADAPTER_NOT_CONFIGURED`, `manual_intervention_required = true`, `next_attempt_at_utc = NULL`, `AttemptCount = 3`, `LeaseVersion = 3`, cardinalidad del catálogo `1` tenant / `1` operación y Worker detenido en `0/0`.
- **Fronteras y seguimiento posterior:**
  - El consumo real de las bases por `Identity` y `Clinical` (`PLAT-05C`), el aislamiento multitenant con múltiples clínicas vivas, la verificación de identidad del certificado CA del servidor y respaldo/HA/carga permanecen fuera de alcance de `PLAT-05B`.
  - Bajo el presupuesto conservador vigente (`MaxAutomaticRetries = 3` → `MaxTotalAttempts = 4`), únicamente resta `1` intento disponible tras `AttemptCount = 3`; esta restricción deberá revisarse antes de implementar `ClinicalPreparation` e `IdentityPreparation` (sin alterarla en este cierre). `LAB-10` permanece pospuesto.
