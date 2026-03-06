---
applyTo: 'src/be/Mergician/**'
---

- Mergician is designed to work against **any** self-hosted GitLab server in production — not just CI Lab. Do not hardcode anything specific to the CI Lab / localhost instance of GitLab.
- `appsettings.json` ships with **empty** GitLab settings. CI Lab-specific values belong only in `appsettings.Development.json` or environment variables. Do not add CI Lab URLs, ports, or credentials to `appsettings.json`.
