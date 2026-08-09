#!/usr/bin/env python3
# =====================================================================================
# Read or write one environment's promoted image tag (C124).
#
#   python3 infra/k8s/tools/set_image_tag.py staging --print       # what is deployed
#   python3 infra/k8s/tools/set_image_tag.py staging sha-1a2b3c4   # promote
#
# `infra/k8s/overlays/<env>/images/kustomization.yaml` is the ONE file a deploy changes, and this
# is the only thing that changes it. Everything else about a deploy — the workflows, the
# runbooks, a rollback — is a call to this script plus a git commit.
#
# --- Why a script and not `kustomize edit set image` -----------------------------------
# Three reasons, in order of how much they matter:
#
#   1. `kustomize edit` REWRITES THE FILE. It parses the kustomization, drops every comment, and
#      re-emits it — so a promotion commit would show 34 tag changes plus the deletion of the
#      header that explains what the file is. Here the diff is exactly the `newTag:` lines, which
#      is what makes `.github/workflows/deploy.yml`'s "nothing but tags changed" assertion
#      possible at all.
#   2. It would happily add an image the catalog does not have, or leave one behind that the
#      catalog gained. This script asserts the list matches `service-catalog.yaml` before it
#      writes, so an image that exists in one place and not the other is a failed promotion
#      rather than a service that silently keeps its old tag for ever.
#   3. `kustomize` is a separate binary. `kubectl kustomize` can build but cannot edit, so
#      depending on `kustomize edit` means installing a second tool in every workflow that
#      deploys.
#
# Line-oriented, therefore. It does not parse the YAML to write it — it rewrites the `newTag:`
# lines in place and validates the result by parsing it afterwards.
# =====================================================================================

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

import yaml

REPO = Path(__file__).resolve().parents[3]
K8S = REPO / "infra" / "k8s"
CATALOG = K8S / "service-catalog.yaml"

# `sha-` + exactly seven hex digits. Deliberately strict: `latest`, `main` and a branch name are
# all things somebody will try, and every one of them makes a rollback unprovable — two clusters
# pulling the same moving tag at different times can run different code with nothing to show it.
TAG = re.compile(r"^sha-[0-9a-f]{7}$")
NEW_TAG_LINE = re.compile(r"^(\s*newTag:\s*)(\S+)\s*$")


def catalog_images(cat: dict) -> list[str]:
    images = [s["name"] for s in cat["services"]]
    images += [p["name"] for p in cat["portals"]]
    images.append(cat["migrator"]["name"])
    return [f"{cat['registry']}/{i}" for i in images]


def read_tag(path: Path) -> str:
    """The single tag every image in the file carries — and an error if they disagree."""
    doc = yaml.safe_load(path.read_text(encoding="utf-8"))
    tags = {entry["newTag"] for entry in doc.get("images", [])}
    if not tags:
        raise SystemExit(f"error: {path} declares no images")
    if len(tags) > 1:
        # A split tag set means a previous promotion failed halfway. Refusing to guess is the
        # point: a deploy is one commit's worth of platform, and "which of these two is the
        # deployed version" has no answer.
        raise SystemExit(
            f"error: {path.relative_to(REPO)} carries more than one tag ({', '.join(sorted(tags))}).\n"
            "       A promotion was interrupted. Set them all with:\n"
            f"       python3 infra/k8s/tools/set_image_tag.py <env> <tag>"
        )
    return tags.pop()


def main() -> int:
    ap = argparse.ArgumentParser(description="Read or write an environment's promoted image tag")
    ap.add_argument("environment")
    ap.add_argument("tag", nargs="?", help="sha-xxxxxxx; omit with --print")
    ap.add_argument("--print", action="store_true", dest="show", help="print the current tag and exit")
    args = ap.parse_args()

    cat = yaml.safe_load(CATALOG.read_text(encoding="utf-8"))
    if args.environment not in cat["environments"]:
        raise SystemExit(
            f"error: unknown environment '{args.environment}'. "
            f"The catalog declares: {', '.join(cat['environments'])}"
        )

    path = K8S / "overlays" / args.environment / "images" / "kustomization.yaml"
    if not path.exists():
        raise SystemExit(
            f"error: {path.relative_to(REPO)} does not exist.\n"
            "       Run: python3 infra/k8s/tools/generate_manifests.py"
        )

    if args.show or args.tag is None:
        print(read_tag(path))
        return 0

    if not TAG.match(args.tag):
        raise SystemExit(
            f"error: '{args.tag}' is not a sha-xxxxxxx tag.\n"
            "       Deploying a moving tag (latest, main, a branch) makes a rollback unprovable."
        )

    text = path.read_text(encoding="utf-8")

    # --- the list must match the catalog BEFORE anything is written -------------------
    doc = yaml.safe_load(text)
    have = [entry["name"] for entry in doc.get("images", [])]
    want = catalog_images(cat)
    if have != want:
        missing = [i for i in want if i not in have]
        extra = [i for i in have if i not in want]
        raise SystemExit(
            f"error: {path.relative_to(REPO)} does not match service-catalog.yaml.\n"
            + (f"       missing: {', '.join(missing)}\n" if missing else "")
            + (f"       extra:   {', '.join(extra)}\n" if extra else "")
            + "       Run: python3 infra/k8s/tools/generate_manifests.py"
        )

    # --- rewrite, line by line, so every comment survives ----------------------------
    out, changed = [], 0
    for line in text.splitlines(keepends=True):
        m = NEW_TAG_LINE.match(line.rstrip("\n"))
        if m:
            if m.group(2) != args.tag:
                changed += 1
            out.append(f"{m.group(1)}{args.tag}\n")
        else:
            out.append(line)
    body = "".join(out)

    # Parse what we are about to write, not what we read. A regex that matched something it
    # should not have would otherwise produce a broken kustomization that only fails at deploy.
    verify = yaml.safe_load(body)
    if {e["newTag"] for e in verify["images"]} != {args.tag}:
        raise SystemExit("error: the rewritten file does not carry the tag uniformly — refusing to write")
    if [e["name"] for e in verify["images"]] != want:
        raise SystemExit("error: the rewrite changed the image list — refusing to write")

    path.write_text(body, encoding="utf-8")
    if changed:
        print(f"{args.environment}: {len(want)} images -> {args.tag}")
    else:
        print(f"{args.environment}: already at {args.tag} (nothing written)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
