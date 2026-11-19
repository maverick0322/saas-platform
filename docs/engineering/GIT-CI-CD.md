# Convenciones de repositorio y entregas — Platform

## Ramas (acuerdo del proyecto)

- `main`: versión promovida y candidata al despliegue en el K3s de demostración.
- `develop`: integración y validación previa a la promoción.
- `feature/*`: ramas cortas creadas desde `develop`.

Flujo: `feature/*` → PR → `develop` → pruebas → PR → `main` → GHCR → actualización declarativa en `saas-infra` → Argo CD → K3s.

Los cambios de configuración GitOps viven en `saas-infra`, **no** en el repositorio de la aplicación. Publicar una imagen no equivale a desplegarla.

## Estándares iniciales recomendados (a confirmar en la configuración de GitHub)

- PR para cambios de `develop` y `main`; no forzar pushes ni borrar estas ramas.
- Al menos una revisión de otra persona al promover a `main` si el plan/visibilidad permite exigirlo.
- En `develop` priorizar PR y CI sin imponer revisión obligatoria en cada pequeño cambio.
- Requerir checks *únicamente cuando existan y sus nombres estén estabilizados*; no bloquear con un check inexistente.
- Commits preferidos: `feat:`, `fix:`, `docs:`, `test:`, `ci:`, `chore:`, `refactor:`. Es convención propuesta, no condición técnica del pipeline.
- Etiquetas de versión e imágenes independientes de otros servicios; no publicar `latest` como única referencia desplegable.

## Fase APP-00: verificaciones que sí deberán existir

En PR a `develop`/`main`: restauración de paquetes, compilación, pruebas, construcción de contenedor (sin publicarlo), permisos mínimos de workflow.

Tras promoción a `main`: publicación GHCR con referencia de commit y/o etiqueta de versión inmutable. La versión desplegada en K3s se cambia mediante PR en `saas-infra`; Argo CD sincroniza el cambio.

Pruebas de integración con PostgreSQL (Testcontainers) se incorporarán cuando exista código de persistencia; no desplegar un Kubernetes adicional para ejecutar CI.

## Seguridad

- No almacenar secretos en Git, workflow, variables de entorno compartidas ni artefactos de CI.
- No entregar tokens amplios a workflows de PR, especialmente de forks.
- GHCR: privilegio `packages: write` solo en el trabajo de publicación; demás jobs con permisos mínimos.
- No registrar credenciales, datos personales ni datos clínicos reales en logs.
- No otorgar acceso DDL global a la identidad de ejecución ordinaria de Platform.

## Evidencias a conservar

URL y SHA del PR, checks de CI, digest de imagen GHCR, commit de GitOps, estado de sincronización de Argo CD, estado Ready y respuesta al health check. Separar evidencia local, CI, publicación y despliegue.
