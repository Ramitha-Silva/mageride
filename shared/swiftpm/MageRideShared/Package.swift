// swift-tools-version: 5.9

import PackageDescription

// The KMP shared module as a Swift package.
//
// D7' §6 fixes the pipeline: `./gradlew :shared:assembleXCFramework` → `xcodebuild`, "via
// CocoaPods/SPM". This is the SPM half — a local package with a single binary target pointing at
// the artefact that task produces.
//
// **The XCFramework must exist before this package resolves.** SPM validates a local
// `binaryTarget` path at load time, so `xcodebuild` on a tree that has never run
// `:shared:assembleXCFramework` fails with "artifact not found" rather than with a link error
// later. That is the right failure and it is the order `.github/workflows/ci.yml` already uses:
// the iOS leg assembles the framework and then builds each app.
//
// **One package, both apps.** C085 (driver) and C094 (passenger) reference this same directory
// rather than each carrying a copy — a second package would be a second place the artefact path
// has to be right, and the path is the one thing about this file that can be wrong.
//
// The framework is **static** (`isStatic = true` in `shared/kmp/build.gradle.kts`), so nothing has
// to be embedded or re-signed by either Xcode project, and SwiftUI previews work — which they do
// not with a dynamic KMP framework.
let package = Package(
    name: "MageRideShared",
    platforms: [
        // Matches `IPHONEOS_DEPLOYMENT_TARGET` in `apps/*-ios/Config/Shared.xcconfig`. A package
        // floor above the app's would fail to resolve; one below would let a symbol through that
        // the app cannot run.
        .iOS(.v16),
    ],
    products: [
        .library(name: "MageRideShared", targets: ["MageRideShared"]),
    ],
    targets: [
        .binaryTarget(
            name: "MageRideShared",
            // Relative to this file. `XCFramework("MageRideShared")` writes the release artefact
            // here; the Debug variant is beside it under `debug/` and is deliberately not used —
            // an app built against a debug Kotlin/Native binary is several times slower on the
            // position pipeline, which is the one hot path in this app.
            path: "../../kmp/build/XCFrameworks/release/MageRideShared.xcframework"
        ),
    ]
)
