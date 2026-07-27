package lk.mageride.shared.platform

import lk.mageride.shared.domain.geo.H3Grid

/**
 * The platform's own H3 implementation, or `null` when it has none.
 *
 * H3 cell ids have to be bit-identical to the ones the backend computes — see [H3Grid] — so this
 * is the canonical C library on each platform rather than a re-derivation:
 *
 * - **Android** returns a grid backed by `com.uber:h3` (JNI over the reference implementation).
 * - **iOS** returns `null`. Kotlin/Native needs a `cinterop` binding against an H3 built for
 *   `ios-arm64` / `ios-simulator-arm64`, which cannot be produced on the Linux build host this
 *   module is developed on, so the iOS app (C094/C085) binds its own [H3Grid] — one line of Koin
 *   over a Swift H3 package — exactly as it already binds an `HttpClientEngine` (C013) and a
 *   `SecureStore` (C014). See the C017 handoff in `build/progress.md`.
 *
 * Consumers should take [H3Grid] from Koin rather than calling this: `geoRealtimeModule` binds it,
 * and an app-supplied binding overrides this default.
 */
public expect fun platformH3Grid(): H3Grid?
