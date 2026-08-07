package lk.mageride.shared.platform

import lk.mageride.shared.domain.geo.H3Grid

/**
 * iOS: none yet — the app supplies one.
 *
 * `com.uber:h3` is a JNI wrapper and ships no Kotlin/Native klib, so the iOS half needs a
 * `cinterop` binding against an H3 C library compiled for `ios-arm64` and
 * `ios-simulator-arm64`. That artefact can only be produced on macOS with Xcode, and this module
 * is developed and verified on a Linux host (root `CLAUDE.md`, "Build Host"), so the binding is
 * left to the iOS shell components rather than committed here unbuilt and untested.
 *
 * **The app binds one instead, and C094 has (Δ C094).** `shared/swiftpm/MageRideH3` vendors the same
 * reference C library `com.uber:h3` wraps, as an SPM target the Xcode build compiles from source;
 * the passenger app conforms a Swift type to [H3Grid] over it and passes the instance to
 * `startIosGraphWithH3`, which overrides `geoRealtimeModule`'s default. That route was taken in
 * preference to a `cinterop` binding because cinterop needs a static library built on macOS and
 * committed as a binary, which this host cannot produce and nobody could review.
 *
 * This function stays `null` rather than reaching for that package: `:shared` must not depend on an
 * app's Swift package, and the driver app deliberately binds nothing (AL-31's home map joins no
 * geocell group, so nothing in it resolves an [H3Grid] at all).
 *
 * **Everything else in `domain/geo`, `mqtt` and `realtime` is common code and runs unchanged on both
 * platforms**; what iOS was missing is the index arithmetic, not the rules.
 */
public actual fun platformH3Grid(): H3Grid? = null
