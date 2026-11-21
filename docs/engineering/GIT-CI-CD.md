# Convenciones de repositorio y entregas — Platform

## Ramas (acuerdo del proyecto)

- `main`: versión promovida y candidata al despliegue en el K3s de demostración.
- `develop`: integración y validación previa a la promoción.
- `feature/*`: ramas cortas creadas desde `develop`.

Flujo: `feature/*` → PR → `develop` → pruebas → PR → `main` → GHCR → actualización declarativa en `saas-infra` → Argo CD → K3s.

Los cambios de configuración GitOps viven en `saas-infra`, **no** en el repositorio de la aplicación. Publicar una imagen no equivale a desplegarla.

## Reglas actuales de ramas y protección (verificadas en GitHub)

Ambas ramas protegidas cuentan con rulesets activos (`REPO-00 protect main` y `REPO-00 protect develop`):

- Cambios únicamente mediante Pull Request; prohibido `force push` y borrado de ramas.
- Métodos de fusión permitidos: `merge` y `squash`.
- **Aprobaciones humanas obligatorias:** **0** en ambas ramas (`required_approving_review_count: 0`).
- **Checks obligatorios (`strict_required_status_checks_policy: true`):**
  - `Build and test (.NET 10)`
  - `Docker build verification`
- **Sincronización posterior a promociones (`main` → `develop`):** después de promover `develop` → `main` mediante merge commit, sincronizar `develop` incorporando `origin/main` mediante **merge commit** (no `squash`) para preservar la ascendencia de `main` y evitar divergencias futuras.
- Commits preferidos: `feat:`, `fix:`, `docs:`, `test:`, `ci:`, `chore:`, `refactor:`. Es convención propuesta, no condición técnica del pipeline.
- **Referencia de despliegue inmutable:** etiquetar por SHA completo y desplegar por digest (`ghcr.io/maverick0322/saas-platform@sha256:...`); no usar `latest` como referencia desplegable.

## Verificaciones CI/CD implementadas (APP-00 / LAB-05 y APP-01 / LAB-06)

En PR a `develop`/`main` y `push` a `develop`: restauración de paquetes, compilación, pruebas, construcción de contenedor con metadatos OCI y smoke test HTTP (sin publicar en GHCR), con permisos mínimos (`contents: read`).

Tras promoción a `main` (`push` a `refs/heads/main`): publicación en GHCR de la misma imagen validada mediante artefacto temporal, etiquetada con `${GITHUB_SHA}` y registrando el digest inmutable en `$GITHUB_STEP_SUMMARY`. La versión desplegada en K3s se actualiza en `saas-infra` mediante PR con referencia por digest (`@sha256:...`); Argo CD sincroniza el cambio.

Pruebas de integración con PostgreSQL (Testcontainers) se incorporarán cuando exista código de persistencia; no desplegar un Kubernetes adicional para ejecutar CI.

## Seguridad

- No almacenar secretos en Git, workflow, variables de entorno compartidas ni artefactos de CI.
- No entregar tokens amplios a workflows de PR, especialmente de forks.
- GHCR: privilegio `packages: write` solo en el trabajo de publicación; demás jobs con permisos mínimos.
- No registrar credenciales, datos personales ni datos clínicos reales en logs.
- No otorgar acceso DDL global a la identidad de ejecución ordinaria de Platform.

## Evidencias a conservar

URL y SHA del PR, checks de CI, digest de imagen GHCR, commit de GitOps, estado de sincronización de Argo CD, estado Ready y respuesta al health check. Separar evidencia local, CI, publicación y despliegue.
