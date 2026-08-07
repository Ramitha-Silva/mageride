// swift-tools-version: 5.9

import PackageDescription

// The H3 geocell engine for iOS, as a Swift package.
//
// **Why this exists at all.** `:shared`'s `H3Grid` is deliberately an interface rather than a
// Kotlin implementation: a client's cell ids have to be *bit-identical* to the ones
// `position-processor-svc` computes, or a passenger joins `cell:{h3index}` groups nothing publishes
// to — a failure that looks exactly like an empty map with no error anywhere. Android binds
// `com.uber:h3` (JNI over this same C library, catalogue version 4.4.0) and the backend binds
// `pocketken.H3`; `platformH3Grid()` answers `null` on iOS, and C017 and C085 both record that the
// iOS shell has to supply one. This is that engine.
//
// **Why the reference C library rather than a Swift port.** The grid is defined by constant tables —
// twenty icosahedron face centres, 122 base cells with their neighbours and rotations, and the
// deleted-subsequence rules for the twelve pentagons. A re-derived grid that is subtly wrong fails
// *silently*: every id is well-formed, every group name is plausible, and the map is simply empty.
// So the index arithmetic is the canonical implementation, vendored, and nothing here reimplements
// any of it — see `VENDOR.md` for the provenance and `Sources/MageRideH3/H3.swift` for the whole of
// what this package adds.
//
// **Why vendored rather than a package dependency.** `uber/h3` ships no `Package.swift`, so it
// cannot be referenced as a remote SPM package. A `cinterop` binding from Kotlin/Native (C017's
// suggestion) would need a static library built for `ios-arm64` and `ios-simulator-arm64` and
// committed as a binary — which cannot be produced on this Linux build host and could not be
// reviewed once it was. An SPM C target compiles the same sources from source as part of the app
// build, on whichever platform is building, and is reviewable as text.
let package = Package(
    name: "MageRideH3",
    platforms: [
        // Matches `IPHONEOS_DEPLOYMENT_TARGET` in `apps/*-ios/Config/Shared.xcconfig`.
        .iOS(.v16),
    ],
    products: [
        .library(name: "MageRideH3", targets: ["MageRideH3"]),
    ],
    targets: [
        // The vendored C. `include/` holds every header — public and private alike — because the
        // library's own sources include them by bare name (`#include "baseCells.h"`) and SPM only
        // adds `publicHeadersPath` to the header search path. Exposing the private headers to
        // importers is harmless: nothing outside this package imports `CH3` (see `MageRideH3`).
        .target(
            name: "CH3",
            path: "Sources/CH3",
            publicHeadersPath: "include",
            cSettings: [
                .headerSearchPath("include"),
                // The four definitions H3's own CMakeLists sets for a normal build.
                // `H3_PREFIX` is empty upstream (`set(H3_PREFIX "" …)`), so the exported symbols
                // keep their documented names; `BUILDING_H3` selects the library-internal half of
                // `h3api.h`; clang supports both `alloca` and variable-length arrays on every
                // Apple platform, and H3 falls back to slower paths without them.
                .define("H3_PREFIX", to: ""),
                .define("BUILDING_H3", to: "1"),
                .define("H3_HAVE_ALLOCA"),
                .define("H3_HAVE_VLA"),
            ]
        ),
        // The façade. Four functions, no state, no opinions — see its own documentation.
        .target(name: "MageRideH3", dependencies: ["CH3"], path: "Sources/MageRideH3"),
        .testTarget(name: "MageRideH3Tests", dependencies: ["MageRideH3"], path: "Tests/MageRideH3Tests"),
    ]
)
