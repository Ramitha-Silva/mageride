#!/usr/bin/env python3
# =====================================================================================
# MageRide manifest generator (C124).
#
# Renders the repetitive half of infra/k8s from infra/k8s/service-catalog.yaml:
#
#   base/services/<name>.yaml                     Deployment|StatefulSet + Service + HPA + PDB
#   base/services/kustomization.yaml
#   base/portals/<name>.yaml                      Deployment + Service + HPA + PDB
#   base/portals/kustomization.yaml
#   platform/external-secrets/base/<name>.yaml    ExternalSecret (Vault -> <name>-secret)
#   platform/external-secrets/base/kustomization.yaml
#
#   python3 infra/k8s/tools/generate_manifests.py            # write
#   python3 infra/k8s/tools/generate_manifests.py --check    # fail on drift (CI + k8s-verify.sh)
#   python3 infra/k8s/tools/generate_manifests.py --matrix    # the CI image matrix, as JSON
#
# WHY A GENERATOR AND NOT 33 HAND-WRITTEN FILES: thirty-one .NET workloads plus three
# portals share one shape, and the parts that differ — a port, a probe, a secret list, a
# replica count — are exactly the parts worth seeing side by side. Hand-written, the
# difference between two services would be invisible in a diff and drift would be
# undetectable; here `--check` makes drift a red build. It is also what lets the image
# matrix and the ExternalSecret set come from the same list as the manifests, so adding a
# service is one catalog entry and never a workflow edit.
#
# Same convention as build/tools/generate_build_plan.py: the OUTPUT IS NOT EDITED. Every
# generated file says so in its first line.
# =====================================================================================

from __future__ import annotations

import argparse
import json
import sys
import textwrap
from pathlib import Path

import yaml

REPO = Path(__file__).resolve().parents[3]
K8S = REPO / "infra" / "k8s"
CATALOG = K8S / "service-catalog.yaml"

BANNER = (
    "# GENERATED FILE — do not edit.\n"
    "# Source: infra/k8s/service-catalog.yaml\n"
    "# Regenerate: python3 infra/k8s/tools/generate_manifests.py\n"
)

# D7' §5.1 row 1: stateless .NET services. One place, so a threshold change is one edit.
READINESS = {"initialDelaySeconds": 15, "periodSeconds": 10, "failureThreshold": 3}
LIVENESS = {"initialDelaySeconds": 30, "periodSeconds": 15, "failureThreshold": 6}
# D7' §5.1's tcp-adapter row: "TCP socket on 5023", 10 s, init 20 s, threshold 5.
TCP_READINESS = {"initialDelaySeconds": 20, "periodSeconds": 10, "failureThreshold": 5}
TCP_LIVENESS = {"initialDelaySeconds": 30, "periodSeconds": 10, "failureThreshold": 5}


# -------------------------------------------------------------------------------------
# rendering helpers
# -------------------------------------------------------------------------------------
def wrap(text: str, prefix: str = "# ", width: int = 88) -> str:
    """A catalog `why` as a comment block, so the reason travels with the manifest."""
    if not text:
        return ""
    body = " ".join(text.split())
    return "\n".join(textwrap.wrap(body, width=width, initial_indent=prefix, subsequent_indent=prefix)) + "\n"


def labels(name: str, component: str) -> str:
    return (
        f"    app: {name}\n"
        f"    app.kubernetes.io/name: {name}\n"
        f"    app.kubernetes.io/component: {component}\n"
        f"    app.kubernetes.io/part-of: mageride\n"
    )


# ArgoCD applies one wave at a time and waits for the wave to be Healthy before the next. The
# whole ordering is in infra/k8s/platform/argocd/README.md; the application tier is wave 2,
# after the data plane (0) and after the migration gate (1). That single number is what makes
# "a failed migration leaves the previous version serving" true: wave 2 is never applied.
APP_WAVE = '  annotations:\n    argocd.argoproj.io/sync-wave: "2"\n'


def resources(spec: dict) -> str:
    r, l = spec["requests"], spec["limits"]
    return (
        "          resources:\n"
        f"            requests: {{ cpu: \"{r['cpu']}\", memory: {r['memory']} }}\n"
        f"            limits: {{ cpu: \"{l['cpu']}\", memory: {l['memory']} }}\n"
    )


def ports_block(indent: str, primary_name: str, port: int, extra: list[dict]) -> str:
    out = f"{indent}ports:\n{indent}  - {{ name: {primary_name}, containerPort: {port}, protocol: TCP }}\n"
    for p in extra:
        out += f"{indent}  - {{ name: {p['name']}, containerPort: {p['port']}, protocol: {p['protocol']} }}\n"
    return out


def env_from(name: str, has_secret: bool) -> str:
    out = (
        "          envFrom:\n"
        "            # D7' §4.1's non-secret set, and the in-cluster address of every upstream.\n"
        "            - configMapRef: { name: common-config }\n"
        "            - configMapRef: { name: service-endpoints }\n"
        "            # Vault, via the External Secrets Operator (D7' §13). Never a repository file.\n"
        "            - secretRef: { name: common-secret }\n"
    )
    if has_secret:
        out += f"            - secretRef: {{ name: {name}-secret }}\n"
    return out


def volume_mounts(vols: list[dict]) -> str:
    # readOnlyRootFilesystem is on, so /tmp is always an emptyDir: .NET writes there
    # (temp files, the diagnostic socket) and an image whose root is read-only otherwise
    # fails at the first write with no useful message.
    out = "          volumeMounts:\n            - { name: tmp, mountPath: /tmp }\n"
    for v in vols:
        ro = ", readOnly: true" if v.get("readOnly") else ""
        out += f"            - {{ name: {v['name']}, mountPath: {v['mountPath']}{ro} }}\n"
    return out


def volumes(vols: list[dict]) -> str:
    out = "      volumes:\n        - { name: tmp, emptyDir: {} }\n"
    for v in vols:
        if "secret" in v:
            out += f"        - name: {v['name']}\n          secret:\n            secretName: {v['secret']}\n            defaultMode: 0400\n"
        elif "claim" in v:
            out += f"        - name: {v['name']}\n          persistentVolumeClaim:\n            claimName: {v['claim']}\n"
    return out


def pod_security(uid: int) -> str:
    # `runAsUser` is numeric and not left to the image, because it cannot be: every image
    # here ends on `USER app` (or `USER node`), a NAME, and kubelet refuses a pod with
    # `runAsNonRoot: true` whose image user is non-numeric — it cannot prove the name is
    # not root. 1654 is APP_UID in the .NET 10 images, 1000 is `node` in node:24-alpine.
    return (
        "      securityContext:\n"
        "        runAsNonRoot: true\n"
        f"        runAsUser: {uid}\n"
        f"        runAsGroup: {uid}\n"
        f"        fsGroup: {uid}\n"
        "        seccompProfile: { type: RuntimeDefault }\n"
    )


CONTAINER_SECURITY = (
    "          securityContext:\n"
    "            allowPrivilegeEscalation: false\n"
    "            readOnlyRootFilesystem: true\n"
    "            capabilities: { drop: [ALL] }\n"
)


def topology_spread(name: str, replicas: int) -> str:
    """Spread replicas across nodes, then across zones. Advisory, never blocking."""
    if replicas < 2:
        return ""
    # `ScheduleAnyway`, not `DoNotSchedule`, and that is the whole design: on the K3s
    # single-node overlay a hard constraint would leave the second replica Pending for ever,
    # and on DOKS a node pool that is momentarily full would block a rollout that
    # maxUnavailable 0 is waiting on. Advisory spreading gives the availability on a healthy
    # cluster and costs nothing on a constrained one.
    return (
        "      topologySpreadConstraints:\n"
        "        - maxSkew: 1\n"
        "          topologyKey: kubernetes.io/hostname\n"
        "          whenUnsatisfiable: ScheduleAnyway\n"
        f"          labelSelector:\n            matchLabels: {{ app: {name} }}\n"
        "        - maxSkew: 1\n"
        "          topologyKey: topology.kubernetes.io/zone\n"
        "          whenUnsatisfiable: ScheduleAnyway\n"
        f"          labelSelector:\n            matchLabels: {{ app: {name} }}\n"
    )


def probes(kind: str, port_name: str) -> str:
    if kind == "http":
        return (
            "          # D7' §5.1: /health/ready pings DB + Redis + Kafka; /health/live is liveness only.\n"
            f"          readinessProbe:\n            httpGet: {{ path: /health/ready, port: {port_name} }}\n"
            f"            initialDelaySeconds: {READINESS['initialDelaySeconds']}\n"
            f"            periodSeconds: {READINESS['periodSeconds']}\n"
            "            timeoutSeconds: 3\n"
            f"            failureThreshold: {READINESS['failureThreshold']}\n"
            f"          livenessProbe:\n            httpGet: {{ path: /health/live, port: {port_name} }}\n"
            f"            initialDelaySeconds: {LIVENESS['initialDelaySeconds']}\n"
            f"            periodSeconds: {LIVENESS['periodSeconds']}\n"
            "            timeoutSeconds: 3\n"
            f"            failureThreshold: {LIVENESS['failureThreshold']}\n"
        )
    if kind == "tcp":
        return (
            "          # D7' §5.1's tcp-adapter row. There is no HTTP surface in this process at all\n"
            "          # (mqtt-topics.md §7), so a socket connect is the only probe there can be.\n"
            f"          readinessProbe:\n            tcpSocket: {{ port: {port_name} }}\n"
            f"            initialDelaySeconds: {TCP_READINESS['initialDelaySeconds']}\n"
            f"            periodSeconds: {TCP_READINESS['periodSeconds']}\n"
            f"            failureThreshold: {TCP_READINESS['failureThreshold']}\n"
            f"          livenessProbe:\n            tcpSocket: {{ port: {port_name} }}\n"
            f"            initialDelaySeconds: {TCP_LIVENESS['initialDelaySeconds']}\n"
            f"            periodSeconds: {TCP_LIVENESS['periodSeconds']}\n"
            f"            failureThreshold: {TCP_LIVENESS['failureThreshold']}\n"
        )
    raise SystemExit(f"unknown probe kind: {kind}")


def strategy(spec: dict) -> str:
    # The C124 fence: RollingUpdate, maxUnavailable 0, maxSurge 1 (D7' §7). `Recreate` is
    # allowed only where the catalog says why — a ReadWriteOnce volume makes maxUnavailable 0
    # a deadlock, not a safety property.
    if spec.get("strategy") == "Recreate":
        return "  strategy:\n    type: Recreate\n"
    return (
        "  strategy:\n"
        "    type: RollingUpdate\n"
        "    rollingUpdate: { maxUnavailable: 0, maxSurge: 1 }\n"
    )


def hpa(name: str, auto: dict, kind: str = "Deployment") -> str:
    return (
        "---\n"
        "apiVersion: autoscaling/v2\n"
        "kind: HorizontalPodAutoscaler\n"
        "metadata:\n"
        f"  name: {name}\n"
        "  labels:\n" + labels(name, "autoscaling") + APP_WAVE +
        "spec:\n"
        f"  scaleTargetRef: {{ apiVersion: apps/v1, kind: {kind}, name: {name} }}\n"
        f"  minReplicas: {auto['min']}\n"
        f"  maxReplicas: {auto['max']}\n"
        "  metrics:\n"
        "    - type: Resource\n"
        "      resource:\n"
        "        name: cpu\n"
        "        target: { type: Utilization, averageUtilization: 70 }\n"
        "  behavior:\n"
        "    # Scale up fast, down slowly. A dispatch round that lost a pod mid-offer is a\n"
        "    # rider watching a spinner; a pod that lived five minutes too long is pennies.\n"
        "    scaleUp: { stabilizationWindowSeconds: 30 }\n"
        "    scaleDown: { stabilizationWindowSeconds: 300 }\n"
    )


def pdb(name: str, spec: dict, replicas: int) -> str:
    if replicas < 2:
        return ""
    cfg = spec.get("pdb") or {"maxUnavailable": 1}
    key, value = next(iter(cfg.items()))
    return (
        "---\n"
        "apiVersion: policy/v1\n"
        "kind: PodDisruptionBudget\n"
        "metadata:\n"
        f"  name: {name}\n"
        "  labels:\n" + labels(name, "policy") + APP_WAVE +
        "spec:\n"
        f"  {key}: {value}\n"
        f"  selector:\n    matchLabels: {{ app: {name} }}\n"
    )


def service(
    name: str,
    port: int,
    target: str,
    extra: list[dict],
    headless: bool = False,
    primary_name: str = "http",
) -> str:
    out = (
        "---\n"
        "apiVersion: v1\n"
        "kind: Service\n"
        "metadata:\n"
        f"  name: {name}\n"
        "  labels:\n" + labels(name, "service") + APP_WAVE +
        "spec:\n"
    )
    if headless:
        # A StatefulSet needs a headless governing Service for stable per-pod DNS.
        out += "  clusterIP: None\n"
    out += (
        "  type: ClusterIP\n"
        f"  selector: {{ app: {name} }}\n"
        "  ports:\n"
        f"    - {{ name: {primary_name}, port: {port}, targetPort: {target}, protocol: TCP }}\n"
    )
    for p in extra:
        out += f"    - {{ name: {p['name']}, port: {p['port']}, targetPort: {p['name']}, protocol: {p['protocol']} }}\n"
    return out


# -------------------------------------------------------------------------------------
# workloads
# -------------------------------------------------------------------------------------
def render_service(cat: dict, svc: dict) -> str:
    d = cat["defaults"]
    name = svc["name"]
    port = svc.get("port", d["port"])
    probe = svc.get("probe", d["probe"])
    replicas = svc.get("replicas", d["replicas"])
    res = svc.get("resources", d["resources"])
    uid = svc.get("runAsUser", d["runAsUser"])
    workload = svc.get("workload", d["workload"])
    dockerfile = svc.get("dockerfile", d["dockerfile"])
    extra = svc.get("extraPorts", []) or []
    vols = svc.get("volumes", []) or []
    secrets = svc.get("secrets", []) or []
    auto = svc.get("autoscale", d["autoscale"]) if "autoscale" in svc else d["autoscale"]
    grace = svc.get("terminationGracePeriodSeconds", 30)
    # The primary port carries its protocol's name where there is one — 5023 is GT06 (T-01),
    # not "http", and a Service port named `http` on a socket that speaks a binary tracker
    # protocol is the kind of label an operator believes.
    port_name = "gt06" if probe == "tcp" else "http"
    image = f"{cat['registry']}/{name}:{cat['placeholderTag']}"

    head = BANNER + "#\n" + f"# {name}  ·  backend/src/{svc['project']}  ·  {dockerfile}\n"
    if svc.get("why"):
        head += "#\n" + wrap(svc["why"])

    body = (
        "---\n"
        "apiVersion: apps/v1\n"
        f"kind: {workload}\n"
        "metadata:\n"
        f"  name: {name}\n"
        "  labels:\n" + labels(name, "backend") + APP_WAVE +
        "spec:\n"
        f"  replicas: {replicas}\n"
        "  revisionHistoryLimit: 5\n"
    )
    if workload == "StatefulSet":
        body += (
            f"  serviceName: {name}\n"
            "  podManagementPolicy: Parallel\n"
            "  updateStrategy:\n    type: RollingUpdate\n"
        )
    else:
        body += "  progressDeadlineSeconds: 600\n  minReadySeconds: 10\n" + strategy(svc)
    body += (
        f"  selector:\n    matchLabels: {{ app: {name} }}\n"
        "  template:\n"
        "    metadata:\n"
        "      labels:\n" + labels(name, "backend").replace("    ", "        ") +
        "      annotations:\n"
    )
    if probe == "http":
        body += (
            "        prometheus.io/scrape: \"true\"\n"
            f"        prometheus.io/port: \"{port}\"\n"
            "        prometheus.io/path: /metrics\n"
        )
    else:
        body += (
            "        # No /metrics: this process has no HTTP surface. Its telemetry is OTLP to the\n"
            "        # collector (Otel__Endpoint), which is the only path it has (C119).\n"
            "        prometheus.io/scrape: \"false\"\n"
        )
    body += (
        "    spec:\n"
        "      serviceAccountName: mageride\n"
        + pod_security(uid)
        + topology_spread(name, replicas)
        + f"      terminationGracePeriodSeconds: {grace}\n"
        "      containers:\n"
        f"        - name: {name}\n"
        f"          image: {image}\n"
        "          imagePullPolicy: IfNotPresent\n"
        + ports_block("          ", port_name, port, extra)
        + env_from(name, bool(secrets))
    )
    if port != 5000 and probe == "http":
        body += (
            "          env:\n"
            "            # The image defaults to 5000 (D7' §2.2); this service is D7' §2.1's exception.\n"
            f"            - {{ name: ASPNETCORE_URLS, value: \"http://+:{port}\" }}\n"
        )
    body += resources(res) + probes(probe, port_name) + CONTAINER_SECURITY + volume_mounts(vols)
    body += volumes(vols)

    tail = service(
        name,
        80 if probe == "http" else port,
        port_name,
        extra,
        headless=(workload == "StatefulSet"),
        primary_name=port_name,
    )
    if auto:
        tail += hpa(name, auto, workload)
    tail += pdb(name, svc, replicas)
    return head + body + tail


def render_portal(cat: dict, p: dict) -> str:
    name = p["name"]
    port = p["port"]
    res = p["resources"]
    image = f"{cat['registry']}/{name}:{cat['placeholderTag']}"

    head = (
        BANNER + "#\n"
        f"# {name}  ·  portals/{p['portal']}  ·  infra/docker/Dockerfile.portal  ·  {p['host']}\n"
    )
    if p.get("why"):
        head += "#\n" + wrap(p["why"])

    body = (
        "---\n"
        "apiVersion: apps/v1\n"
        "kind: Deployment\n"
        "metadata:\n"
        f"  name: {name}\n"
        "  labels:\n" + labels(name, "portal") + APP_WAVE +
        "spec:\n"
        f"  replicas: {p['replicas']}\n"
        "  revisionHistoryLimit: 5\n"
        "  progressDeadlineSeconds: 600\n"
        "  minReadySeconds: 10\n"
        + strategy(p)
        + f"  selector:\n    matchLabels: {{ app: {name} }}\n"
        "  template:\n"
        "    metadata:\n"
        "      labels:\n" + labels(name, "portal").replace("    ", "        ") +
        "    spec:\n"
        "      serviceAccountName: mageride\n"
        + pod_security(1000)
        + topology_spread(name, p["replicas"])
        + "      terminationGracePeriodSeconds: 30\n"
        "      containers:\n"
        f"        - name: {name}\n"
        f"          image: {image}\n"
        "          imagePullPolicy: IfNotPresent\n"
        + ports_block("          ", "http", port, [])
        + "          envFrom:\n"
          "            # NEXT_PUBLIC_* only. A portal is a browser bundle: anything it can read,\n"
          "            # a user can read, so no Secret is ever mounted into one.\n"
          "            - configMapRef: { name: portal-config }\n"
        + resources(res)
        + "          # The Dockerfile's own healthcheck, as a probe: Next.js serves `/` as soon as\n"
          "          # the standalone server is listening, and a redirect to /login counts (kubelet\n"
          "          # treats any 2xx-3xx as success).\n"
          "          readinessProbe:\n            httpGet: { path: /, port: http }\n"
          "            initialDelaySeconds: 10\n            periodSeconds: 10\n            timeoutSeconds: 3\n            failureThreshold: 3\n"
          "          livenessProbe:\n            httpGet: { path: /, port: http }\n"
          "            initialDelaySeconds: 20\n            periodSeconds: 15\n            timeoutSeconds: 3\n            failureThreshold: 6\n"
        + CONTAINER_SECURITY
        + "          volumeMounts:\n            - { name: tmp, mountPath: /tmp }\n"
          "            # Next.js writes its image-optimisation cache under .next/cache at runtime.\n"
          f"            - {{ name: next-cache, mountPath: /app/portals/{p['portal']}/.next/cache }}\n"
        + "      volumes:\n        - { name: tmp, emptyDir: {} }\n        - { name: next-cache, emptyDir: {} }\n"
    )

    tail = service(name, 80, "http", [])
    if p.get("autoscale"):
        tail += hpa(name, p["autoscale"])
    tail += pdb(name, p, p["replicas"])
    return head + body + tail


def resolve(cat: dict, service: str, env_key: str) -> tuple[str, str]:
    """(vault path, property) for one env var. The alias table wins; otherwise own path/name."""
    alias = (cat.get("aliases") or {}).get(env_key)
    if alias:
        return alias["path"], alias.get("property", env_key)
    return service, env_key


def render_external_secret(cat: dict, svc: dict) -> str:
    name = svc["name"]
    head = (
        BANNER + "#\n"
        f"# {name}-secret — the D7' §4.2 rows whose Secret column is yes.\n"
        "#\n"
        "# Vault is the only source (D7' §13). `key` is the path INSIDE the KV mount the\n"
        "# ClusterSecretStore points at, and the mount is what differs per environment — so this\n"
        "# file is identical in dev, staging and production and no environment name appears in it.\n"
        "#\n"
        "# A `key` that is not this service's own name is a SHARED credential — see `aliases` in\n"
        "# service-catalog.yaml for which sentence in which options class requires it.\n"
    )
    body = (
        "---\n"
        "apiVersion: external-secrets.io/v1\n"
        "kind: ExternalSecret\n"
        "metadata:\n"
        f"  name: {name}-secret\n"
        "  labels:\n" + labels(name, "secret") +
        "spec:\n"
        "  # 1 h, and rotation does not wait for it: the rotation procedure restarts the\n"
        "  # Deployment, which re-reads the Secret ESO has already updated\n"
        "  # (docs/runbooks/secret-rotation.md).\n"
        "  refreshInterval: 1h\n"
        "  secretStoreRef:\n    name: mageride-vault\n    kind: ClusterSecretStore\n"
        "  target:\n"
        f"    name: {name}-secret\n"
        "    creationPolicy: Owner\n"
        "    # Retain: deleting the ExternalSecret must not delete the running pods' credentials.\n"
        "    deletionPolicy: Retain\n"
        "  data:\n"
    )
    for key in svc.get("secrets", []):
        path, prop = resolve(cat, name, key)
        body += (
            f"    - secretKey: {key}\n"
            f"      remoteRef: {{ key: {path}, property: {prop} }}\n"
        )
    return head + body


# -------------------------------------------------------------------------------------
# driver
# -------------------------------------------------------------------------------------
def kustomization(header: str, files: list[str]) -> str:
    out = BANNER + "#\n" + f"# {header}\n---\n" "apiVersion: kustomize.config.k8s.io/v1beta1\nkind: Kustomization\nresources:\n"
    for f in sorted(files):
        out += f"  - {f}\n"
    return out


def build(cat: dict) -> dict[Path, str]:
    files: dict[Path, str] = {}

    svc_files = []
    for svc in cat["services"]:
        path = K8S / "base" / "services" / f"{svc['name']}.yaml"
        files[path] = render_service(cat, svc)
        svc_files.append(f"{svc['name']}.yaml")
    files[K8S / "base" / "services" / "kustomization.yaml"] = kustomization(
        f"{len(svc_files)} backend workloads (D7' §5). One file per service, generated from the catalog.",
        svc_files,
    )

    portal_files = []
    for p in cat["portals"]:
        path = K8S / "base" / "portals" / f"{p['name']}.yaml"
        files[path] = render_portal(cat, p)
        portal_files.append(f"{p['name']}.yaml")
    files[K8S / "base" / "portals" / "kustomization.yaml"] = kustomization(
        "The three Next.js surfaces (AL-02 / AL-03 / AL-44).", portal_files
    )

    es_files = []
    for svc in cat["services"]:
        if not svc.get("secrets"):
            continue
        path = K8S / "platform" / "external-secrets" / "base" / f"{svc['name']}.yaml"
        files[path] = render_external_secret(cat, svc)
        es_files.append(f"{svc['name']}.yaml")
    # One `images` Component per environment — the list a promotion rewrites, and the only
    # file `infra/k8s/tools/set_image_tag.py` touches. A Component rather than the overlay's
    # own `images:` block so that a promotion commit is a diff of nothing but image tags:
    # reviewable at a glance, and impossible to sneak a manifest change into.
    all_images = [svc["name"] for svc in cat["services"]]
    all_images += [p["name"] for p in cat["portals"]]
    all_images.append(cat["migrator"]["name"])
    for env in cat["environments"]:
        body = (
            BANNER + "#\n"
            f"# {env}: the promoted image tag for all {len(all_images)} images (D7' §7 —\n"
            "# \"dev->staging->prod promotion by image SHA tag\").\n"
            "#\n"
            "# Every image carries the SAME tag, because a deploy is one commit's worth of\n"
            "# platform and not a per-service version matrix. `sha-0000000` is unpullable on\n"
            "# purpose: an environment that has never been promoted must fail to pull rather\n"
            "# than quietly run `latest`.\n"
            "#\n"
            "# Written by: python3 infra/k8s/tools/set_image_tag.py " + env + " sha-<7>\n"
            "---\n"
            "apiVersion: kustomize.config.k8s.io/v1alpha1\n"
            "kind: Component\n"
            "images:\n"
        )
        for image in all_images:
            body += f"  - name: {cat['registry']}/{image}\n    newTag: {cat['placeholderTag']}\n"
        files[K8S / "overlays" / env / "images" / "kustomization.yaml"] = body

    files[K8S / "platform" / "external-secrets" / "base" / "kustomization.yaml"] = kustomization(
        "One ExternalSecret per service that has a D7' §4.2 secret, plus the hand-written ones\n"
        "# that belong to no single service: the platform-wide set, the data plane's own\n"
        "# credentials and the registry pull secret.",
        es_files
        + [
            "common-secret.yaml",
            "emqx-auth.yaml",
            "emqx-device-ca.yaml",
            "emqx-tls.yaml",
            "ghcr-pull.yaml",
            "postgres-superuser.yaml",
            "tcp-adapter-psk.yaml",
        ],
    )
    return files


def matrix(cat: dict) -> str:
    """The CI build/push/sign matrix — one entry per image, from the same list.

    `build_arg` is NEWLINE-separated, not space-separated. `docker/build-push-action`'s `build-args`
    input takes one `K=V` per LINE and treats a space as part of the value, so the portals' two pairs
    arrived as a single arg named PORTAL whose value was `admin PORT=3001` — `npm run build
    --workspace "admin PORT=3001"` then failed with `No workspaces found`. The .NET images have one
    pair each and were unaffected, which is why only the three portals broke, and why nothing noticed
    until the portal build context was fixed and they got far enough to run npm at all.
    """
    d = cat["defaults"]
    out = []
    for svc in cat["services"]:
        out.append(
            {
                "image": svc["name"],
                "project": svc["project"],
                "dockerfile": svc.get("dockerfile", d["dockerfile"]),
                "build_arg": f"SERVICE={svc['project']}",
            }
        )
    for p in cat["portals"]:
        out.append(
            {
                "image": p["name"],
                "project": f"portals/{p['portal']}",
                "dockerfile": "infra/docker/Dockerfile.portal",
                "build_arg": f"PORTAL={p['portal']}\nPORT={p['port']}",
            }
        )
    m = cat["migrator"]
    out.append(
        {
            "image": m["name"],
            "project": m["project"],
            "dockerfile": m["dockerfile"],
            "build_arg": f"SERVICE={m['project']}",
        }
    )
    return json.dumps(out, separators=(",", ":"))


def main() -> int:
    ap = argparse.ArgumentParser(description="Render infra/k8s from service-catalog.yaml")
    ap.add_argument("--check", action="store_true", help="exit 1 if any generated file is stale")
    ap.add_argument("--matrix", action="store_true", help="print the CI image matrix as JSON")
    args = ap.parse_args()

    cat = yaml.safe_load(CATALOG.read_text(encoding="utf-8"))

    if args.matrix:
        print(matrix(cat))
        return 0

    files = build(cat)

    if args.check:
        stale = [p for p, body in files.items() if not p.exists() or p.read_text(encoding="utf-8") != body]
        if stale:
            print("error: generated manifests are stale. Run:", file=sys.stderr)
            print("  python3 infra/k8s/tools/generate_manifests.py", file=sys.stderr)
            for p in sorted(stale):
                print(f"  {p.relative_to(REPO)}", file=sys.stderr)
            return 1
        print(f"{len(files)} generated manifests are current")
        return 0

    for path, body in files.items():
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(body, encoding="utf-8")
    print(f"wrote {len(files)} files under infra/k8s/")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
