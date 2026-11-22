# ADR-0002 — Persistencia multitenant y topología PostgreSQL

- **Fecha:** 2026-11-22
- **Estado:** aprobada (formalización arquitectónica previa a PLAT-01)
- **Ámbito:** topología de persistencia multitenant del sistema **Plataforma SaaS Gestión Hospitalaria** y responsabilidades de datos de `saas-platform` y los microservicios confirmados
- **Referencias relacionadas:** [`ADR-0001`](ADR-0001-vertical-slice-platform.md), [`ADR-0003`](ADR-0003-transactional-consistency-and-provisioning.md), [`PLATFORM_IDENTITY_FRONTERA_Y_CONTRATO_MINIMO_v0.1_CANDIDATO.md`](../contracts/PLATFORM_IDENTITY_FRONTERA_Y_CONTRATO_MINIMO_v0.1_CANDIDATO.md) (borrador candidato no ratificado)

## Contexto

El sistema **Plataforma SaaS Gestión Hospitalaria** es un proyecto académico (dos desarrolladores, ocho semanas, exclusivamente datos sintéticos) orientado a clínicas independientes en modalidad SaaS multitenant. Para la demostración se prevén **cuatro o cinco tenants sintéticos** ejecutándose sobre un laboratorio con recursos limitados (actualmente un nodo K3s y futura incorporación de una Surface).

En [`ADR-0001`](ADR-0001-vertical-slice-platform.md) se definió la arquitectura interna de `saas-platform` (Vertical Slice Architecture), dejando expresamente abierta la topología definitiva `tenant × servicio × base de datos`. Antes de iniciar la implementación de persistencia y catálogo en PLAT-01, es necesario fijar el modelo de aislamiento de datos, la propiedad de esquemas y la tecnología de operación de PostgreSQL en Kubernetes, sin acoplar la lógica de dominio a detalles físicos de infraestructura ni adelantar contratos interservicios pendientes.

## Decisión

Se adopta la **alternativa B: una instancia PostgreSQL independiente por tenant**, administrada en Kubernetes mediante **CloudNativePG**, combinada con **aplicaciones compartidas** para los microservicios de negocio y una **base global independiente** para Platform:

1. **Instancia PostgreSQL independiente por tenant:** cada clínica (tenant) dispone de su propia instancia PostgreSQL dedicada en Kubernetes, administrada por el operador **CloudNativePG**.
2. **Microservicios compartidos (multitenant en cómputo):** Identity, Clinical y los demás microservicios confirmados operan como aplicaciones compartidas que atienden múltiples tenants. No se despliegan copias de los microservicios por tenant.
3. **Base de datos propia por servicio dentro de cada instancia de tenant:** dentro de la instancia PostgreSQL de un tenant, cada microservicio que persiste datos del tenant posee su propia base de datos lógica separada.
4. **Base global independiente de Platform:** Platform mantiene su propia base de datos PostgreSQL global, independiente de las instancias por tenant, destinada al catálogo administrativo de clínicas y metadatos propios de Platform.
5. **Aislamiento estricto entre servicios:** está prohibido compartir tablas, entidades de persistencia, `DbContext`, consultas SQL o claves foráneas entre microservicios.
6. **Propiedad exclusiva de esquemas y migraciones:** el esquema y las migraciones de cada servicio siguen siendo responsabilidad exclusiva del servicio propietario. Ningún servicio altera el esquema de otro.
7. **Resolución de conexiones fuera del dominio:** las conexiones hacia la base de datos del tenant correspondiente se resuelven en la capa de infraestructura/adaptadores a partir de un **contexto de tenant autenticado y autorizado**, nunca desde encabezados no confiables ni dentro de las reglas de dominio.
8. **Independencia de la lógica funcional respecto a la ubicación física:** la lógica funcional y el dominio deben permanecer independientes de la ubicación física, nombres internos o topología de despliegue de PostgreSQL.
9. **Escala objetivo del laboratorio:** el diseño se dimensiona para **cuatro o cinco tenants sintéticos** en el entorno de demostración.

## Justificación

- **Aislamiento fuerte de datos por clínica:** separar físicamente los datos en una instancia PostgreSQL por tenant reduce de manera drástica el riesgo de fugas transversales por errores en filtros `WHERE tenant_id = ...` y acota el impacto operativo por clínica.
- **Límites limpios entre microservicios:** asignar una base de datos independiente por servicio dentro de la instancia del tenant preserva la autonomía de modelo, evolución de esquema y migraciones de cada servicio (Platform, Identity, Clinical y demás servicios confirmados).
- **Equilibrio frente a recursos del laboratorio:** compartir las instancias de aplicación de los microservicios evita multiplicar pods de cómputo .NET por cada clínica, haciendo viable demostrar cuatro o cinco tenants sintéticos en un laboratorio limitado.
- **Operación declarativa en Kubernetes:** CloudNativePG permite modelar y administrar instancias PostgreSQL y sus recursos asociados de manera nativa en Kubernetes sin recurrir a scripts manuales de administración de bases de datos.
- **Separación entre catálogo global y datos de tenant:** la base global independiente de Platform permite administrar el ciclo de vida de los tenants sin mezclar catálogo administrativo con datos operativos de las clínicas.

## Alternativas consideradas

1. **Alternativa A — Instancia PostgreSQL compartida con esquema compartido (`tenant_id` en tablas) o esquema por tenant:**
   - *Ventajas:* menor consumo de memoria y almacenamiento en un laboratorio pequeño; menor complejidad de aprovisionamiento inicial.
   - *Desventajas:* menor aislamiento físico entre clínicas; mayor exposición a errores de filtrado o configuración de Row-Level Security; mayor acoplamiento operativo entre tenants al compartir motor y recursos.
   - *Motivo de descarte:* se prioriza un aislamiento de datos más fuerte y demostrable acorde al dominio clínico/académico del proyecto.
2. **Silo completo (instancia de microservicios + instancia PostgreSQL dedicada por tenant):**
   - *Ventajas:* aislamiento total de cómputo y datos.
   - *Desventajas:* multiplicación inviable de pods, memoria y complejidad operativa en un nodo K3s de laboratorio para 4–5 tenants.
   - *Motivo de descarte:* desproporcionado para el alcance, recursos y equipo del proyecto.
3. **Base de datos única compartida entre todos los microservicios dentro del tenant:**
   - *Ventajas:* menos objetos de base de datos que crear por tenant.
   - *Desventajas:* acoplamiento por esquema, riesgo de consultas cruzadas, colisión de migraciones y pérdida de propiedad de datos por servicio.
   - *Motivo de descarte:* contradice las fronteras de microservicios y los principios del proyecto.
4. **PostgreSQL en Kubernetes con `StatefulSet` administrado manualmente (sin operador):**
   - *Ventajas:* sin dependencia de un operador externo.
   - *Desventajas:* gestión manual de inicialización, credenciales, ciclo de vida y recuperación básica.
   - *Motivo de descarte:* CloudNativePG reduce carga operativa repetitiva y estandariza la gestión de instancias.

## Consecuencias

### Positivas

- Cada clínica cuenta con una frontera física clara de almacenamiento y motor PostgreSQL.
- Cada servicio evoluciona su propio esquema, entidades y migraciones de forma independiente.
- El dominio de los microservicios no depende de direcciones físicas, nombres de clúster ni detalles de CloudNativePG.
- Se evita la proliferación de copias de microservicios por tenant.

### Negativas y de costo operativo

- Mayor consumo base de memoria, CPU y almacenamiento persistente en Kubernetes respecto a una instancia PostgreSQL compartida (una instancia por cada tenant más la base global de Platform).
- Los microservicios compartidos requieren un mecanismo seguro y auditable de resolución dinámica de cadena de conexión/credenciales por tenant autenticado y autorizado.
- El alta de un tenant deja de ser únicamente un registro lógico en Platform y pasa a coordinar recursos PostgreSQL y preparación de bases de datos (véase [`ADR-0003`](ADR-0003-transactional-consistency-and-provisioning.md)).

## Riesgos

- **Compatibilidad CloudNativePG con K3s/Kubernetes 1.37.2:** la versión de Kubernetes/K3s del laboratorio (`1.37.2`) ha sido probada, pero **no está oficialmente soportada para CloudNativePG 1.30**. Cualquier anomalía de CRD, API deprecada o comportamiento del operador deberá verificarse empíricamente antes de depender de funcionalidades avanzadas.
- **Recursos limitados del laboratorio:** actualmente el entorno cuenta con un solo nodo K3s y se prevé la incorporación futura de una Surface. Crear más instancias PostgreSQL de las previstas (4–5 tenants sintéticos) o configurar solicitudes/límites inadecuados puede agotar memoria, CPU o disco.
- **Almacenamiento persistente, credenciales, permisos y acceso entre servicios:** es necesario definir políticas claras para `StorageClass`/PVC, almacenamiento y entrega segura de credenciales por tenant y por servicio, mínimo privilegio de roles PostgreSQL y aislamiento/acceso de red entre servicios y bases de datos.
- **Conflicto entre GitOps y recursos dinámicos:** gestionar recursos dinámicos de tenants con la misma propiedad que la infraestructura declarativa estática de Argo CD provocaría conflictos de reconciliación o borrados accidentales (tratado en [`ADR-0003`](ADR-0003-transactional-consistency-and-provisioning.md)).
- **Resolución insegura de tenant:** resolver la conexión a partir de cabeceras o parámetros no validados abriría vulnerabilidades de acceso entre tenants.
- **Contratos pendientes con Identity y Clinical:** los mecanismos exactos de inicialización de bases por servicio, propagación de contexto de tenant y criterios definitivos para declarar un tenant `ACTIVA` continúan pendientes de conciliación.

## Garantías

- **Aislamiento físico por tenant:** los datos de dos clínicas distintas no residen en la misma instancia PostgreSQL.
- **Aislamiento lógico por servicio:** ningún servicio accede directamente a la base de datos, tablas o entidades de otro servicio.
- **Propiedad de migraciones:** cada servicio aplica y controla únicamente sus propias migraciones.
- **Independencia del dominio:** el código de dominio no contiene dependencias hacia CloudNativePG, Kubernetes ni topología física de conexiones.
- **Protección de datos ante suspensión:** la suspensión de una clínica no implica borrado automático de sus datos ni de su instancia PostgreSQL (véase [`ADR-0003`](ADR-0003-transactional-consistency-and-provisioning.md)).

## Exclusiones

Queda explícitamente fuera del alcance de esta decisión y del MVP:

- Copias de los microservicios desplegadas por tenant.
- Funcionalidades diferenciadas por especialidad médica.
- Alta disponibilidad (HA) de PostgreSQL (múltiples réplicas por tenant, failover automático o backups productivos avanzados) en el MVP.
- Migraciones masivas automatizadas de esquemas existentes a gran escala.
- Traslado automatizado de datos de un tenant entre instancias PostgreSQL.
- Definición de esquemas de tablas definitivos, rutas HTTP, contratos RPC o estados de negocio definitivos.
- Implementación de código, manifiestos Kubernetes, recursos CloudNativePG o cambios en `saas-infra`, `saas-clinical` o Identity dentro de PLAT-00.

## Decisiones pendientes

- **Resolución de conexiones y gestión de secretos por tenant × servicio:** definir cómo cada microservicio compartido obtiene de forma segura la conexión y credenciales de su base de datos en la instancia del tenant autorizado, sin exponer secretos en logs ni en Git.
- **Políticas de almacenamiento persistente y dimensionamiento:** establecer `StorageClass`, tamaño de volúmenes persistentes y límites de CPU/RAM por instancia CloudNativePG acordes al laboratorio (nodo K3s actual y futura Surface).
- **Contratos pendientes con Identity y Clinical:** conciliar cómo se coordinan la creación/verificación de bases de datos propias de cada servicio, la ejecución de sus migraciones y el contrato mínimo Platform–Identity (el documento `v0.1` sigue en estado **candidato**).
- **Condiciones definitivas para declarar un tenant `ACTIVA`:** acordar formalmente qué comprobaciones mínimas (infraestructura PostgreSQL, preparación de servicios y cuenta inicial) deben cumplirse antes de considerar una clínica operativa.
- **Políticas de red y acceso entre servicios:** definir restricciones de acceso a las instancias PostgreSQL por servicio y por componente de Platform.
