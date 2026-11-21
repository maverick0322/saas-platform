# APP-01 / LAB-06 — Publicación independiente de Platform en GHCR

- **Rama de trabajo:** `feature/app-01-ghcr-publication` (creada desde `develop` en `a8fcb5a`, tras el merge del PR #2 y CI verde en `develop`).
- **Workflow extendido:** `.github/workflows/ci.yml` (preserva los nombres de checks `Build and test (.NET 10)` y `Docker build verification`).
- **Estado de publicación GHCR:** `PENDING` (no se ha promovido a `main` ni publicado ninguna imagen).

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

## 4. Resultados y verificaciones pendientes

- **Base verificada:** PR #2 fusionado en `develop` (`a8fcb5a`), CI de `develop` verde (`https://github.com/maverick0322/saas-platform/actions/runs/44876851817`).
- **Validación local:** `dotnet restore` (`FAIL` por SDK local `9.0.306`), `dotnet build`/`test` y `docker build`/`run` (`NOT_RUN` por falta de SDK .NET 10 y daemon Docker detenido).
- **CI remoto del PR a `develop`:** `PENDING` (se completará al abrir el PR de `feature/app-01-ghcr-publication` → `develop`; debe confirmar `CI_PASS` y `publish-ghcr` omitido).
- **Publicación real en GHCR (`GHCR_PASS`):** `PENDING` — se verificará únicamente tras promover `develop` → `main` y comprobar `docker pull` del digest publicado.
