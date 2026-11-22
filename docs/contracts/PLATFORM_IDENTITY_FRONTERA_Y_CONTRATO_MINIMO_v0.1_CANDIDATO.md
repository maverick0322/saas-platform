# Plataforma SaaS Gestión Hospitalaria
## Frontera de Platform y contrato mínimo Platform–Identity — v0.1 (candidato de conciliación)

**Estado:** propuesta de trabajo, no ratificada por ambos desarrolladores.  
**Base documental:** catálogo de casos de uso v0.4.2 modificado por el compañero (17-nov-2026), descripciones v0.1.1 modificadas, contexto y decisiones v0.3 y `CONTRATO_INTERSERVICIOS_v0.1` (13-nov-2026, no aprobado).  
**Objetivo:** permitir desarrollo autónomo inicial de Platform e Identity, sin adoptar contratos de Clinical, BI, Agenda o Billing ni tecnología adicional.

## 1. Fronteras y propiedad

| Responsabilidad | Propietario |
|---|---|
| Tenant ID inmutable, identificador público de clínica, datos administrativos del tenant | **Platform** |
| Estado operativo de la clínica (`PENDIENTE_ADMIN`, `ACTIVA`, `SUSPENDIDA`) | **Platform** |
| Plan SaaS, vigencia, cuotas y límites declarados | **Platform** |
| Coordinación de la creación del primer administrador y su recuperación | **Platform** |
| Cuentas independientes por tenant, credenciales, activación y sesiones | **Identity** |
| Roles organizacionales y verificación de autoridad para concederlos | **Identity** |
| Registro administrativo y expedientes de pacientes | **Clinical** |
| Cobros de pacientes y cortes | **Billing** (candidato) |
| Composición de los ocho KPI | **BI** (candidato) |

**Invariantes:** no compartir tablas/DbContext; no utilizar consultas SQL entre servicios; Platform nunca almacena contraseñas o refresh tokens; SuperAdmin no tiene acceso clínico. Platform tiene su propio catálogo global privado en PostgreSQL; la topología multitenant (instancia PostgreSQL por tenant con base de datos propia por servicio, administrada con CloudNativePG) y los criterios de aprovisionamiento/consistencia fueron formalizados posteriormente en [`ADR-0002`](../adr/ADR-0002-multitenant-persistence.md) y [`ADR-0003`](../adr/ADR-0003-transactional-consistency-and-provisioning.md) (este documento **permanece como borrador candidato no ratificado**). Identity separa cuentas/credenciales por tenant, sin identidad global de profesionales.

## 2. Estados mínimos del tenant

| Estado de origen | Evento | Estado destino | Regla |
|---|---|---|---|
| — | Registro válido por SuperAdmin | `PENDIENTE_ADMIN` | El tenant existe en Platform pero aún no debe habilitar login operativo |
| `PENDIENTE_ADMIN` | Identity confirma que el primer administrador quedó habilitado | `ACTIVA` | Solo después de persistir la confirmación |
| `PENDIENTE_ADMIN` | Identity falla o vence el tiempo de respuesta | `PENDIENTE_ADMIN` | Reintento seguro; no reportar activación |
| `ACTIVA` | Suspensión por SuperAdmin | `SUSPENDIDA` | Sin borrado de registros |
| `SUSPENDIDA` | Reactivación por SuperAdmin | `ACTIVA` | Conservando cuentas y registros; permisos individuales vigentes se vuelven a evaluar |

No se inventa otro estado de tenant por cada error técnico. El error de aprovisionamiento puede quedar como **resultado técnico interno** (código, momento, intentos), sin datos sensibles. No permitir suspender/reactivar `PENDIENTE_ADMIN` por el flujo normal de CU-15.

## 3. Contrato gRPC mínimo — Platform → Identity

### Operación propuesta `ProvisionInitialClinicAdmin`

- **Dueño del RPC:** Identity; **cliente autorizado:** Platform, autenticado como servicio.
- **Objetivo:** preparar el contexto de tenant de Identity si es necesario y crear/habilitar **una** cuenta inicial AdminClinica en ese tenant.
- **Entrada lógica mínima:**
  - `tenantId`: identificador inmutable de clínica emitido por Platform.
  - `provisioningRequestId`: identificador estable de la operación generado y persistido por Platform; se reutiliza en todos los reintentos.
  - `admin`: datos mínimos para identificar la cuenta inicial (por ejemplo `email` y `displayName`, por definir en el esquema).
- **Salida lógica mínima en éxito:** `tenantId`, `initialAdminAccountId`, `provisioningStatus` y `provisioningRequestId`.
- **Significado de éxito:** `READY` solo si Identity confirma que la cuenta inicial está **habilitada para iniciar sesión conforme al mecanismo de credenciales acordado**. Si la cuenta existe pero requiere activación, devolver `PENDING_ACTIVATION` y mantener el tenant `PENDIENTE_ADMIN` hasta confirmar que esté lista, salvo acuerdo explícito distinto.
- **Credenciales:** Identity es responsable de su creación/activación y entrega segura. No incluir contraseñas, tokens de activación ni secretos de un solo uso en la respuesta habitual ni en logs de Platform.
- **Idempotencia:** misma `provisioningRequestId` + `tenantId` + datos de solicitud equivale a la misma operación y debe devolver el mismo identificador de cuenta; no crear duplicados. Reutilizar el ID con contenido diferente produce conflicto explícito. Evitar dos administradores iniciales para un mismo tenant por solicitudes concurrentes.
- **Fallo o timeout:** Platform mantiene `PENDIENTE_ADMIN`, conserva el ID de operación y permite reintento. Un timeout es resultado **desconocido**, no prueba de fracaso de Identity.

**Nota:** el borrador `CONTRATO_INTERSERVICIOS_v0.1` propone dos operaciones (`Preparar identidad de tenant` y `Habilitar cuenta administradora inicial`). Para el MVP **se propone** una única operación idempotente de negocio que encapsule ambos pasos dentro de Identity, si su persistencia lo permite. Si Identity requiere estados internos separados, estos no se exponen como pasos manuales de Platform sin una necesidad demostrable.

## 4. Consulta mínima del estado de tenant — Platform → consumidores autorizados

### Operación propuesta `GetTenantAccessState`

- **Dueño:** Platform; **consumidores potenciales:** Identity para inicio de sesión, Gateway/otros componentes de control si se ratifica.
- **Entrada:** `tenantId` obtenido de un contexto confiable, no de una cabecera de usuario sin validar.
- **Salida:** `tenantId`, `state`, `mayAuthenticate` calculado (`true` solo si `ACTIVA`), momento de evaluación si se requiere.
- **Restricciones:** no devolver datos comerciales, clínicos ni información personal del administrador.
- **Semántica de indisponibilidad:** si Identity no puede comprobar de forma confiable el estado del tenant, no debe conceder una sesión nueva basándose en un estado desconocido. La revocación de sesiones ya emitidas y la política de verificación en servicios protegidos se definirán en el contrato de seguridad transversal.

**Pendiente de ratificar:** cómo se resuelve la clínica antes del login (slug/subdominio) y si esta operación se ofrece por gRPC a Identity, al Gateway o a ambos. No establecer dependencias circulares en el alta: Identity recibe una orden autenticada de Platform y no necesita consultar a Platform para crear la cuenta inicial.

## 5. API HTTP/JSON externa mínima de Platform (nombres candidatos)

| Operación pública para SuperAdmin | Efecto |
|---|---|
| `POST /v1/tenants` | Solicitar alta; crea `PENDIENTE_ADMIN`, intenta aprovisionamiento, comunica el estado real obtenido |
| `GET /v1/tenants/{tenantId}` | Consultar ficha administrativa y estado de aprovisionamiento |
| `POST /v1/tenants/{tenantId}/retry-initial-admin` | Reintentar solo si está pendiente, reutilizando `provisioningRequestId` |
| `POST /v1/tenants/{tenantId}/suspend` | Suspender clínica `ACTIVA` (CU-15; puede implementarse después del contrato inicial) |
| `POST /v1/tenants/{tenantId}/reactivate` | Reactivar clínica `SUSPENDIDA` (CU-15; puede implementarse después del contrato inicial) |

Son **ejemplos de rutas**, no OpenAPI aprobado. Una solicitud de creación puede responder éxito con `ACTIVA` solo si la habilitación está confirmada; de lo contrario debe indicar `PENDIENTE_ADMIN` (por ejemplo, mediante una respuesta aceptada/persistida según la semántica HTTP que se acuerde). Nunca declarar completado un alta incompleta.

## 6. Errores de integración y seguridad

| Situación | Comportamiento requerido |
|---|---|
| Llamador gRPC no autenticado o sin autoridad | Rechazar; no crear cuentas |
| Tenant o campos inválidos | Rechazar con error verificable, sin credenciales ni detalles sensibles |
| Operación repetida idéntica | Retornar resultado de la operación original; no duplicar |
| Misma clave de operación con otro contenido | Conflicto; no sobrescribir cuenta existente |
| Identity no disponible / timeout / pérdida de respuesta | Mantener `PENDIENTE_ADMIN`, conservar la operación y reintentar de forma controlada |
| Reintento después de éxito parcial en Identity | Reconocer cuenta ya creada y terminar sin duplicación |
| Solicitud de SuperAdmin para consultar pacientes/notas | No exponer; Platform no posee esos datos |
| Usuario de clínica `SUSPENDIDA` intenta autenticarse | No otorgar nueva sesión; política para sesiones existentes a ratificar en seguridad transversal |

Tiempo límite, número de reintentos, autenticación servicio-a-servicio y política de activación del primer administrador se concretan en el contrato técnico. No hacer reintentos ilimitados ni registrar secretos.

## 7. Criterios de aceptación de la primera integración

1. `CreateTenant` con Identity disponible: un tenant y un administrador inicial; estado `ACTIVA` solo cuando la cuenta está habilitada.
2. Identity falla antes de crear la cuenta: Platform conserva `PENDIENTE_ADMIN` y no informa `ACTIVA`.
3. Identity completa el alta pero se pierde la respuesta: el reintento con el mismo ID recupera el mismo `initialAdminAccountId` sin duplicar.
4. Dos solicitudes concurrentes/reintentos para el mismo alta no producen dos administradores iniciales.
5. Cuenta de clínica A no se confunde con una cuenta de clínica B; no hay identidad global compartida.
6. Tenant `SUSPENDIDA` o `PENDIENTE_ADMIN` no recibe nuevas sesiones ordinarias.
7. Un SuperAdmin no obtiene datos clínicos mediante Platform; no se registran credenciales en logs.
8. Los tests de contrato de Platform e Identity ejecutan dobles de prueba de forma autónoma y existe al menos una prueba de integración real de ambos servicios antes de promocionar versiones compatibles.

## 8. Acuerdos mínimos pendientes entre los dos desarrolladores

**D-01.** Confirmar que Identity expondrá `ProvisionInitialClinicAdmin` como un RPC idempotente (o justificar dos RPC si es indispensable).  
**D-02.** Definir cuándo se considera *habilitada* la primera cuenta y quién entrega/activa las credenciales; Platform nunca recibe contraseñas.  
**D-03.** Ratificar los campos y estados mínimos de respuesta, las claves de idempotencia y los errores observables.  
**D-04.** Acordar `GetTenantAccessState` y el mecanismo de resolución de clínica previo al login; registrar por separado la política de revocación de sesiones activas.  

No bloquear por estos puntos los esqueletos .NET, tests, CI/GHCR ni las operaciones locales de catálogo/planes de Platform. **Sí bloquear la integración efectiva del aprovisionamiento** hasta que ambos ratifiquen los contratos.

## 9. Alcance inmediato y exclusiones

**Primera entrega:** registro de tenant, consulta administrativa de su estado y operación de aprovisionamiento reintentable.  
**Segunda entrega:** suspensión/reactivación; catálogo de planes/cuotas/vigencias; consultas agregadas propias de Platform.  

**Fuera del contrato mínimo:** facturación de suscripciones, pagos reales, datos de pacientes, reglas de acceso clínico, gestión de personal ordinario, API de BI, RabbitMQ, Saga completa, CQRS distribuido y transacciones entre bases independientes.

**Propuesta de arquitectura interna de Platform (no vinculante):** Vertical Slice Architecture, comandos/consultas ligeros, dominio explícito para transiciones e invariantes, EF Core/PostgreSQL privado y cliente gRPC encapsulado detrás de un puerto pequeño. No obligar a Identity a compartir estructura interna o entidades con Platform.
