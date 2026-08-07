#!/usr/bin/env python3
"""Writes `DriverApp.xcodeproj/project.pbxproj` from the source tree.

    python3 apps/driver-ios/Tools/generate_xcodeproj.py

Run it from anywhere; paths are resolved relative to this file.

**The generator itself is `shared/tools/generate_xcodeproj.py`** (Δ C094): there are two iOS apps and
it is the same program for both, so what lives here is only what differs — the target name, the
bundle id and the Swift packages this app links. Read that file's header for how the `.pbxproj` is
derived, why it is generated at all, and what to do after adding a source file.
"""

from __future__ import annotations

import pathlib
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[3] / "shared" / "tools"))

from generate_xcodeproj import AppProject, generate  # noqa: E402

DRIVER_APP = AppProject(
    root=pathlib.Path(__file__).resolve().parents[1],
    app_target="DriverApp",
    bundle_id="lk.mageride.driver",
    packages=[
        # The KMP shared module. A LOCAL package, so the artefact `:shared:assembleXCFramework`
        # produces is consumed straight out of the build directory — see shared/swiftpm/MageRideShared.
        {"kind": "local", "path": "../../shared/swiftpm/MageRideShared", "products": ["MageRideShared"]},
        # D2' §C's map row. The `-distribution` repository is MapLibre's own prebuilt XCFramework;
        # building the renderer from source would add a C++ toolchain to every CI run.
        {
            "kind": "remote",
            "url": "https://github.com/maplibre/maplibre-gl-native-distribution",
            "requirement": ("upToNextMajorVersion", "6.0.0"),
            "products": ["MapLibre"],
        },
        # D2' §C's MQTT row. EMQX's own client, which is also what the broker is.
        {
            "kind": "remote",
            "url": "https://github.com/emqx/CocoaMQTT",
            "requirement": ("upToNextMajorVersion", "2.1.0"),
            "products": ["CocoaMQTT"],
        },
        # D2' §C's push row: "APNs via FCM". Only the Messaging product — Firestore and the rest would
        # drag gRPC into a build that has no use for it.
        {
            "kind": "remote",
            "url": "https://github.com/firebase/firebase-ios-sdk",
            "requirement": ("upToNextMajorVersion", "11.0.0"),
            "products": ["FirebaseMessaging"],
        },
    ],
    app_products=["MageRideShared", "MapLibre", "CocoaMQTT", "FirebaseMessaging"],
)


if __name__ == "__main__":
    raise SystemExit(generate(DRIVER_APP))
