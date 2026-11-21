# REPO-00 — Criterios de aceptación

**Estado:** controles de repositorio, ramas, rulesets y PR #1 verificados; permisos del segundo integrante pendientes de comprobación.

## Repositorio

- [x] `maverick0322/saas-platform` existe en GitHub con visibilidad elegida conscientemente (`PUBLIC`).
- [x] `main` está configurada como rama predeterminada.
- [x] `develop` existe y se ha publicado (`origin/develop`).
- [x] Existe al menos una rama `feature/*` de prueba creada desde `develop` (`feature/repo-00-validation`, fusionada en PR #1).
- [ ] El otro integrante tiene los permisos apropiados para colaborar *(pendiente de verificación explícita)*.

## Ingeniería

- [x] ADR de Vertical Slice redactado con estado de adopción individual.
- [x] Convenciones de Git, CI y GitOps documentadas.
- [x] `.editorconfig`, `.gitignore`, `.gitattributes` y plantilla de PR presentes.
- [x] Contrato Platform–Identity v0.1 archivado **como candidato**, sin elevar su estado.
- [x] Reglas de protección de ramas configuradas según visibilidad/plan (`REPO-00 protect main` y `REPO-00 protect develop`).
- [x] PR de prueba a `develop` creado y fusionado (`#1`).

## No se exige en REPO-00

- API funcional, Dockerfile, pipeline CI, GHCR o K3s: se validan en APP-00/LAB-05 (CI), APP-01/LAB-06 (GHCR), LAB-07 (Kubernetes), LAB-08 (Argo CD) y LAB-09 (CD mediante GitOps).
- Contratos gRPC y endpoints definitivos con Identity: pendientes de conciliación D-01 a D-04.

## Evidencias

- URL del repositorio: `https://github.com/maverick0322/saas-platform`
- SHA del commit inicial (`main` / `develop` inicial): `4d5b53c778c1b58184464301b65c19b8f1fbde5d`
- URL del primer PR: `https://github.com/maverick0322/saas-platform/pull/1` (fusionado en `origin/develop` mediante `b98804ffb00dbd689e7f6574c4c8879119789640`)
- Configuración de reglas de ramas: rulesets `REPO-00 protect main` (PR + 1 aprobación, bloqueo de force-push y borrado) y `REPO-00 protect develop` (PR sin aprobación obligatoria, bloqueo de force-push y borrado); ver `docs/REPO-00-REMOTE-VALIDATION.md`.
