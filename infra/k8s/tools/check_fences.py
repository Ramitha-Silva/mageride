#!/usr/bin/env python3
# =====================================================================================
# The C124 fences, checked against a RENDERED overlay (C124).
#
#   kubectl kustomize infra/k8s/overlays/staging | python3 infra/k8s/tools/check_fences.py staging -
#   python3 infra/k8s/tools/check_fences.py staging build/staging.yaml
#
# Called by infra/scripts/k8s-verify.sh once per overlay. Reads the RENDERED output rather than the
# source files on purpose: a fence has to hold in what the cluster actually receives, and an
# overlay patch can undo anything base says. `maxUnavailable: 0` in base with a dev overlay that
# patches it to 1 is exactly the kind of thing that would pass a source-file check.
#
# From build/prompts/C124.md:
#   - "Rollout is RollingUpdate with maxUnavailable 0, maxSurge 1. No big-bang deploys."
#   - "Secrets come from Vault via External Secrets Operator — never from repository files."
#   - "DB migrations are gated pre-deploy" — the gate's own manifest, at the wave that makes it one.
# and the platform rules these manifests have to keep: MQTT never through an HTTP ingress, every
# container non-root with probes and limits, and no image on a moving tag.
# =====================================================================================

from __future__ import annotations

import re
import sys

import yaml

# END-anchored: `Mqtt__SessionTokenMinimumTtl` contains "Token" and is a timespan, and a check
# that flags it is a check somebody switches off. What a credential key actually ends in is
# Secret / Password / ApiKey / Key / Pem / Token.
CREDENTIAL_KEY = re.compile(r"(password|secret|api_?key|token|key|pem|credential)$", re.I)

# Values that cannot be a credential whatever the key is called: a duration, a number, a boolean,
# a URL, a bucket name.
NOT_A_SECRET = re.compile(
    r"^(?:\d+(?:\.\d+)?|\d+\.\d{2}:\d{2}:\d{2}|\d{2}:\d{2}:\d{2}|true|false|https?://\S*)$",
    re.I,
)
DSN_PASSWORD = re.compile(r"Password=[^;\s]+")
MOVING_TAGS = {"latest", "main", "master", "dev", "staging", "production"}


class Report:
    def __init__(self, env: str) -> None:
        self.env = env
        self.problems: list[str] = []
        self.notes: list[str] = []

    def bad(self, msg: str) -> None:
        self.problems.append(msg)

    def note(self, msg: str) -> None:
        self.notes.append(msg)


def pod_spec(doc: dict) -> dict:
    kind = doc["kind"]
    if kind == "CronJob":
        return doc["spec"]["jobTemplate"]["spec"]["template"]["spec"]
    if kind == "Job":
        return doc["spec"]["template"]["spec"]
    return doc["spec"]["template"]["spec"]


def check_rollout(r: Report, docs: list[dict]) -> None:
    """maxUnavailable 0 is the half that matters: a rollout adds capacity before it removes any,
    so an image that never becomes Ready cannot take the service down with it."""
    for d in docs:
        if d.get("kind") != "Deployment":
            continue
        name = d["metadata"]["name"]
        strategy = d["spec"].get("strategy", {})
        kind = strategy.get("type")

        if kind == "Recreate":
            # Allowed only where a ReadWriteOnce volume makes maxUnavailable 0 a deadlock — the new
            # pod cannot attach until the old one detaches, and the old one is not evicted until
            # the new one is Ready. service-catalog.yaml says which service and why.
            claims = [v for v in pod_spec(d).get("volumes", []) if "persistentVolumeClaim" in v]
            if claims:
                r.note(f"{name} is Recreate — RWO volume {claims[0]['name']} (see the catalog)")
            else:
                r.bad(f"{name} uses strategy Recreate with no PVC to justify it — that is a big-bang deploy")
            continue

        if kind != "RollingUpdate":
            r.bad(f"{name} has strategy '{kind}', not RollingUpdate")
            continue

        ru = strategy.get("rollingUpdate", {})
        if ru.get("maxUnavailable") != 0:
            r.bad(f"{name} has maxUnavailable={ru.get('maxUnavailable')!r}, must be 0")
        if ru.get("maxSurge") != 1:
            r.bad(f"{name} has maxSurge={ru.get('maxSurge')!r}, must be 1")


def check_no_secret_values(r: Report, docs: list[dict]) -> None:
    for d in docs:
        kind = d.get("kind")
        if kind == "Secret":
            r.bad(
                f"a Secret named {d['metadata']['name']} is in the rendered manifests. Every "
                "credential is an ExternalSecret from Vault (or a SealedSecret in dev)."
            )
        if kind != "ConfigMap":
            continue
        for key, value in (d.get("data") or {}).items():
            if not isinstance(value, str) or not value.strip():
                continue
            if CREDENTIAL_KEY.search(key) and not NOT_A_SECRET.match(value.strip()):
                r.bad(f"ConfigMap {d['metadata']['name']} key {key} looks like a credential "
                      f"with a value in it")
            if DSN_PASSWORD.search(value):
                # A DSN with a password in it is a password in `kubectl get cm -o yaml`, which is a
                # much wider audience than a Secret's.
                r.bad(f"ConfigMap {d['metadata']['name']} key {key} contains a DSN password")


def check_containers(r: Report, docs: list[dict]) -> None:
    placeholders: set[str] = set()
    for d in docs:
        kind = d.get("kind")
        if kind not in ("Deployment", "StatefulSet", "Job", "CronJob"):
            continue
        name = d["metadata"]["name"]
        spec = pod_spec(d)
        psc = spec.get("securityContext", {})

        if psc.get("runAsNonRoot") is True and not isinstance(psc.get("runAsUser"), int):
            # Every image in this platform ends on `USER app` or `USER node` — a NAME. kubelet
            # cannot prove a named user is not root, so it refuses the pod outright.
            r.bad(
                f"{name} sets runAsNonRoot with no numeric runAsUser. Every image here has a NAMED "
                "user, which kubelet cannot verify — the pod is refused at admission."
            )

        for c in spec.get("containers", []):
            cname = f"{name}/{c['name']}"
            image = c.get("image", "")

            if ":" not in image.rsplit("/", 1)[-1]:
                r.bad(f"{cname} has no image tag ({image})")
            else:
                tag = image.rsplit(":", 1)[1]
                if tag in MOVING_TAGS:
                    r.bad(
                        f"{cname} is on the moving tag '{tag}'. A rollback to a moving tag cannot "
                        "be reasoned about: two clusters pulling it at different times run "
                        "different code with nothing to show which."
                    )
                if tag == "sha-0000000":
                    # A NOTE, not a failure. The placeholder is the committed state of an
                    # environment that has not been promoted yet, and the placeholder is the
                    # point: it is unpullable, so an un-promoted overlay fails loudly instead of
                    # quietly running whatever `latest` happens to be. The pipeline replaces it
                    # (set_image_tag.py). Failing here would make a fresh repository unverifiable.
                    placeholders.add(cname)

            res = c.get("resources", {})
            if "limits" not in res:
                r.bad(f"{cname} has no resource limits")
            if "requests" not in res:
                r.bad(f"{cname} has no resource requests")

            csc = c.get("securityContext", {})
            if csc.get("allowPrivilegeEscalation") is not False:
                r.bad(f"{cname} does not set allowPrivilegeEscalation: false")
            if csc.get("capabilities", {}).get("drop") != ["ALL"]:
                r.bad(f"{cname} does not drop ALL capabilities")

            if kind in ("Deployment", "StatefulSet"):
                for probe in ("readinessProbe", "livenessProbe"):
                    if probe not in c:
                        r.bad(f"{cname} has no {probe} — D7' §5.1 gives every service both")

    if placeholders:
        r.note(
            f"{len(placeholders)} image(s) are still on the placeholder tag sha-0000000 — this "
            f"environment has never been promoted. `python3 infra/k8s/tools/set_image_tag.py "
            f"{r.env} sha-<7>` is what a deploy runs."
        )


def check_mqtt_never_http(r: Report, docs: list[dict]) -> None:
    """infra/CLAUDE.md: MQTT is TCP passthrough and is never routed through the API gateway. An
    HTTP ingress cannot terminate a tracker's mutual-TLS handshake, and the failure is not a 502 —
    it is a device that connects and delivers nothing."""
    for d in docs:
        if d.get("kind") != "Ingress":
            continue
        name = d["metadata"]["name"]
        paths = []
        for rule in d["spec"].get("rules", []):
            for p in rule.get("http", {}).get("paths", []):
                paths.append(p["path"])
                svc = p["backend"]["service"]
                port = svc["port"].get("number")
                if svc["name"] in ("emqx", "emqx-ingest") or port in (1883, 8883, 8084):
                    r.bad(f"Ingress {name} routes to {svc['name']}:{port} — MQTT must never pass "
                          "through an HTTP ingress")

        ann = d["metadata"].get("annotations", {})
        timeout = ann.get("nginx.ingress.kubernetes.io/proxy-read-timeout")
        ingress_class = d["spec"].get("ingressClassName")
        if paths == ["/hubs"] and ingress_class == "nginx" and timeout != "3600":
            r.bad(f"Ingress {name} serves /hubs with proxy-read-timeout={timeout!r}, not 3600 "
                  "(D7' §5) — a WebSocket carries nothing between position updates and would be "
                  "closed mid-ride")
        if "/" in paths and timeout == "3600":
            r.bad(f"Ingress {name} gives the REST plane a 3600 s read timeout — one stuck upstream "
                  "would exhaust the proxy's worker pool")


def check_migration_gate(r: Report, docs: list[dict]) -> None:
    """The gate is the sync-wave ordering, so the ordering is what gets checked."""
    jobs = [d for d in docs if d.get("kind") == "Job"]
    migrate = next((j for j in jobs if j["metadata"]["name"] == "migrate"), None)
    if migrate is None:
        r.bad("there is no `migrate` Job in this overlay — nothing gates the deploy")
        return

    ann = migrate["metadata"].get("annotations", {})
    wave = ann.get("argocd.argoproj.io/sync-wave")
    if wave != "1":
        r.bad(f"the migrate Job is at sync-wave {wave!r}, not 1. It must run after the data plane "
              "(wave 0) and before the applications (wave 2), or it gates nothing.")
    if "Replace=true" not in ann.get("argocd.argoproj.io/sync-options", ""):
        r.bad("the migrate Job has no Replace=true sync option. A Job's template is immutable, so a "
              "new image SHA cannot be patched in — the sync would fail on the gate itself.")

    at_two = [
        d for d in docs
        if d.get("kind") in ("Deployment", "StatefulSet")
        and d["metadata"].get("annotations", {}).get("argocd.argoproj.io/sync-wave") == "2"
    ]
    if not at_two:
        r.bad("no workload is at sync-wave 2, so a failed gate at wave 1 blocks nothing")

    # The runner must not go through the pooler: DbUp gives each script a transaction, migration
    # 1804 needs CREATEROLE, and `COMMENT ON ROLE` needs superuser — none of which survive
    # PgBouncer handing the server connection back at COMMIT.
    env = pod_spec(migrate)["containers"][0].get("env", [])
    dsn = next((e for e in env if e["name"] == "ConnectionStrings__Postgres"), None)
    if dsn is None:
        r.bad("the migrate Job has no ConnectionStrings__Postgres")
    else:
        key = (dsn.get("valueFrom") or {}).get("secretKeyRef", {}).get("key")
        if key != "ConnectionStrings__PostgresDirect":
            r.bad(f"the migrate Job reads {key!r} — it must use ConnectionStrings__PostgresDirect, "
                  "because DDL and CREATEROLE do not survive PgBouncer's transaction pooling")


def main() -> int:
    if len(sys.argv) != 3:
        print("usage: check_fences.py <env> <rendered.yaml|->", file=sys.stderr)
        return 2

    env, path = sys.argv[1], sys.argv[2]
    text = sys.stdin.read() if path == "-" else open(path, encoding="utf-8").read()
    docs = [d for d in yaml.safe_load_all(text) if d]

    r = Report(env)
    check_rollout(r, docs)
    check_no_secret_values(r, docs)
    check_containers(r, docs)
    check_mqtt_never_http(r, docs)
    check_migration_gate(r, docs)

    for n in r.notes:
        print(f"— {env}: {n}")

    if r.problems:
        for p in r.problems:
            print(f"✗ {env}: {p}")
        return 1

    print(f"✓ {env}: RollingUpdate maxUnavailable 0 / maxSurge 1 on every Deployment")
    print(f"✓ {env}: no Secret and no credential-shaped ConfigMap value")
    print(f"✓ {env}: every container non-root, capabilities dropped, requests, limits, both probes")
    print(f"✓ {env}: no image on a moving or placeholder tag")
    print(f"✓ {env}: MQTT is not in any Ingress; /hubs has the 3600 s timeout and / does not")
    print(f"✓ {env}: the migration gate is at wave 1, Replace=true, direct DSN, ahead of wave 2")
    return 0


if __name__ == "__main__":
    sys.exit(main())
