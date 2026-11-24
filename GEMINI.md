# GEMINI.md — saas-platform

## 1. Contexto y autoridad

Repositorio: `maverick0322/saas-platform`, servicio Platform del proyecto académico **Plataforma SaaS Gestión Hospitalaria**, SaaS multitenant para clínicas (dos desarrolladores, ocho semanas, exclusivamente datos ficticios). Backend confirmado: **.NET 10 / ASP.NET Core**.

**Decisión adoptada:** arquitectura interna **Vertical Slice Architecture** (véase `docs/adr/ADR-0001-vertical-slice-platform.md`). Se organizan funcionalidades por casos de uso, con handlers ligeros y separación de comandos/consultas cuando existan operaciones reales. No imponer MediatR, un bus ni CQRS distribuido. `Domain` contiene únicamente reglas e invariantes reales; no generar capas por plantilla.

Lee antes de editar: `README.md`, `docs/engineering/GIT-CI-CD.md`, `ADR-0001`, `ADR-0002`, `ADR-0003` y el alcance del incremento solicitado. El archivo `docs/contracts/PLATFORM_IDENTITY_FRONTERA_Y_CONTRATO_MINIMO_v0.1_CANDIDATO.md` es **candidato**, no un contrato ratificado ni autorización para implementar sus RPC, esquemas o rutas. Si hay conflicto entre las instrucciones del encargo y un documento candidato, indícalo; no conviertas propuestas en acuerdos.

## 2. Límites del servicio

Platform es dueño del catálogo administrativo global de tenants (en su propia base global independiente de PostgreSQL, según `ADR-0002`), y coordina, cuando se apruebe, el aprovisionamiento de clínicas (mediante un componente trabajador propio de Platform, según `ADR-0003`, sin crear un microservicio independiente) y el alta de la cuenta inicial a través de Identity. **Identity** es dueño de cuentas, contraseñas, sesiones, roles y activación; **Clinical** de pacientes y expedientes. No compartir tablas, `DbContext`, entidades de dominio, claves foráneas ni consultar directamente bases de otros servicios. SuperAdmin no tiene acceso clínico.

Los estados `PENDIENTE_ADMIN`, `ACTIVA` y `SUSPENDIDA` se incorporaron en el catálogo de `Tenant` en **PLAT-02A** (iniciando toda clínica en `PENDIENTE_ADMIN`), manteniendo fuera de alcance la activación efectiva, el trabajador de aprovisionamiento, los clientes/servidores gRPC y las rutas HTTP administrativas hasta sus incrementos aprobados. No inventar requisitos de Billing, BI, Agenda, Document o Prescription.

## 3. Principios de ingeniería obligatorios

- **KISS:** elegir la solución más simple que cumpla el alcance y sea verificable; escribir código idiomático de .NET sin crear infraestructura preventiva.
- **YAGNI:** no crear proyectos, interfaces, capas, servicios, genéricos, brokers, cachés, paquetes o patrones solo por una posible necesidad futura.
- **SOLID, con criterio:** responsabilidades claras; dependencias de abstracciones **solo cuando desacoplan una dependencia real o facilitan una prueba concreta**. No aplicar interfaces por cada clase por ritual.
- **DRY:** eliminar duplicaciones de reglas/conocimiento; una pequeña repetición incidental entre slices puede ser preferible a una abstracción prematura. Centralizar las invariantes cuando se comparten realmente.
- **Alta cohesión / bajo acoplamiento:** agrupar código propio de una funcionalidad dentro de su slice; evitar `Common`, `Utils` o `Services` como vertederos de lógica de negocio.
- **Legibilidad y mantenimiento:** nombres de dominio claros, funciones acotadas, código expresivo, dependencias explícitas por DI, eliminación de código muerto; comentarios para decisiones no evidentes, no para repetir el código.
- **Errores explícitos:** validar entradas en los límites, no ocultar excepciones, usar respuestas HTTP coherentes y no revelar información sensible.
- **Testabilidad:** probar comportamiento observable y reglas reales; evitar pruebas triviales, mocks indiscriminados y dependencia de un clúster para pruebas unitarias.
- **Seguridad:** mínimo privilegio, entrada no confiable, secretos fuera de Git/artefactos/logs, no registrar datos clínicos ni credenciales. No confiar en identificadores UUID como autorización.
- **Recursos:** desarrollar para portátil/laboratorio limitado; no exigir Kubernetes para `dotnet test` ni Docker-in-Docker en la API.

## 4. Convenciones específicas .NET

- Usar capacidades nativas de ASP.NET Core y .NET 10 antes de incorporar paquetes externos; documentar la razón de cada dependencia nueva.
- Para HTTP, usar Minimal APIs o Controllers cuando el incremento lo justifique; **APP-00 utiliza Minimal API únicamente para endpoints técnicos**. Los slices de negocio futuros se decidirán al aprobar su alcance.
- Asincronía para E/S real, `CancellationToken` cuando corresponda; evitar métodos async falsos.
- Preferir logging `ILogger<T>` y health checks integrados; no introducir una pila observabilidad completa ni logs verbosos con datos sensibles.
- Configuración por entorno mediante mecanismos estándar (`appsettings`, variables de entorno y secretos fuera del repositorio). Nunca guardar credenciales reales.
- Persistencia: EF Core + PostgreSQL cuando un incremento autorizado necesite datos; no crear repositorios genéricos encima de `DbContext` por defecto. Platform mantiene su base global independiente y el sistema adopta una instancia PostgreSQL por tenant administrada con CloudNativePG (`ADR-0002`), con esquema y migraciones privadas por servicio y resolución de conexiones fuera del dominio a partir de un contexto de tenant autenticado y autorizado.
- Separar preocupaciones sin añadir cuatro proyectos de Clean Architecture a Platform. Un ensamblado API y proyecto(s) de pruebas son suficientes hasta que un incremento autorizado justifique el componente trabajador de aprovisionamiento dentro de Platform (`ADR-0003`, con permisos Kubernetes separados de la API pública, sin crear un microservicio independiente).
- Docker: imagen multietapa oficial .NET 10, runtime separado de SDK, puerto no privilegiado y ejecución sin root cuando sea compatible; contexto de build limitado con `.dockerignore`.

## 5. Pruebas y definición de terminado

Cada cambio funcional tendrá pruebas proporcionales al riesgo. En el incremento técnico inicial, al menos un test HTTP real en memoria de la ruta de health; `dotnet restore`, `dotnet build`, `dotnet test` y `docker build` deben ser reproducibles (localmente o en CI, declarar si algo no pudo ejecutarse). No crear un proyecto vacío de pruebas únicamente para aparentar cobertura.

Cuando existan funciones de negocio, probar positivos, negativos, permisos, idempotencia y aislamiento de tenants según el contrato aprobado. Usar integración con PostgreSQL real/Testcontainers **solo cuando se introduzca persistencia**.

No declarar `PASS` a partir de inspección estática: registrar comandos, salida resumida y límites de la validación. Diferenciar build, pruebas, publicación GHCR, promoción a `main` y despliegue K3s/Argo CD realmente verificado.

## 6. Flujo Git y seguridad CI/CD

- Ramas: `feature/*` desde `develop` → PR a `develop` → pruebas → PR a `main` (checks requeridos; 0 aprobaciones humanas obligatorias) → publicación GHCR → actualización de versión por digest en `saas-infra` mediante PR → Argo CD → K3s. Tras promover a `main`, sincronizar `develop` mediante merge commit (no `squash`) para preservar ascendencia.
- No hacer push directo a `main` o `develop`, ni force push, merge, cambios de rulesets, publicación, deploy, ni modificar `saas-infra` sin solicitud expresa.
- No alterar `Vital`, `saas-identity` ni otros repositorios.
- Workflows de validación con `permissions: contents: read`, sin secretos ni permisos de escritura en PR. `packages: write` reservado al job de publicación en GHCR desde `main`.
- No poner credenciales, claves de GitHub, tokens, datos ficticios que parezcan reales o información clínica en logs, commits o documentación.
- No introducir acciones de GitHub no verificadas, dependencias arbitrarias ni infraestructura excesiva. Evitar checks requeridos inexistentes.

## 7. Protocolo de trabajo para agentes

1. Inspeccionar estado Git, archivos y reglas existentes; **no sobrescribir** cambios ajenos.
2. Resumir en pocas líneas alcance, archivos previstos y riesgos; identificar qué está confirmado y qué está pendiente.
3. Trabajar solo en la rama `feature/*` del incremento; si no existe, crearla **desde `develop`** sin alterar ramas protegidas. No inventar aprobación del usuario.
4. Implementar exclusivamente lo autorizado; preferir modificaciones pequeñas y comprobables.
5. Ejecutar tests y build; revisar diff, configuración, secretos y necesidad de cada dependencia.
6. Entregar lista de archivos tocados, comandos y resultados, riesgos/puntos pendientes y mensaje de PR propuesto. No afirmar que el PR, CI remoto o despliegue ocurrieron si no se verificaron.
7. Ante decisiones funcionales o interservicios sin ratificación, detener solo esa parte, conservar el trabajo técnico independiente y explicar el bloqueo sin inventar el contrato.

**Prohibido por defecto (sin aprobación explícita del incremento):** funcionalidades médicas o por especialidad, pacientes, datos reales, crear suscripciones/cobros, autenticar usuarios, implementar contratos candidatos no ratificados (p. ej. gRPC candidato), RabbitMQ, Redis, CQRS distribuido o con bases de lectura separadas, event sourcing, Helm, OpenTelemetry/Grafana completos, Terraform, duplicar microservicios por tenant, crear un microservicio independiente de aprovisionamiento o añadir capas/carpetas vacías para aparentar arquitectura.

**Mecanismos autorizados bajo criterio de `ADR-0003` (no prohibidos cuando un incremento aprobado los justifique):** transacciones ACID locales como base obligatoria; patrón **Saga** únicamente cuando una operación distribuida lo justifique (reconociendo expresamente que no ofrece atomicidad ni aislamiento ACID global) y **Transactional Outbox** cuando sea necesario publicar eventos o coordinar trabajo asíncrono de forma consistente con cambios locales. No introducirlos indiscriminadamente ni en operaciones puramente locales.
