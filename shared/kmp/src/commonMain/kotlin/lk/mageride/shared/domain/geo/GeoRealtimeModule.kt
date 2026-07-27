package lk.mageride.shared.domain.geo

import lk.mageride.shared.platform.platformH3Grid
import org.koin.core.module.Module
import org.koin.dsl.module

/**
 * C017's Koin module: one binding, and deliberately only one.
 *
 * **[H3Grid] is the only thing here that an app cannot build for itself.** Everything else this
 * component owns is either a stateless rule ([GeoCells], `MqttTopics`, `LiveHub`,
 * `distanceMetres`) or a value type whose whole content comes from configuration the client has
 * just read — `AdaptiveRateConfig` from the admin cadence settings, `MqttConfig` from the
 * environment, `GeoCellSubscription` and `PositionReplayQueue` from the vehicle the foreground
 * service is currently publishing for. Binding any of those as a singleton would pin whatever the
 * numbers were when the app launched, which is the mistake C015 and C016 both call out at length.
 *
 * **The default resolves lazily and can be overridden.** On Android it is `com.uber:h3`. On iOS
 * `platformH3Grid()` answers `null` until the app supplies its own — see
 * [lk.mageride.shared.platform.platformH3Grid] — and because app modules are appended after
 * `sharedModules`, an app-level `single<H3Grid> { … }` replaces this one. Koin builds it on first
 * use, so an app that never opens a map never loads the native library and never sees the error.
 */
public val geoRealtimeModule: Module = module {
    single<H3Grid> {
        platformH3Grid() ?: throw H3GridUnavailableException(
            "No H3Grid is bound. Android provides one through com.uber:h3; an iOS app must bind " +
                "an H3Grid in its own Koin module (see PlatformH3Grid.ios.kt and the C017 handoff).",
        )
    }
}
