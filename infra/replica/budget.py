#!/usr/bin/env python3
"""The replica's memory budget, read from the spec, and the compose file checked against it.

    python3 infra/replica/budget.py totals            # what the spec budgets, as JSON
    python3 infra/replica/budget.py drift <compose>   # per-container disagreements, one per line

WHY THE SPEC IS THE SOURCE. `specs/lightweight-production-replica.md` has the resource table, so
copying a number into a script is copying a number that drifts. It is also the only way to answer the
question at all: the C125 prompt's definition of done says "~18.9 GB" and the spec's own totals are
16.7 GB (core 11) and 19.7 GB (core + voip + both portals + monitoring), so there is no single figure
to hardcode even if hardcoding were wise.

WHAT COUNTS AS CORE. The table marks the rest: `*(optional)*` on voip, both portals, nominatim and
monitoring, and `*(batch, on-demand)*` on osm-pipeline. Nominatim is optional AND explicitly hosted on
a separate VPS ("too heavy to co-locate"), so counting its 8 GB against this box would refuse a deploy
that fits — which is exactly what the first version of this did.
"""

from __future__ import annotations

import json
import os
import re
import subprocess
import sys


SPEC = "specs/lightweight-production-replica.md"

# A container whose row is not marked optional but which is still not part of steady state on THIS
# box. Named rather than pattern-matched, so adding one is a decision.
ELSEWHERE = {"nominatim"}


def rows() -> dict[str, dict]:
    """Every container row in the spec's resource table.

    Scoped to the `## Resource Summary` section, and that scoping is load-bearing rather than tidy.
    The spec has several other tables with a size in their second column — the very first one,
    "Production vs Light Replica", contains `| Geocoding (Nominatim) | Dedicated 8 GB Postgres | ... |`
    — and reading the whole file invented a 12th core container called "Geocoding" worth 8 GB. The
    budget came out at 24.6 GiB against a 24 GiB box, so the guardrail refused a deploy that fits.
    """
    found: dict[str, dict] = {}
    inside = False

    with open(SPEC, encoding="utf-8") as handle:
        for line in handle:
            if line.startswith("## "):
                inside = line.strip().lower().startswith("## resource summary")
                continue

            if not inside or not line.startswith("|"):
                continue

            cells = [c.strip() for c in line.strip().strip("|").split("|")]
            if len(cells) < 3:
                continue

            name_cell, ram_cell = cells[0], cells[1]

            size = re.search(r"([\d.]+)\s*(GB|MB)\b", ram_cell)
            if not size:
                continue

            mib = float(size.group(1)) * (1024 if size.group(2).upper() == "GB" else 1)

            # `**Total (core 11)**`, and the header row, are not containers.
            bare = name_cell.replace("*", "").replace("`", "").strip()
            if bare.lower().startswith("total") or not bare:
                continue

            marked_optional = "optional" in name_cell.lower() or "batch" in name_cell.lower()
            container = bare.split()[0]

            found[container] = {
                "mib": mib,
                "optional": marked_optional,
                "elsewhere": container in ELSEWHERE,
            }

    return found


def totals() -> dict:
    table = rows()
    core = {k: v for k, v in table.items() if not v["optional"] and not v["elsewhere"]}
    optional_here = {
        k: v for k, v in table.items() if v["optional"] and not v["elsewhere"]
    }

    return {
        "core_mib": int(sum(v["mib"] for v in core.values())),
        "core_containers": sorted(core),
        "optional_mib": int(sum(v["mib"] for v in optional_here.values())),
        "optional_containers": sorted(optional_here),
        "elsewhere": sorted(k for k, v in table.items() if v["elsewhere"]),
        "rows": table,
    }


def rendered(compose: str) -> dict:
    """The compose file as docker resolves it, with placeholder secrets so `config` succeeds."""
    environment = dict(os.environ)
    environment.setdefault("MQTT_JWT_SECRET", "placeholder-for-config-render")
    environment.setdefault("MINIO_ROOT_PASSWORD", "placeholder-for-config-render")
    environment.setdefault("MINIO_KMS_SECRET_KEY", "placeholder-for-config-render")

    result = subprocess.run(
        ["docker", "compose", "-f", compose, "config", "--format", "json"],
        capture_output=True,
        text=True,
        env=environment,
        check=False,
    )

    if result.returncode != 0:
        tail = (result.stderr or "").strip().splitlines()
        raise SystemExit("compose-render-failed: " + (tail[-1] if tail else "unknown"))

    return json.loads(result.stdout)


def drift(compose: str) -> list[str]:
    """Where the compose file and the spec disagree about a container's memory."""
    table = rows()
    problems: list[str] = []

    for name, service in sorted(rendered(compose)["services"].items()):
        limit = (service.get("deploy") or {}).get("resources", {}).get("limits", {}).get("memory")

        if limit is None:
            # One-shots (migrate, the two init containers) are not steady state and the spec gives
            # them no row. A LONG-running container with no limit is a different matter: it can grow
            # into the whole box, which is what the budget exists to prevent.
            if service.get("restart") not in {"no", None} and not service.get("profiles"):
                problems.append(f"{name}: long-running with no memory limit")
            continue

        mib = int(limit) / (1024 * 1024)
        row = table.get(name)

        if row is None:
            problems.append(f"{name}: {mib:.0f} MiB in compose, no row in the spec's table")
        elif abs(row["mib"] - mib) > 1:
            problems.append(
                f"{name}: {mib:.0f} MiB in compose, {row['mib']:.0f} MiB in the spec")

    return problems


def main() -> int:
    if len(sys.argv) < 2:
        print(__doc__)
        return 2

    if sys.argv[1] == "totals":
        print(json.dumps(totals(), indent=1))
        return 0

    if sys.argv[1] == "drift":
        if len(sys.argv) < 3:
            print("usage: budget.py drift <compose-file>", file=sys.stderr)
            return 2
        problems = drift(sys.argv[2])
        for problem in problems:
            print(problem)
        return 0

    print(f"unknown command: {sys.argv[1]}", file=sys.stderr)
    return 2


if __name__ == "__main__":
    raise SystemExit(main())
