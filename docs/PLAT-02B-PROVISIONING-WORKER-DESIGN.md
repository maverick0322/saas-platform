# PLAT-02B — Diseño del trabajador de aprovisionamiento y contratos mínimos

**Proyecto:** SaaS Vital — Plataforma multitenant de clínicas  
**Versión:** 1.0 — 24 de noviembre de 2026  
**Estado:** Diseño lógico consolidado. Pendiente de ratificación técnica interservicios y autorización de implementación.  
**Repositorio:** `saas-platform`  
**Base:** PLAT-02A, ADR-0002, ADR-0003 y contrato Platform–Identity gRPC v1.

## 1. Objetivo

Diseñar un mecanismo recuperable, idempotente y verificable para aprovisionar clínicas nuevas sin intervención manual de Kubernetes por parte del SuperAdmin.

Platform coordinará la operación; cada servicio conservará sus datos, migraciones y reglas de negocio.

El mecanismo utilizará la base global PostgreSQL de Platform para registrar operaciones y su progreso. No requiere RabbitMQ, Redis ni un motor genérico de Sagas.

## 2. Decisiones confirmadas

1. Una instancia PostgreSQL independiente por clínica, administrada mediante CloudNativePG, con bases privadas por microservicio.
2. Servicios obligatorios para activar: Identity y Clinical, además de la infraestructura necesaria.
3. Identity `READY` confirma únicamente su responsabilidad. Platform decide la activación global.
4. Reintentos automáticos limitados y recuperación hacia adelante. Sin borrado automático de bases o datos ante fallos.
5. Los errores persistentes requieren intervención administrativa autorizada.
6. Las clínicas procedentes de PLAT-01, si existen, se conservan pendientes sin fabricar operaciones retroactivas.
7. Las cuotas serán configurables por clínica mientras no exista el catálogo definitivo de planes.
8. Identity generará y entregará las contraseñas temporales; Mailpit será el mecanismo de correo para la demostración.
9. La API pública de Platform y su trabajador tendrán identidades y permisos de Kubernetes separados.

## 3. Flujo de aprovisionamiento

| Paso | Responsable | Confirmación requerida |
|---|---|---|
| 1. Registro | Platform API | Tenant y operación inicial persistidos atómicamente |
| 2. Infraestructura | Trabajador Platform / CloudNativePG | Instancia PostgreSQL dedicada preparada y accesible |
| 3. Bases privadas | Trabajador Platform | Bases y usuarios PostgreSQL privados de Identity y Clinical creados o verificados |
| 4. Conectividad | Infraestructura y servicios propietarios | Conexiones y credenciales disponibles para los servicios correctos |
| 5. Preparación Clinical | Clinical | Base propia migrada y preparada |
| 6. Preparación Identity | Identity | Base propia preparada, administrador inicial creado y resultado `READY` |
| 7. Activación | Platform | Todas las evidencias válidas; operación `COMPLETED` y tenant `ACTIVA` |

El orden concreto de las preparaciones Clinical e Identity podrá ajustarse si no cambia sus dependencias ni los criterios de activación.

Ningún servicio debe acceder a tablas privadas de otro servicio.

La preparación de Identity debe funcionar mientras el tenant continúa `PENDIENTE_ADMIN`, sin exigir que ya esté `ACTIVA`.

## 4. Estados y transiciones

### 4.1 Estado operativo del tenant

Se conservan los tres estados existentes:

- `PENDIENTE_ADMIN`: registrado, pero sin acceso operativo.
- `ACTIVA`: todos los requisitos obligatorios confirmados.
- `SUSPENDIDA`: acceso bloqueado sin eliminar información ni infraestructura.

PLAT-02B se concentra exclusivamente en preparar una transición segura de `PENDIENTE_ADMIN` a `ACTIVA`.

La suspensión y reactivación comercial corresponden a otro incremento.

### 4.2 Estado técnico de aprovisionamiento

Se reutilizan los estados existentes de PLAT-02A.

| Estado origen | Evento | Estado destino |
|---|---|---|
| `PENDING` | Trabajador adquiere la operación | `IN_PROGRESS` |
| `IN_PROGRESS` | Falla recuperable | `FAILED_RETRYABLE` |
| `FAILED_RETRYABLE` | Reintento autorizado | `IN_PROGRESS` |
| `IN_PROGRESS` | Error no recuperable | `FAILED_TERMINAL` |
| `IN_PROGRESS` | Todos los pasos confirmados | `COMPLETED` |
| `IN_PROGRESS` | Reinicio, timeout o pérdida del trabajador | Recuperación controlada después de vencer su arrendamiento |

Una operación con reintentos automáticos agotados podrá permanecer `FAILED_RETRYABLE`, pero sin fecha de reintento automático y con intervención administrativa pendiente.

`FAILED_TERMINAL` no se reintentará automáticamente. Su resolución requerirá revisar la causa y autorizar expresamente una recuperación. No se eliminará la operación para intentar crear otra con un nuevo identificador.

`COMPLETED` es final para el aprovisionamiento inicial.

## 5. Adquisición de trabajo y concurrencia

**Diseño técnico propuesto:** un trabajador de Platform, inicialmente con una sola réplica, consultará periódicamente su base global.

Para cada operación:

1. La adquisición se realizará atómicamente en PostgreSQL.
2. Se guardará un identificador de propietario del trabajo y un vencimiento de arrendamiento (*lease*).
3. Otros trabajadores no podrán adquirir una operación con arrendamiento vigente.
4. El trabajador renovará el arrendamiento durante operaciones prolongadas.
5. Después de un reinicio, un arrendamiento vencido permitirá recuperación.
6. Las actualizaciones comprobarán que quien escribe todavía posee la operación, para impedir que un trabajador obsoleto sobrescriba resultados.

Puede emplearse `FOR UPDATE SKIP LOCKED` durante la adquisición, liberando la transacción antes de iniciar llamadas externas. No deben mantenerse bloqueos PostgreSQL mientras Kubernetes crea una instancia.

No es necesario ejecutar múltiples réplicas para el MVP; estas precauciones evitan duplicaciones y permiten recuperación segura.

### Persistencia adicional mínima prevista

Sobre la entidad de PLAT-02A, evaluar incorporar:

- `lease_owner` y `lease_until_utc`, junto con un número de versión o mecanismo equivalente de exclusión.
- `next_attempt_at_utc` para controlar reintentos.
- Una representación durable pequeña de pasos confirmados.
- Identificador de la cuenta inicial devuelto por Identity.
- Referencias técnicas no sensibles a recursos aprovisionados.

No almacenar contraseñas, cadenas de conexión, secretos de Kubernetes ni respuestas de excepción completas.

`AttemptCount` deberá contar **intentos iniciados**, no únicamente errores. Su comportamiento actual de PLAT-02A se ajustará al implementar el trabajador.

## 6. Recuperación y errores

Los errores se clasificarán como:

**Transitorios:** indisponibilidad temporal de Kubernetes, PostgreSQL no preparado todavía, timeout gRPC, reinicio del trabajador o fallo temporal del servicio.

**No recuperables automáticamente:** configuración inválida persistente, permisos insuficientes, violación de contrato, conflicto de datos de aprovisionamiento o recursos incompatibles.

Los errores transitorios tendrán reintentos limitados con espera creciente y límites configurables. Como valor inicial candidato para el laboratorio se proponen tres reintentos automáticos, pendiente de ajuste mediante pruebas.

Ante un timeout no se supondrá que el paso falló. Antes de repetirlo se verificará si el sistema externo ya lo completó.

La recuperación será hacia adelante: reconocer los recursos existentes y continuar. No se eliminarán automáticamente instancias, bases ni cuentas por fallos posteriores.

Los diagnósticos persistidos utilizarán códigos y mensajes controlados. Las expresiones regulares de redacción son una defensa adicional, no la garantía principal contra filtración de secretos.

## 7. Contratos mínimos

### 7.1 Platform ↔ CloudNativePG

Contrato conceptual para el adaptador de infraestructura del trabajador:

**Preparar instancia**

Entrada: `tenantId` y configuración técnica autorizada.

Resultado: referencia de recurso, estado verificado y evidencia de disponibilidad.

**Preparar base privada de servicio**

Entrada: `tenantId`, identificador del servicio y referencia de la instancia.

Resultado: base y rol PostgreSQL preparados, referencia segura a credenciales y confirmación de conectividad.

**Consultar estado**

Entrada: referencia del recurso.

Resultado: estado observado y error técnico controlado cuando corresponda.

Estas operaciones deben ser idempotentes. La implementación exacta depende de DB-01B/01C.

La infraestructura declarativa del operador pertenece a Argo CD; las instancias dinámicas de tenants pertenecen al aprovisionador.

### 7.2 Platform → Identity

Se reutiliza sin modificaciones el RPC existente:

`IdentityProvisioningService.ProvisionInitialClinicAdmin`

Entrada: `tenant_id`, `provisioning_request_id`, `clinic_slug`, `clinic_name`, `admin_full_name`, `admin_email`.

Resultado requerido: `tenant_id` y `provisioning_request_id` correspondientes, `status = READY`, `initial_admin_account_id` válido y resultado de entrega de contraseña temporal.

Identity conserva sus migraciones y ejecuta su preparación utilizando la conexión del tenant.

Platform conserva inmutable el identificador de solicitud y los datos asociados durante los reintentos.

La respuesta de Identity no activa automáticamente la clínica.

### 7.3 Platform → Clinical (contrato candidato)

Clinical todavía no tiene implementación que debamos preservar.

Se propone una capacidad interna mínima para preparar/verificar su base, sin imponer todavía HTTP o gRPC:

**`EnsureClinicalTenantReady`** (nombre conceptual).

Entrada:

- `tenantId` emitido por Platform.
- Identificador estable de la operación.
- Referencia o contexto seguro de la base Clinical previamente preparada.

Resultado:

- `tenantId` correspondiente.
- Estado `READY`, `PENDING` o `FAILED`.
- Versión o identificador verificable de migraciones aplicadas.
- Código de error controlado cuando corresponda.

Requisitos:

- Idempotencia ante solicitudes repetidas.
- Migraciones exclusivamente propiedad de Clinical.
- Sin necesidad de crear pacientes, expedientes o registros clínicos ficticios para confirmar preparación.
- Sin devolver información clínica ni secretos.
- No exigir que el tenant esté `ACTIVA` durante la preparación técnica.
- Fallo cerrado cuando la base o sus migraciones no estén listas.

**Este contrato no queda ratificado automáticamente para Clinical.** Debe revisarse cuando se construya ese servicio.

### 7.4 Platform → Identity: estado y cuotas

Se conserva el contrato existente:

`TenantAccessService.GetTenantAccessState`

Platform resolverá tenants por UUID o identificador público (`PublicId`/`slug`).

`may_authenticate` será verdadero únicamente cuando el tenant esté `ACTIVA`.

La cuota `max_active_accounts` procederá de una configuración explícita por clínica mientras no exista el catálogo definitivo de planes. El valor deberá ser válido y coherente con las cuentas iniciales; no se inventarán planes comerciales.

Si Platform no puede comprobar confiablemente el estado del tenant, Identity no otorgará acceso basándose en estados desconocidos.

## 8. Gestión de credenciales y mínimo privilegio

El trabajador tendrá los permisos Kubernetes estrictamente necesarios para preparar los recursos dinámicos autorizados.

La API pública de Platform no tendrá privilegios de creación de clústeres CloudNativePG ni acceso administrativo a secretos de tenants.

Cada microservicio dispondrá de credenciales PostgreSQL propias y solo podrá utilizar sus bases autorizadas.

Se deberá verificar cómo reciben Identity y Clinical nuevas conexiones sin reiniciar manualmente toda la plataforma ni exponer secretos al frontend.

El mecanismo de distribución y actualización de secretos, los permisos Kubernetes exactos y la compatibilidad con la resolución dinámica ya implementada en Identity se cerrarán con la evidencia de DB-01C.

## 9. Activación final

Platform solo podrá completar el aprovisionamiento cuando confirme:

1. Instancia dedicada del tenant disponible.
2. Bases privadas requeridas preparadas.
3. Migraciones de Clinical confirmadas.
4. Preparación de Identity confirmada mediante `READY`.
5. Identificador válido del administrador inicial, asociado al tenant y a la operación esperados.

Las confirmaciones deberán proceder de fuentes internas confiables, nunca de booleanos enviados por un cliente HTTP.

La actualización de `tenant_provisioning_operations` a `COMPLETED` y la transición del tenant a `ACTIVA` se realizarán en una única transacción PostgreSQL de Platform, protegida contra concurrencia.

No se declarará completada una operación con evidencia incompleta o desactualizada.

## 10. Clínicas anteriores a PLAT-02A

Si existen registros de PLAT-01 sin operación de aprovisionamiento:

- Permanecerán `PENDIENTE_ADMIN`.
- No se crearán cuentas ni operaciones automáticamente.
- Una futura operación administrativa autenticada podrá completar sus datos y registrar la intención inicial atómicamente.
- No se desarrollará este flujo adicional mientras no exista una necesidad verificable en el laboratorio.

## 11. Pruebas de aceptación previstas

Antes de aceptar el trabajador operativo deberán verificarse:

1. Una operación pendiente se adquiere una sola vez por ciclo de ejecución.
2. Dos trabajadores concurrentes no completan indebidamente el mismo trabajo.
3. Un reinicio permite recuperar una operación sin perder su identificador.
4. Un timeout externo no duplica instancias, bases ni administradores.
5. Los reintentos respetan los límites configurados.
6. Un error persistente deja la operación diagnosticable sin destruir recursos.
7. Identity `READY` por sí solo no permite activar la clínica.
8. Clinical no preparado impide la activación.
9. La confirmación completa produce `COMPLETED` y `ACTIVA` de manera atómica.
10. No se guardan secretos en la base ni en logs.
11. La API pública carece de permisos privilegiados de Kubernetes.
12. Los contratos entre servicios son comprobables mediante pruebas autónomas y una integración real posterior.

## 12. Dependencias para implementación

**No bloqueantes para diseño y pruebas locales:** instalación de CloudNativePG y existencia del microservicio Clinical.

**Obligatorias para integración real:** DB-01B/01C, mecanismo probado de entrega de conexiones y credenciales, contrato de preparación de Clinical y verificación del aprovisionamiento de Identity.

## 13. Fuera de alcance

RabbitMQ, Redis, motor genérico de Sagas, reconciliación compleja de clústeres, alta disponibilidad de PostgreSQL, eliminación automática de tenants, facturación real, aprovisionamiento de todas las especialidades, paneles completos y migración masiva de bases históricas.

## 14. Criterio de cierre

PLAT-02B tendrá su diseño cerrado cuando sus invariantes, responsabilidades, estados, recuperación y contratos conceptuales estén documentados.

Su implementación y aceptación funcional serán incrementos posteriores con pruebas reales.

**No se considera que una clínica esté operativa hasta que Platform confirme todos los requisitos obligatorios.**
