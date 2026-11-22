# ADR-0003 — Consistencia transaccional y aprovisionamiento de tenants

- **Fecha:** 2026-11-22
- **Estado:** aprobada (formalización arquitectónica previa a PLAT-01)
- **Ámbito:** modelo de consistencia transaccional y dirección de diseño para el aprovisionamiento de tenants coordinado por `saas-platform`
- **Referencias relacionadas:** [`ADR-0001`](ADR-0001-vertical-slice-platform.md), [`ADR-0002`](ADR-0002-multitenant-persistence.md), [`PLATFORM_IDENTITY_FRONTERA_Y_CONTRATO_MINIMO_v0.1_CANDIDATO.md`](../contracts/PLATFORM_IDENTITY_FRONTERA_Y_CONTRATO_MINIMO_v0.1_CANDIDATO.md) (borrador candidato no ratificado)

## Contexto

La adopción de una instancia PostgreSQL independiente por tenant administrada con CloudNativePG ([`ADR-0002`](ADR-0002-multitenant-persistence.md)) implica que el alta de una nueva clínica involucra múltiples pasos y límites de sistema: persistencia del catálogo administrativo en la base global de Platform, creación/verificación de recursos PostgreSQL en Kubernetes mediante CloudNativePG y coordinación con otros servicios (como Identity y, cuando corresponda, Clinical) conforme a los contratos que se ratifiquen.

Adicionalmente, el SuperAdmin debe poder registrar y aprovisionar una clínica **sin ejecutar manualmente comandos de Kubernetes o PostgreSQL**, manteniendo el principio de mínimo privilegio, la separación con GitOps (Argo CD) y garantías claras sobre qué operaciones son atómicas y cuáles requieren coordinación distribuida.

Hasta ahora, las reglas preventivas del repositorio excluían por defecto mecanismos como Saga o Outbox para evitar sobreingeniería prematura en las fases base (APP-00/APP-01). Es necesario formalizar criterios explícitos que autoricen su uso únicamente cuando la naturaleza distribuida de una operación lo justifique, manteniendo excluidas las tecnologías no aprobadas.

## Decisión

### A. Aprovisionamiento de tenants

1. **Coordinación bajo responsabilidad de Platform:** Platform coordina el alta administrativa y el aprovisionamiento de nuevas clínicas, permitiendo al SuperAdmin operar sin ejecutar manualmente comandos de Kubernetes o PostgreSQL.
2. **Administración PostgreSQL mediante CloudNativePG:** las instancias PostgreSQL por tenant y sus recursos PostgreSQL asociados se administran mediante CloudNativePG en Kubernetes.
3. **Ejecución privilegiada en un componente trabajador de Platform:** las operaciones privilegiadas de aprovisionamiento (interacción con la API de Kubernetes / recursos CloudNativePG y tareas largas o reintentables) se ejecutarán **fuera del proceso de la API pública**, mediante un **componente trabajador perteneciente a Platform**.
4. **Sin microservicio independiente de aprovisionamiento:** no se crea un microservicio separado para aprovisionar; el componente trabajador permanece bajo el límite, propiedad y repositorio/servicio de Platform.
5. **Separación de permisos Kubernetes:** la identidad de ejecución (ServiceAccount / RBAC) de la **API pública** de Platform y la del **componente trabajador** deben mantenerse estrictamente separadas. La API pública no recibe permisos privilegiados de Kubernetes ni de creación de recursos CloudNativePG.
6. **Idempotencia, registro durable y recuperación:** toda operación de aprovisionamiento debe ser **idempotente**, mantener un **registro durable de operaciones** y soportar **recuperación controlada ante fallos** (reintentos seguros sin duplicar recursos ni dejar estados inconsistentes no detectables).
7. **Frontera estricta con GitOps (Argo CD):** no se permite que **Argo CD** y el **aprovisionador** sean propietarios simultáneos de los mismos recursos dinámicos de tenants. La infraestructura declarativa estática (aplicaciones, operadores, configuración base del clúster) se gestiona vía GitOps en `saas-infra`; los recursos dinámicos creados por el aprovisionador para cada tenant quedan fuera de la reconciliación declarativa de Argo CD.
8. **Suspensión no destructiva:** al suspender una clínica **no se borran automáticamente** sus datos ni su instancia PostgreSQL.

### B. Consistencia transaccional

1. **Transacciones ACID locales:** toda operación local dentro de un mismo límite de base de datos que requiera atomicidad debe utilizar **transacciones ACID reales** y **restricciones de integridad** de PostgreSQL (claves únicas, restricciones, etc.).
2. **Contrato explícito para operaciones distribuidas:** toda operación que cruce límites de base de datos, servicios o infraestructura (Kubernetes/CloudNativePG) debe definir explícitamente sus **invariantes**, **semántica de éxito**, **manejo de fallos**, **estrategia de recuperación** e **idempotencia**.
3. **Uso justificado de Saga:** se autoriza utilizar el patrón **Saga** cuando una operación distribuida lo justifique (por ejemplo, coordinación multi-paso del alta/aprovisionamiento de una clínica). No debe incorporarse indiscriminadamente en operaciones que puedan resolverse localmente.
4. **Reconocimiento explícito de los límites de Saga:** se deja constancia expresa de que **Saga no proporciona atomicidad ACID global ni garantiza aislamiento global**. Los estados intermedios son observables y deben modelarse con salvaguardas (idempotencia, bloqueos lógicos o invariantes de acceso) para evitar efectos indebidos antes de completar la operación.
5. **Revisión ante requisitos de atomicidad global estricta:** si una operación futura pareciera exigir atomicidad global estricta entre sistemas independientes, deben revisarse sus límites transaccionales y evaluar alternativas apropiadas antes de implementarla.
6. **Consideración de Transactional Outbox:** se considera **Transactional Outbox** cuando sea necesario publicar eventos o encolar/disparar trabajo asíncrono de manera consistente con cambios locales en la base de datos, evitando la pérdida de mensajes o la publicación de cambios no confirmados.
7. **Tecnologías no adoptadas sin aprobación:** no se adopta **RabbitMQ**, **CQRS distribuido** ni **event sourcing** sin una necesidad explícitamente aprobada.

## Justificación

- **Mínimo privilegio y seguridad:** separar el componente trabajador de la API pública evita exponer permisos privilegiados de Kubernetes al plano de entrada HTTP, acotando el impacto de cualquier fallo o vulnerabilidad en la API pública.
- **Cohesión sin fragmentación:** mantener el trabajador dentro de Platform evita introducir un nuevo microservicio, otro ciclo de despliegue independiente y nuevas fronteras de red innecesarias para un equipo de dos personas.
- **Evitar conflictos de propiedad con Argo CD:** si Argo CD y un aprovisionador dinámico intentan reconciliar los mismos manifiestos/recursos de tenants, se producen bucles de drift o eliminación inadvertida de instancias activas.
- **Realismo transaccional:** las operaciones que combinan PostgreSQL, Kubernetes/CloudNativePG y otros microservicios no admiten transacciones ACID globales prácticas. Declarar explícitamente el uso selectivo de Saga (con sus límites reales) y la opción de Transactional Outbox evita tanto la parálisis por prohibiciones genéricas como la adopción innecesaria de brokers o patrones pesados.
- **Autoservicio controlado para el SuperAdmin:** el flujo durable e idempotente elimina la dependencia de comandos manuales `kubectl`/`psql` y hace recuperables los fallos parciales (timeouts, indisponibilidad temporal de servicios o del clúster).

## Alternativas consideradas

1. **Aprovisionamiento manual por CLI (`kubectl` / `psql`):**
   - *Motivo de descarte:* incumple el objetivo de que el SuperAdmin registre y aprovisione clínicas sin intervención manual sobre Kubernetes o PostgreSQL, y carece de trazabilidad e idempotencia integradas.
2. **Ejecutar el aprovisionamiento directamente dentro del proceso de la API pública de Platform:**
   - *Motivo de descarte:* obliga a otorgar permisos privilegiados de Kubernetes a la API expuesta y acopla peticiones HTTP síncronas a tiempos variables de creación de recursos PostgreSQL.
3. **Crear un microservicio independiente de aprovisionamiento (`saas-provisioning`):**
   - *Motivo de descarte:* añade sobrecarga de repositorio/pipeline/contratos sin justificación suficiente en el alcance académico de ocho semanas y dos desarrolladores.
4. **Gestionar el alta de cada tenant mediante PRs en `saas-infra` + Argo CD:**
   - *Motivo de descarte:* impide el alta autoservicio desde Platform y mezcla el ciclo de vida dinámico de datos de negocio (tenants) con la infraestructura declarativa estática administrada por GitOps.
5. **Transacciones distribuidas 2PC / XA:**
   - *Motivo de descarte:* impracticables entre APIs heterogéneas (PostgreSQL, Kubernetes/CNPG, gRPC/HTTP entre servicios), con alto acoplamiento temporal y riesgo de bloqueos.
6. **Adoptar RabbitMQ, CQRS distribuido y Event Sourcing desde el inicio:**
   - *Motivo de descarte:* complejidad operativa y consumo de recursos injustificados en el laboratorio actual sin un requisito aprobado que los exija.

## Consecuencias

### Positivas

- La API pública de Platform mantiene permisos mínimos y delega el trabajo privilegiado al componente trabajador de Platform.
- Las operaciones locales se benefician de atomicidad ACID real sin complejidad distribuida artificial.
- Las operaciones distribuidas cuentan con criterios claros de diseño (idempotencia, registro durable, recuperación, uso acotado de Saga y consideración de Transactional Outbox).
- Se elimina la ambigüedad entre infraestructura estática (GitOps / Argo CD) y recursos dinámicos de tenants.

### Negativas y de costo operativo

- Se requiere diseñar y operar un componente trabajador adicional dentro de Platform (con su propia configuración de identidad y permisos RBAC en Kubernetes cuando se implemente).
- El equipo debe modelar explícitamente estados de progreso/recuperación e invariantes ante la ausencia de aislamiento global en operaciones Saga.
- La validación de flujos de aprovisionamiento requerirá pruebas específicas de idempotencia, reintentos y recuperación ante fallos parciales.

## Riesgos

- **Compatibilidad CloudNativePG con K3s/Kubernetes 1.37.2:** Kubernetes `1.37.2` figura como versión probada por upstream, pero **no está oficialmente soportada para CloudNativePG 1.30**; además, la **validación local de CloudNativePG en nuestro K3s sigue pendiente** (no se afirma que la integración funcione todavía en el laboratorio) y fallos en el operador o en la conciliación de CRDs pueden afectar el flujo de aprovisionamiento.
- **Recursos limitados del laboratorio:** el nodo K3s actual (y la futura incorporación de Surface) impone tiempos de creación variables y restricciones de memoria/disco que pueden provocar timeouts o fallos transitorios durante el aprovisionamiento.
- **Políticas de almacenamiento, credenciales, permisos y acceso entre servicios:** errores en RBAC de Kubernetes (API pública vs trabajador), generación/entrega de credenciales o políticas de almacenamiento persistente pueden comprometer seguridad o impedir la recuperación.
- **Propiedad compartida accidental con Argo CD:** una mala separación de namespaces, etiquetas o manifiestos podría provocar que Argo CD intente sincronizar o podar recursos dinámicos creados por el aprovisionador.
- **Falsa sensación de atomicidad en Sagas:** asumir aislamiento o atomicidad global donde no existe podría permitir que un tenant sea usado antes de completar su aprovisionamiento o generar duplicados ante reintentos concurrentes.
- **Contratos pendientes con Identity y Clinical:** el contrato candidato Platform–Identity (`v0.1`) aún no está ratificado y los contratos con Clinical siguen pendientes; implementar integraciones sin cerrarlos introduciría retrabajo.
- **Condiciones definitivas para declarar un tenant `ACTIVA`:** todavía están pendientes de conciliación entre integrantes y condicionan el criterio exacto de finalización del aprovisionamiento.

## Garantías

- **Integridad local:** toda transición o actualización local en base de datos se protege con transacciones ACID reales y restricciones de integridad.
- **Separación de privilegios:** la API pública de Platform nunca requiere permisos privilegiados de Kubernetes para aprovisionar instancias PostgreSQL.
- **Idempotencia y trazabilidad:** las operaciones de aprovisionamiento conservan un registro durable y pueden reintentarse sin duplicar recursos ni perder estado de progreso.
- **Conservación de datos al suspender:** suspender una clínica no borra automáticamente datos ni instancias PostgreSQL.
- **Propiedad única de recursos:** los recursos estáticos pertenecen exclusivamente a GitOps (Argo CD) y los recursos dinámicos de tenants pertenecen exclusivamente al flujo de aprovisionamiento.

## Exclusiones

Queda explícitamente fuera del alcance de esta decisión y del MVP:

- Creación de un microservicio independiente de aprovisionamiento.
- Adopción de RabbitMQ, CQRS distribuido o event sourcing sin necesidad aprobada.
- Borrado automático de datos o instancias PostgreSQL al suspender clínicas.
- Migraciones masivas de esquemas existentes, traslado automatizado de tenants entre instancias y alta disponibilidad (HA) de PostgreSQL en el MVP.
- Definición definitiva de estados de negocio, rutas HTTP, RPC o esquemas de base de datos.
- Implementación de código, proyectos .NET adicionales, manifiestos Kubernetes, RBAC, workflows o recursos CloudNativePG en PLAT-00.

## Decisiones pendientes

- **Mecanismo concreto de disparo y coordinación API ↔ trabajador de Platform:** decidir cómo la API pública persiste la intención de aprovisionamiento y cómo el trabajador la detecta y ejecuta (por ejemplo, tabla durable/outbox en PostgreSQL de Platform), sin introducir brokers no aprobados.
- **Diseño exacto de RBAC de Kubernetes y alcance del trabajador:** definir los permisos mínimos estrictamente necesarios sobre recursos CloudNativePG y secretos, así como los límites de namespace.
- **Condiciones definitivas para declarar un tenant `ACTIVA`:** conciliar qué pasos obligatorios (instancia PostgreSQL lista, bases/migraciones de servicios, habilitación inicial en Identity) completan el alta operativa.
- **Contratos pendientes con Identity y Clinical:** ratificar el contrato Platform–Identity (actualmente `v0.1` candidato) y acordar la interacción necesaria con Clinical para la preparación de su base por tenant.
- **Estrategia de compensación vs recuperación hacia adelante:** fijar, una vez ratificados los contratos, qué pasos del alta se recuperan mediante reintento hacia adelante y cuáles requieren reversión controlada ante fallos irrecuperables.
