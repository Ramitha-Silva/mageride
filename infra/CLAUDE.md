# Infra Conventions
- Docker Compose (dev + lightweight production replica), Kubernetes manifests, bash helper
  scripts. No application code lives here.
- Base images (D7' §2.1/§2.2 + Δ 2026-07-23): `aspnet:10.0-alpine` for services,
  `runtime:10.0-alpine` for the tcp-adapter workers, `node:24-alpine` for the portals
- Every service container runs as a non-root user and exposes `/health/live` + `/health/ready`
  with the D7' §5.1 healthcheck
- Env files are templates only: `.env*` is gitignored, `*.env.example` / `.env.*.example` are
  committed with placeholders. Never commit a secret — secrets are pgcrypto + Vault (D7' §13)
- Three environments, never mixed: dev compose (C009) → lightweight production replica on a
  single Contabo VPS with synthetic data, for testing/CI/demos only (C125) → production on
  DigitalOcean Kubernetes, Singapore
- Keep the replica stack DOWN during waves 0–4 — this box hosts both and ~17–20 GB of replica
  will not fit alongside a build (see the root CLAUDE.md "Build Host" note)
- MQTT (8883) is TCP passthrough at HAProxy and is never routed through the API gateway
- Verify: `docker compose -f infra/docker-compose.dev.slim.yml config`
