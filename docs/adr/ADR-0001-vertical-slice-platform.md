# ADR-0001 — Vertical Slice Architecture para Platform

- **Fecha:** 2026-11-19
- **Estado:** adoptada por el responsable de Platform; comunicar y conciliar con el segundo integrante
- **Ámbito:** arquitectura interna de `saas-platform`, no contratos entre microservicios

## Contexto

Platform administra el catálogo global de tenants, planes y vigencias, y coordina el proceso de alta de administrador inicial a través de Identity. Sus funcionalidades tienen complejidad desigual. El proyecto dispone de dos desarrolladores y ocho semanas, y requiere independencia de compilación, pruebas y despliegue.

## Decisión

Usar **Vertical Slice Architecture** como estilo interno: agrupar endpoints, handlers, validaciones y acceso a datos específico por funcionalidad, sin exigir una cadena de servicios/repositorios idéntica para cada operación.

- Separar comandos y consultas mediante handlers simples con inyección de dependencias; no requerir MediatR.
- Mantener un `Domain` mínimo para reglas e invariantes efectivas (p. ej., transiciones de estado ratificadas).
- Usar EF Core y PostgreSQL como persistencia privada de Platform cuando se implemente; no introducir un repositorio genérico redundante.
- Mantener adaptadores de comunicaciones externas fuera de las reglas de dominio.
- Un único servicio ASP.NET Core desplegable; múltiples proyectos .NET solo si existe una necesidad comprobada.

## Alternativas consideradas

- Controller–Service–Repository: válida, pero mayor riesgo de servicios horizontales extensos y abstracciones de persistencia redundantes.
- Clean Architecture: válida para dominios más ricos, pero sus separaciones físicas obligatorias no se justifican inicialmente en Platform.
- Hexagonal: válida, no requerida como arquitectura adicional a VSA.
- CQRS con modelos o bases de lectura separados: costo adicional sin necesidad demostrada.

## Consecuencias y controles

- **Beneficio:** cambios y pruebas localizados por funcionalidad.
- **Riesgo:** duplicación de invariantes entre slices; las reglas compartidas se centralizan cuando aparezcan.
- **Riesgo:** `Common` o `Infrastructure` se conviertan en contenedores indiscriminados; toda extracción debe tener una justificación concreta.
- **Control:** PR, pruebas de invariantes, revisiones de seguridad, documentación de decisiones.

## No decidido por este ADR

Mecanismo final de aprovisionamiento, topología tenant × servicio × BD, credenciales de migración, esquema de mensajes, RabbitMQ/Redis/Outbox, forma definitiva de los RPC con Identity, resolución de tenant antes del login y política de revocación de sesiones.

La frontera con Identity se conserva en un **contrato candidato v0.1**, que requiere conciliación explícita entre integrantes antes de implementar la integración.

## Referencias cruzadas posteriores

Sin alterar el contenido histórico de este ADR, varias cuestiones listadas en *No decidido por este ADR* fueron formalizadas posteriormente en:

- [`ADR-0002 — Persistencia multitenant y topología PostgreSQL`](ADR-0002-multitenant-persistence.md)
- [`ADR-0003 — Consistencia transaccional y aprovisionamiento de tenants`](ADR-0003-transactional-consistency-and-provisioning.md)
