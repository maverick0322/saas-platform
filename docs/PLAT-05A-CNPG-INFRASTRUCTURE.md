# PLAT-05A — Adaptador real de aprovisionamiento CloudNativePG (`InfrastructureInstance`)

**Estado:** `IMPLEMENTATION_PASS` (implementado y verificado localmente con Docker .NET 10, Testcontainers PostgreSQL 17 y dobles controlados de Kubernetes API; sin ejecución en K3s real en este incremento)  
**Repositorio:** `maverick0322/saas-platform`  
**Rama:** `feature/plat-05a-cnpg-infrastructure`  
**Perfil de referencia:** `maverick0322/saas-infra` commit `9678556691464d5485af8801e4767c85230d6f9b` (`DB-01C`)

---

## 1. Alcance y límites estrictos

PLAT-05A conecta únicamente la primera etapa del trabajador durable (`ProvisioningStage.InfrastructureInstance`, núcleo definido en `PLAT-02B/02C`) con CloudNativePG `1.30.2` mediante `KubernetesClient` (`21.0.8`):

- **Qué SÍ hace:**
  - Crea o reconcilia un recurso `postgresql.cnpg.io/v1 Cluster` por clínica dentro de un namespace dedicado único (`CloudNativePg:Namespace`, por defecto `saas-tenant-databases`).
  - Replica exactamente el perfil técnico validado en `DB-01C`: `instances: 1`, imagen PostgreSQL `17.12` fijada por digest inmutable (`ghcr.io/cloudnative-pg/postgresql:17.12-system-trixie@sha256:0f34d3fb12f415713ec94c3f2aa8295c7975f7114a0a8d360f26c958253bd032`), `storageClass: local-path`, tamaño `2Gi`, `requests` `100m`/`256Mi` y `limits` `750m`/`768Mi`.
  - Verifica propiedad (`app.kubernetes.io/managed-by=saas-platform`, `vital.saas/tenant-id`, `vital.saas/provisioning-operation-id`), compatibilidad de especificación y disponibilidad estricta antes de confirmar `InfrastructureInstance` y persistir `infrastructure_resource_ref`.
  - Avanza de forma controlada a `ServiceDatabases`, donde retorna `AdapterNotConfigured` y detiene la operación en `FAILED_RETRYABLE` con `manual_intervention_required = true` (`next_attempt_at_utc = null`), manteniendo la clínica en `PENDIENTE_ADMIN` y `mayAuthenticate = false`.
- **Qué NO hace todavía (diferido a PLAT-05B+):**
  - No crea recursos `DatabaseRole`, `Database`, usuarios PostgreSQL ni reglas `pg_hba` de servicios consumidores (`identity_lab_owner`, `clinical_lab_owner`, etc.).
  - No crea ni lee `Secret` con credenciales o DSNs hacia `Identity` o `Clinical`.
  - No modifica el contrato `.proto` v1 ni añade rutas HTTP/gRPC ni migraciones de base de datos.
  - No activa clínicas ni ejecuta llamadas externas a `Identity` o `Clinical`.

---

## 2. Separación API vs Worker y configuración segura

- **API HTTP + gRPC (`PlatformHostFactory.CreateApiApplication`):** no registra `KubernetesClient`, `CloudNativePgOptions`, `ICloudNativePgClusterClient` ni `IProvisioningStageExecutor`.
- **Worker (`PlatformHostFactory.CreateWorkerHost`):**
  - Valores por defecto en `appsettings.json`: `ProvisioningWorker:Enabled = false` y `ProvisioningWorker:Mode = "Unconfigured"`.
  - Con `Mode = "Unconfigured"`, registra `UnconfiguredProvisioningStageExecutor` (`SupportsRealProvisioning = false`) y no requiere acceso ni credenciales de Kubernetes.
  - Con `Mode = "CloudNativePg"`, valida estrictamente `CloudNativePgOptions` en arranque y registra `KubernetesCloudNativePgClusterClient` (`KubernetesClientConfiguration.InClusterConfig()`) y `CloudNativePgProvisioningStageExecutor` (`SupportsRealProvisioning = true`).
  - Si se activa `ProvisioningWorker:Enabled = true` con `Mode = "CloudNativePg"` fuera de un pod Kubernetes o sin `ServiceAccount` in-cluster válida, `ProvisioningWorkerHostedService` falla de forma explícita y cerrada (`InvalidOperationException`), sin iniciar ciclos ni emitir confirmaciones simuladas.

### Parámetros configurables (`CloudNativePg`)

| Clave | Valor por defecto | Regla de validación |
|---|---|---|
| `Namespace` | `saas-tenant-databases` | Etiqueta DNS-1123 válida, máximo `54` caracteres (garantiza referencia técnica `<= 128`). |
| `ImageName` | `ghcr.io/cloudnative-pg/postgresql:17.12-system-trixie@sha256:0f34d3fb12f415713ec94c3f2aa8295c7975f7114a0a8d360f26c958253bd032` | Referencia no vacía con digest `@sha256:` o tag explícito; prohíbe `:latest`. |
| `Instances` | `1` | Fijado a `1` (una primaria por instancia en PLAT-05A). |
| `StorageClass` | `local-path` | Subdominio DNS-1123 válido. |
| `StorageSize` | `2Gi` | Cantidad Kubernetes positiva (`KubernetesResourceQuantity`). |
| `CpuRequest` / `CpuLimit` | `100m` / `750m` | Cantidades positivas con `CpuRequest <= CpuLimit`. |
| `MemoryRequest` / `MemoryLimit` | `256Mi` / `768Mi` | Cantidades positivas con `MemoryRequest <= MemoryLimit`. |
| `ApiCallTimeoutSeconds` | `10` | Entero en `[1, 60]`. |
| `ReadinessTimeoutSeconds` | `180` | Entero en `[1, 600]`. |
| `ReadinessPollIntervalSeconds` | `3` | Entero en `[1, ReadinessTimeoutSeconds]`. |

---

## 3. Modelo determinístico del `Cluster` y evidencia técnica

### Identidad y manifiesto

- **Nombre determinístico:** `pg-{tenantId:N}` (`35` caracteres; cumple el límite máximo de `50` caracteres impuesto por el webhook de CloudNativePG `1.30.2`, de modo que los objetos derivados como `-replication` no superen los `63` caracteres DNS).
- **Etiquetas obligatorias:**
  - `app.kubernetes.io/managed-by: saas-platform`
  - `app.kubernetes.io/component: tenant-postgres`
  - `vital.saas/tenant-id: <tenantId:D>`
  - `vital.saas/provisioning-operation-id: <operationId:D>`
- **Sin datos sensibles ni PII:** el manifiesto nunca incluye nombre comercial de clínica, correo/nombre del administrador inicial, tokens JWT, contraseñas ni cadenas de conexión.

### Referencia técnica durable (`infrastructure_resource_ref`)

- **Formato:** `{namespace}/{clusterName}@{metadata.uid}` (máximo `127` caracteres cuando `namespace <= 54`, dentro de `VARCHAR(128)` y compatible sin mutación con `TenantProvisioningOperation.SanitizeTechnicalReference`).
- **Validación en procesador:** `ProvisioningOperationProcessor` rechaza toda confirmación de `InfrastructureInstance` con `SupportsRealProvisioning = true` si `TechnicalReference` es nula, vacía o alterada al sanitizar (`INVALID_TECHNICAL_REFERENCE`, requiere intervención manual).

---

## 4. Criterios estrictos de adopción, disponibilidad y fallo

1. **Propiedad y compatibilidad obligatoria:** si el `Cluster` ya existe (por idempotencia, recuperación o colisión HTTP `409 Conflict`), solo puede adoptarse si coincide `namespace`, `name`, `managed-by=saas-platform`, `vital.saas/tenant-id`, `vital.saas/provisioning-operation-id` (cuando está presente), carece de `deletionTimestamp` y coincide en `instances`, `imageName`, `storageClass`, `storageSize` y `resources` (con comparación normalizada de unidades Kubernetes). En cualquier discrepancia falla terminalmente (`CNPG_CLUSTER_OWNERSHIP_CONFLICT`, `CNPG_CLUSTER_DELETING`, `CNPG_CLUSTER_SPEC_MISMATCH`).
2. **Disponibilidad estricta (CloudNativePG v1.30.2):** `status.instances == 1`, `status.readyInstances == 1`, `status.currentPrimary` no vacío, sin cambio de primaria en curso (`targetPrimary` vacío o igual a `currentPrimary`), `status.phase == "Cluster in healthy state"`, condición `Ready == "True"` y `Ready.observedGeneration >= metadata.generation` (cuando ambos campos existen).
3. **Resiliencia con lease y timeouts:**
   - Durante esperas de reconciliación, el bucle de renovación periódica de `ProvisioningOperationProcessor` mantiene vigente el arrendamiento PostgreSQL o cancela el token si otro trabajador adquiere el *lease*.
   - Ante *timeout* o error de red en `CREATE`, retorna `UncertainTimeout` (`CNPG_CREATE_TIMEOUT_UNCERTAIN`), marcando `verification_required = true` para que el siguiente intento consulte `GET` primero antes de emitir otro `CREATE`.
   - Ante error HTTP `401/403`, `400/422` o fase irrecuperable del operador, falla terminalmente (`CNPG_RBAC_FORBIDDEN`, `CNPG_INVALID_MANIFEST`, `CNPG_CLUSTER_UNRECOVERABLE`).
   - Los errores transitorios o expiración de ventana de espera del ciclo devuelven `TransientFailure` (`CNPG_API_UNAVAILABLE` / `CNPG_WAIT`).
   - Todos los códigos `CNPG_*` están mapeados en `ProvisioningAdministrativeErrorCatalog` a mensajes controlados para la API administrativa (`lastErrorSummary`) sin filtrar payloads crudos de Kubernetes.

---

## 5. Requisitos operativos y RBAC pendientes para `saas-infra`

Antes de habilitar `ProvisioningWorker:Enabled=true` y `ProvisioningWorker:Mode=CloudNativePg` en un despliegue real (fuera del alcance de PLAT-05A):

1. **Namespace dedicado previo:** `saas-infra` debe aprovisionar declarativamente el namespace `saas-tenant-databases`.
2. **Identidad y RBAC mínimo namespaced:**
   - Un `ServiceAccount` exclusivo para el Deployment del Worker de Platform.
   - Un `Role` (restringido al namespace `saas-tenant-databases`, nunca `ClusterRole`) con permisos mínimos sobre el grupo `postgresql.cnpg.io`, recurso `clusters`:
     - Verbos necesarios en PLAT-05A: `["get", "create"]` (no requiere `list`, `watch`, `update`, `patch` ni `delete` en esta etapa).
   - Un `RoleBinding` que vincule únicamente al `ServiceAccount` del Worker con dicho `Role`.
3. **Convivencia con Argo CD (sin pruning):**
   - Los recursos `Cluster` creados dinámicamente por el Worker viven en `saas-tenant-databases` y **no** deben formar parte de ninguna `Application` de Argo CD ni llevar etiquetas de seguimiento de Argo CD (`argocd.argoproj.io/instance`), evitando cualquier conflicto de *out-of-sync* o *prune*.
4. **Guardrails recomendados en `saas-tenant-databases`:**
   - `ResourceQuota` y `LimitRange` que acoten el total de pods, PVCs e instancias permitidas por entorno.
   - Controles de admisión (p. ej. Kyverno o ValidatingAdmissionPolicy) restringiendo etiquetas obligatorias y orígenes de imagen permitidos.
   - `NetworkPolicy` aislando el tráfico de red del plano de bases de datos.
