# APP-01 / LAB-06 — Publicación independiente de Platform en GHCR

- **Rama de implementación:** `feature/app-01-ghcr-publication` (integrada en `develop` mediante PR #3).
- **Promoción a `main`:** PR #4 fusionado mediante merge commit (`3c46822c722f889cd715daa6d05beb58e143fb61`).
- **Workflow extendido:** `.github/workflows/ci.yml` (preserva los nombres de checks `Build and test (.NET 10)` y `Docker build verification`).
- **Estado de publicación GHCR:** **`GHCR_PASS`** (imagen publicada y verificada mediante `sudo k3s crictl pull` por digest).

## 1. Diseño del pipeline

### Triggers
- `pull_request` hacia `develop` y `main`.
- `push` hacia `develop` y `main`.
- Concurrencia: `cancel-in-progress: ${{ github.event_name == 'pull_request' }}` para cancelar ejecuciones obsoletas de PR sin interrumpir una publicación en curso en `main`.

### Permisos por job (mínimo privilegio)

| Job | Nombre de check | Condición | Permisos |
|---|---|---|---|
| `build-and-test` | `Build and test (.NET 10)` | Siempre | `contents: read` |
| `docker-build` | `Docker build verification` | `needs: build-and-test` | `contents: read` |
| `publish-ghcr` | `Publish image to GHCR` | `needs: [build-and-test, docker-build]` y `github.event_name == 'push' && github.ref == 'refs/heads/main'` | `packages: write` |

- Ningún PR recibe `packages: write` ni credenciales de publicación.
- `actions/checkout` mantiene `persist-credentials: false`.

### Acciones oficiales fijadas por SHA completo (Node.js 24)
- `actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1` (`v7.0.1`)
- `actions/setup-dotnet@0f4a354dd5547a28c70b7853dbd95d5b67886e73` (`v6.1.0`)
- `actions/upload-artifact@cf430e030ddbb5b0abf93d22962f4752f3646cd9` (`v7.0.2`)
- `actions/download-artifact@9000827ccba6bdab643e8b6fd33ac0654aef8333` (`v8.0.2`)

### Estrategia de etiquetado, identidad del artefacto y metadatos OCI
- **Imagen:** `ghcr.io/maverick0322/saas-platform:${GITHUB_SHA}` (SHA completo del commit; **sin** etiqueta `latest`).
- **Metadatos OCI en `docker build`:**
  - `org.opencontainers.image.source` (`https://github.com/maverick0322/saas-platform`)
  - `org.opencontainers.image.revision` (`${GITHUB_SHA}`)
  - `org.opencontainers.image.title` (`saas-platform`)
  - `org.opencontainers.image.created` (timestamp UTC ISO-8601)
- **Identidad exacta del artefacto:** `docker-build` construye la imagen, ejecuta el smoke test HTTP (`/health/live` y `/health/ready`) sobre esa misma imagen y, solo en `push` a `refs/heads/main`, la exporta con `docker save` como artefacto temporal (`retention-days: 1`). `publish-ghcr` descarga ese artefacto, ejecuta `docker load` y hace `docker push` sin reconstruir la imagen.

### Obtención del digest
Tras `docker push`, `publish-ghcr` consulta `docker inspect --format='{{index .RepoDigests 0}}' "${IMAGE_REF}"` y escribe en `$GITHUB_STEP_SUMMARY`:
- Referencia etiquetada (`ghcr.io/maverick0322/saas-platform:<SHA>`).
- Digest inmutable (`ghcr.io/maverick0322/saas-platform@sha256:...`).
- SHA completo de origen, evento y ref.

### Visibilidad del paquete GHCR y descarga
- El paquete queda asociado al repositorio mediante `org.opencontainers.image.source`.
- La visibilidad (público/privado) se administra desde GitHub Packages del propietario `maverick0322`.
- Descarga:
  - Paquete público: `docker pull ghcr.io/maverick0322/saas-platform@sha256:<DIGEST>` (o con `:<SHA>`).
  - Paquete privado: autenticar primero con `echo "<PAT_READ_PACKAGES>" | docker login ghcr.io -u <USUARIO> --password-stdin` y luego ejecutar `docker pull`.

## 2. Separación CI vs GHCR vs GitOps

| Fase | Responsabilidad | Repositorio / Mecanismo |
|---|---|---|
| **APP-00 / LAB-05 (CI)** | Restauración, build, pruebas, `docker build` y smoke test | `saas-platform` (`.github/workflows/ci.yml`, jobs `build-and-test` y `docker-build`) |
| **APP-01 / LAB-06 (GHCR)** | Publicación de imagen inmutable por SHA + registro de digest | `saas-platform` (job `publish-ghcr`, solo `push` a `main`) |
| **LAB-07 / LAB-08 / LAB-09 (GitOps)** | Manifiestos K3s, sincronización Argo CD y promoción declarativa | `saas-infra` mediante PR (fuera de alcance de APP-01) |

## 3. Ajustes técnicos menores incluidos

- Resolución de los 5 warnings `xUnit1051` en `tests/Saas.Platform.Tests/HealthEndpointsTests.cs` pasando `TestContext.Current.CancellationToken` a `HttpClient.GetAsync` y `HttpContent.ReadAsStringAsync`, manteniendo las 3 pruebas sin cambios funcionales.
- Actualización de acciones de GitHub de Node.js 20 a versiones oficiales compatibles con Node.js 24 fijadas por SHA completo.

## 4. Evidencias y resultados verificados

| Hito / Verificación | Estado | Evidencia |
|---|---|---|
| APP-00 / LAB-05 (PR #2 → `develop`) | `PASS` | `https://github.com/maverick0322/saas-platform/pull/2` (merge `a8fcb5a`, CI de `develop` `44876851817`). |
| APP-01 / LAB-06 en PR #3 → `develop` | `CI_PASS` | `https://github.com/maverick0322/saas-platform/pull/3` (`build-and-test` y `docker-build` en verde; `publish-ghcr` omitido). |
| Promoción `develop` → `main` (PR #4) | `PASS` | Merge commit `3c46822c722f889cd715daa6d05beb58e143fb61` en `main`. |
| Workflow de publicación en `main` | `GHCR_PASS` | `https://github.com/maverick0322/saas-platform/actions/runs/44880025884` — los 3 jobs (`Build and test (.NET 10)`, `Docker build verification`, `Publish image to GHCR`) finalizaron con `success`. |
| Imagen inmutable en GHCR | `GHCR_PASS` | `ghcr.io/maverick0322/saas-platform@sha256:104c651cd22324935c161314a28aeb7fd8728090710ad355b26cd37842b00746` |
| Descarga y verificación en K3s (`containerd`) | `PASS` | `sudo k3s crictl pull` por digest exitoso — ID local `e833cadbe2d3d`, tamaño reportado `104 MB`. |
| Validación local (.NET 9 / Docker detenido) | `FAIL` / `NOT_RUN` | En la estación local (`win-x64`) solo existía SDK `9.0.306` y Docker Desktop detenido; la validación oficial se ejecutó en GitHub Actions y K3s. |
| Despliegue K3s / Argo CD / GitOps | `NEXT_SCOPE` | Siguiente alcance en `saas-infra`: LAB-07 (manifiestos Kubernetes), LAB-08 (Argo CD) y LAB-09 (CD GitOps). |
