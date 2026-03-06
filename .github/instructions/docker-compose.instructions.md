---
applyTo: 'Dockerfile.**'
---

- Pin third-party images and versions in docker compose files to ensure reproducibility.
- `mergician-compose.yaml` uses `name: mergician` to avoid container name conflicts with `cilab-compose.yaml` (project name `ci-lab`). Do not remove this.
