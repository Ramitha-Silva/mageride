# C134 · S21 — the container, the catalog entry, the ingress host and the apex redirect

## Identity

Hand-written, not generated — see `build/prompts/C134-www/README.md`.
**Session 21 of 22 · Phase 8 (Deploy), part 1 of 2.**

**Prerequisites:** S03 (the Dockerfile registration) and S20 (the verify chain green).
**Gated on MCS-34 decision D6** — DOKS container vs Cloudflare Pages. This session assumes the
container, which is the recommendation on record: it uses `Dockerfile.portal` unchanged, one deploy
pipeline, one TLS story, ArgoCD-managed. If D6 chose Pages, **stop and re-plan** — this session and
S22 change entirely.

---

## Before you start

- `infra/k8s/service-catalog.yaml` — the `portals:` block at line 459. Three entries; read all three
  and the `why:` field's role in each.
- `infra/k8s/base/ingress/ingress.yaml` — read the **whole header comment**. It explains why there
  are three Ingress objects rather than one, and why the certificates are per-host rather than
  wildcard. Both facts decide this session's shape.
- `infra/k8s/tools/generate_manifests.py` and `check_fences.py`.
- `infra/docker/Dockerfile.portal` — confirm S03's four edits landed.

---

## Do this

### 1 · Confirm the image builds

```
docker build -f infra/docker/Dockerfile.portal --build-arg PORTAL=www --build-arg PORT=3004 -t mageride/www-site:dev .
```

The Dockerfile asserts `.next/standalone/portals/www/server.js` and turns a missing standalone
bundle into a **build-time** error rather than a crash-looping container. If this fails,
`outputFileTracingRoot` in `portals/www/next.config.ts` is wrong (S03).

**Build-host note:** keep the replica stack **down** — `CLAUDE.md`, Build Host. The ~17–20 GB
replica and a Next.js image build do not fit on the 24 GB box together.

### 2 · Add the catalog entry — one edit; everything else is generated

In `infra/k8s/service-catalog.yaml`, after `web-passenger`:

```yaml
  - name: www-site
    portal: www
    port: 3004                 # 3001 admin · 3002 fleet · 3003 web-passenger
    host: www.mageride.lk
    replicas: 2
    autoscale: { min: 2, max: 6 }
    resources:
      requests: { cpu: 100m, memory: 192Mi }
      limits:   { cpu: 500m, memory: 384Mi }
    why: >-
      MCS-34. The public informational site — the platform's fifth surface. Static content only:
      no API dependency at request time, so it stays up when the platform does not. Lower memory
      than the three portals because it holds no session, opens no upstream connection and
      renders no map.
```

Match the surrounding entries' field order and comment style exactly — the file is read as much as
it is parsed.

### 3 · Regenerate the manifests

```
python3 infra/k8s/tools/generate_manifests.py
```

Writes `base/portals/www-site.yaml`, updates `base/portals/kustomization.yaml`, and feeds the
`images.yml` build matrix. **CI runs the same generator with `--check`**, so drift is a red build —
which is exactly why the catalog is the only file you hand-edit.

Read the generated Deployment before committing. The health check the other portals use is Next
answering `/`; confirm `www-site` gets the same and that the probe path is a route that actually
exists (it is `/`, which redirects to the negotiated locale — **verify a redirect satisfies the
probe**, or point the probe at `/en` instead and say why).

### 4 · Ingress — the host, the apex redirect, the certificate

Two edits in `infra/k8s/base/ingress/ingress.yaml`:

**(a) `mr-ingress-portals` gains a host.** Add `www.mageride.lk` → `www-site:80` to `rules`, and
`- hosts: [www.mageride.lk] / secretName: mr-tls-www` to `tls`.

The file's existing comment says the certificates are per-host rather than wildcard because
`passenger.mageride.lk` is unauthenticated public traffic and the two portals are back-office —
*"a single certificate and key shared across all three means the key that serves the public page is
the key that serves the admin console."* This host is also unauthenticated public traffic, so it
follows the `passenger.` precedent: **its own secret.**

**(b) A separate, fourth Ingress object for the apex.** `mageride.lk` carrying

```yaml
    nginx.ingress.kubernetes.io/permanent-redirect: https://www.mageride.lk$request_uri
```

**Separate, because ingress-nginx annotations are object-scoped** — which is precisely why that file
already holds three objects rather than one, and the header comment says so. A redirect annotation
on `mr-ingress-portals` would 301 the admin and fleet portals to the marketing site.

Give it the same `sync-wave: "3"`, `ssl-redirect`, HSTS and `ingressClassName: nginx` as its
siblings, its own `mr-tls-apex` secret, and a comment naming MCS-34 and why it is its own object.

### 5 · Cache headers — a marketing site's whole advantage

In `portals/www/next.config.ts`'s `headers()`:

- immutable hashed assets (`/_next/static/*`, `/screens/*`) → `public, max-age=31536000, immutable`
- HTML → `public, s-maxage=300, stale-while-revalidate=86400`

`test/seo.test.ts` (S20) asserts them, so add the assertions this session if S20 left them pending.

`stale-while-revalidate` is what keeps the site up while the cluster is not — which is the point of
the "renders with the backend down" fence extended one layer out.

### 6 · Check the fences

```
python3 infra/k8s/tools/generate_manifests.py --check
python3 infra/k8s/tools/check_fences.py
```

Both clean before you finish. If `check_fences.py` has an opinion about a new host or a new object,
read it rather than working around it.

---

## Fences

- **Hand-edit `service-catalog.yaml` only.** Everything under `infra/k8s/base/portals/` is generated.
- **The apex redirect is its own Ingress object.** Never an annotation on the shared portals object.
- **`www.mageride.lk` gets its own TLS secret.** No wildcard, no sharing with the admin console.
- **No `MAGERIDE_API_BASE_URL`** and no gateway environment variable on this container. It has no
  upstream, and giving it one would be the first step to breaking the fence.
- **Do not bring the replica up** while building images on this host.

---

## Verify

```
docker build -f infra/docker/Dockerfile.portal --build-arg PORTAL=www --build-arg PORT=3004 -t mageride/www-site:dev .
docker run --rm -p 3004:3004 mageride/www-site:dev &
curl -sI localhost:3004/ | head -3            # 200 or a redirect to the negotiated locale
curl -sI localhost:3004/en | head -3          # 200
curl -sI localhost:3004/_next/static/... | grep -i cache-control   # immutable
python3 infra/k8s/tools/generate_manifests.py --check
python3 infra/k8s/tools/check_fences.py
git status --porcelain infra/k8s/base/portals/    # generated files, expected
```

---

## Handoff

- **Component:** C134 www-informational-site — S21 (container & ingress) — <date>
- **Status:** DONE | PARTIAL
- **Notes:** the image size; the probe path chosen and why; the generated manifest diff; anything
  `check_fences.py` objected to.
