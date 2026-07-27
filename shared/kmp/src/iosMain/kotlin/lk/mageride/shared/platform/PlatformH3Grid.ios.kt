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
 * Until then C094 (passenger) / C085 (driver) bind an
 * [lk.mageride.shared.domain.geo.H3Grid] in their own Koin module — four methods over a Swift H3
 * package — and it overrides `geoRealtimeModule`'s default. **Everything else in `domain/geo`,
 * `mqtt` and `realtime` is common code and runs unchanged on both platforms**; what iOS is missing
 * is the index arithmetic, not the rules.
 */
public actual fun platformH3Grid(): H3Grid? = null
