# C134 · S22 — the replica service, the smoke checks, and the go-live rows

## Identity

Hand-written, not generated — see `build/prompts/C134-www/README.md`.
**Session 22 of 22 · Phase 8 (Deploy), part 2 of 2 — the last session.**

**Prerequisite:** S21.

At the end of this session C134's Definition of Done is met and `build/progress.md`'s C134 row moves
to `DONE`.

---

## Know this before you start (README §4.5)

`docs/www-site-plan.md` §A45 says to add `www-site` "under the existing `portals` profile" — the
profile exists, but **`web-passenger` is not in it**. `infra/replica/docker-compose.light-replica.yml`
carries `admin-portal` (line 633) and `fleet-portal` (line 664) and nothing else. There is no
passenger-web precedent to copy.

So copy **`admin-portal`'s** shape, and remove what does not apply.

---

## Do this

### 1 · `infra/replica/docker-compose.light-replica.yml`

```yaml
  # Optional — www-site (www.mageride.lk, MCS-34 / C134).        384 MB / 0.25 vCPU
  www-site:
    <<: *restart
    profiles: [portals]
    build:
      context: ../..
      dockerfile: infra/docker/Dockerfile.portal
      args:
        PORTAL: www
        PORT: "3004"
    image: ${REPLICA_REGISTRY:-mageride}/www-site:${REPLICA_TAG:-replica}
    # No MAGERIDE_API_BASE_URL, and its absence is the point: this surface makes no request at
    # render time (C134 fence 2), so it has no gateway to name. The two portals above both
    # REQUIRE that variable and answer 503 without it; this one would have nothing to do with it.
    #
    # No `depends_on: app-services` either, for the same reason — it is the one container in this
    # file that is healthy while the platform is down, which is exactly what it is for.
    deploy:
      resources:
        limits: { memory: 384M, cpus: "0.25" }
    networks: [internal]
```

Write that comment, or something equivalent. Every other service in that file explains itself, and
the absence of a variable is the kind of thing a later reader "fixes".

### 2 · `infra/replica/haproxy.replica.cfg`

Follow the existing pattern (ACLs at lines 107–113, backends at 144–151):

```
    acl host_www hdr(host) -i -m beg www.
    ...
    use_backend www_site if host_www
...
backend www_site
    server www www-site:3004 check resolvers docker init-addr none
```

**The apex.** In DOKS the `mageride.lk` → `www` 301 is a separate Ingress object (S21). HAProxy has
no equivalent object model, so it is an explicit rule — add one matching the bare apex and issuing
`http-request redirect location https://www.mageride.lk%[capture.req.uri] code 301`. Place it so it
cannot catch `admin.`, `fleet.` or `passenger.`; test that it does not.

### 3 · `infra/replica/smoke.sh`

Currently 24 checks. Add, following the file's existing `ok`/`bad`/`skip_` helpers and its rule that
**every request goes through the edge** (HAProxy on 443 with the self-signed certificate) rather
than the container directly:

1. `/` returns 200 in every rendered locale (`/si`, `/en`, and `/ta` if it ships).
2. `/sitemap.xml` parses as XML and lists every route in the table.
3. A guide chapter returns 200 **and its HTML contains a `HowTo` JSON-LD block**. Pick a fixed
   chapter, not a random one — a flaky smoke check gets ignored.
4. The apex 301s to `www` and the `Location` header is exact.
5. **The site answers 200 with `app-services` stopped.** This is the check that proves C134's second
   fence at the deployment level rather than in a unit test, and it is the most valuable one on the
   list. Guard it so it restores the stopped service afterwards.

Follow the file's own posture on what a smoke check proves: it asserts a *deployment* is reachable
and coherent, not that the code is correct.

### 4 · `docs/production/go-live-checklist.md`

DNS and certificate issuance are **outside the repo** — registrar / Cloudflare work — which is
exactly why they belong on that checklist rather than in a script. Add rows in the file's existing
table format, with a state, an owner and a gate:

- `www.mageride.lk` A/AAAA → the ingress load balancer — **OPEN** — infrastructure owner — before
  the site is announced.
- `mageride.lk` apex A/AAAA (or ALIAS) → the same, so the 301 has something to fire from — **OPEN**.
- `mr-tls-www` and `mr-tls-apex` issued and renewing — **OPEN** — with #10 (Vault/secrets).

Read the file's §"A note on ownership" first and match how ownership is expressed there.

### 5 · Close out C134

- `build/progress.md`: set the **C134 row's Status to `DONE`** with the date, and write the Notes
  cell in the style of the C117 and C103 rows — dense, specific, naming test counts, the decisions
  that carried weight, and the spec gaps found.
- Walk the C134 Definition of Done in `build/manifest.yaml` line by line and record each item's
  state. **Anything not met keeps the status at `PARTIAL`** — including a deferred Tamil corpus, an
  unanswered legal text, or a Lighthouse threshold that was not reached. That is not a failure; it
  is the honest state, and `PARTIAL` with a clear note is what lets somebody finish it.
- Confirm the wave-4c gate now reads **four** web surfaces (S02 changed the manifest text; check it
  survived).

---

## Fences

- **No `MAGERIDE_API_BASE_URL`** and no `depends_on: app-services` on `www-site`.
- **Do not bring the full replica up** alongside a heavy build — `CLAUDE.md`, Build Host. Build the
  image; bring up the `portals` profile alone if you need to smoke-test it.
- **The apex redirect must not catch the other three hosts.** Test it.
- **`build/progress.md` is hand-edited here, never regenerated.** Running
  `generate_build_plan.py` at this point would wipe the handoff log again (see S02).

---

## Verify

```
docker compose -f infra/replica/docker-compose.light-replica.yml --profile portals build www-site
python3 infra/k8s/tools/generate_manifests.py --check
bash -n infra/replica/smoke.sh
npm --prefix portals run lint && npm --prefix portals run build --workspace @mageride/www && npm --prefix portals run test --workspace @mageride/www
git diff --stat build/progress.md      # one row's Status + one handoff entry
```

If the `portals` profile is brought up: `bash infra/replica/smoke.sh` and confirm the five new checks
pass, including the one with `app-services` stopped.

---

## Handoff

- **Component:** C134 www-informational-site — S22 (replica, DNS, smoke) — <date>
- **Status:** DONE | PARTIAL (name every unmet DoD item)
- **Notes:** the smoke-check count before and after; the result of the "backend down" check; the
  go-live rows added; the full Definition-of-Done walk with each item's state; every spec gap found
  across all 22 sessions that has not already been raised as a change set.
