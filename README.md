# saas-platform

Servicio **Platform** del proyecto académico *Plataforma SaaS Gestión Hospitalaria*.

> Laboratorio de ocho semanas y dos integrantes. Solo datos sintéticos. No es un producto para datos clínicos reales.

## Responsabilidad

Platform es dueño del catálogo global de clínicas (tenants), su identificador inmutable, estado administrativo, planes, vigencias y límites declarados. Coordina el alta del administrador inicial **mediante Identity**, sin almacenar credenciales ni manejar cuentas de usuarios.

No gestiona pacientes, expedientes, sesiones, roles de Identity, cobros de pacientes ni KPI de BI.

## Arquitectura adoptada para Platform

- **Vertical Slice Architecture**: organizar las funcionalidades por caso de uso.
- Comandos y consultas mediante handlers ligeros; **sin** imponer un bus, broker, CQRS distribuido ni event sourcing.
- Dominio explícito únicamente para reglas e invariantes reales.
- ASP.NET Core y .NET 10; PostgreSQL privado de Platform cuando se implemente persistencia.
- Una aplicación desplegable y versionada de manera independiente.

Ver [`ADR-0001`](docs/adr/ADR-0001-vertical-slice-platform.md).

## Contrato Platform–Identity

[`Borrador v0.1 — candidato`](docs/contracts/PLATFORM_IDENTITY_FRONTERA_Y_CONTRATO_MINIMO_v0.1_CANDIDATO.md).

**El documento no está ratificado por ambos integrantes.** Los RPC, esquemas, rutas y mecanismos de autenticación propuestos **no están autorizados para implementación definitiva** hasta cerrar D-01 a D-04. La preparación técnica del repositorio y del pipeline es independiente de ese acuerdo.

Estados mínimos del tenant **propuestos en el documento**: `PENDIENTE_ADMIN`, `ACTIVA` y `SUSPENDIDA`. Los errores técnicos del aprovisionamiento no originan por defecto nuevos estados administrativos.

## Organización prevista en APP-00

```text
src/Saas.Platform.Api/
  Features/                  # Slices por funcionalidad (cuando se aprueben)
  Domain/                    # Reglas e invariantes reales
  Infrastructure/            # Persistencia y adaptadores necesarios
  Common/                    # Solo mecanismos técnicos genuinamente comunes
tests/Saas.Platform.UnitTests/
tests/Saas.Platform.IntegrationTests/
```

`src/`, `tests/`, `Dockerfile` y los workflows de CI se crearán en APP-00, con un servicio mínimo funcional; no se incorporan archivos de relleno en REPO-00.

## Flujo de Git y despliegue

`feature/*` → PR a `develop` → validación → PR a `main` → imagen en GHCR → PR de versión en `saas-infra` → Argo CD → K3s.

Fusionar a `main` no demuestra que una versión ya fue desplegada. La verificación de Argo CD y del servicio en K3s es una evidencia posterior.

Ver [convenciones](docs/engineering/GIT-CI-CD.md) y [criterios de REPO-00](docs/REPO-00-CHECKLIST.md).

## Estado

**REPO-00 — bootstrap documental.** Sin lógica de negocio, despliegue, pipeline ejecutado ni integración con Identity. Las evidencias remotas se llenan tras crear ramas y realizar el primer PR.
