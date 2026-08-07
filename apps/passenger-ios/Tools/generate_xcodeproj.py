#!/usr/bin/env python3
"""Writes `PassengerApp.xcodeproj/project.pbxproj` from the source tree.

    python3 apps/passenger-ios/Tools/generate_xcodeproj.py

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

PASSENGER_APP = AppProject(
    root=pathlib.Path(__file__).resolve().parents[1],
    app_target="PassengerApp",
    bundle_id="lk.mageride.passenger",
    packages=[
        # The KMP shared module. The SAME local package the driver app references — C085's decision
        # (6), and the reason is the artefact path: a second package would be a second place it has
        # to be right, and the path is the one thing about that file that can be wrong.
        {"kind": "local", "path": "../../shared/swiftpm/MageRideShared", "products": ["MageRideShared"]},
        # R-06's geocell engine. Local, and vendored — `uber/h3` ships no `Package.swift`, so it
        # cannot be a remote reference, and a `cinterop` binding would need a static library built
        # on macOS and committed as a binary. See `shared/swiftpm/MageRideH3/VENDOR.md`.
        {"kind": "local", "path": "../../shared/swiftpm/MageRideH3", "products": ["MageRideH3"]},
        # D2' §C's map row. The `-distribution` repository is MapLibre's own prebuilt XCFramework;
        # building the renderer from source would add a C++ toolchain to every CI run. The same
        # version the driver app pins — one cartography, one renderer.
        {
            "kind": "remote",
            "url": "https://github.com/maplibre/maplibre-gl-native-distribution",
            "requirement": ("upToNextMajorVersion", "6.0.0"),
            "products": ["MapLibre"],
        },
        # D6' §5 — *"Client = SignalR Java client (Android) / SignalR Swift client (iOS)"*. This is
        # that client: the reference Swift implementation, and the only maintained one. It is where
        # the driver app has CocoaMQTT, and the split is D3' §3.3's — this app has no broker.
        {
            "kind": "remote",
            "url": "https://github.com/moozzyk/SignalR-Client-Swift",
            "requirement": ("upToNextMajorVersion", "1.2.0"),
            "products": ["SignalRClient"],
        },
        # D2' §C's push row: "APNs via FCM". Only the Messaging product — Firestore and the rest
        # would drag gRPC into a build that has no use for it.
        {
            "kind": "remote",
            "url": "https://github.com/firebase/firebase-ios-sdk",
            "requirement": ("upToNextMajorVersion", "11.0.0"),
            "products": ["FirebaseMessaging"],
        },
    ],
    app_products=["MageRideShared", "MageRideH3", "MapLibre", "SignalRClient", "FirebaseMessaging"],
)


if __name__ == "__main__":
    raise SystemExit(generate(PASSENGER_APP))
