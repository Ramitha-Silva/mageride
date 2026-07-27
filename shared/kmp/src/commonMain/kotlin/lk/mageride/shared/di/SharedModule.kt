package lk.mageride.shared.di

import kotlinx.serialization.json.Json
import lk.mageride.shared.data.api.apiModule
import lk.mageride.shared.domain.auth.authModule
import lk.mageride.shared.domain.dispatch.rideDispatchModule
import lk.mageride.shared.domain.fare.fareWalletModule
import lk.mageride.shared.domain.geo.geoRealtimeModule
import lk.mageride.shared.platform.PlatformInfo
import lk.mageride.shared.platform.platformInfo
import lk.mageride.shared.serialization.MageRideJson
import org.koin.core.module.Module
import org.koin.dsl.module

/**
 * The shared module's own Koin graph.
 *
 * Each wave-1 component appends its own [Module] to [sharedModules] rather than growing this
 * one: C013 the `HttpClient`, C014 the session store, C015/C016 the domain use cases, C018
 * the SQLDelight database. Keeping one module per component means an app can be started with
 * a subset in tests, and a missing binding names the component that owns it.
 */
public val sharedCoreModule: Module = module {
    single<Json> { MageRideJson }
    single<PlatformInfo> { platformInfo() }
}

/**
 * Everything `:shared` contributes, in the order Koin should see it.
 *
 * The apps pass this to `startKoin { modules(sharedModules + appModules) }`; they must not
 * enumerate the individual modules, or a component added here would need an edit in all four
 * apps.
 *
 * [apiModule] (C013) needs two bindings only an app can provide — an
 * `io.ktor.client.engine.HttpClientEngine` and an
 * [lk.mageride.shared.data.api.ApiConfig]. [authModule] (C014) needs two more — an
 * [lk.mageride.shared.domain.auth.AuthConfig] and a
 * [lk.mageride.shared.platform.SecureStore]. Koin resolves lazily, so the graph starts without
 * them; the first HTTP call, or the first session read, is where a missing one is reported.
 *
 * [rideDispatchModule] (C015) needs nothing an app has to supply — its one binding,
 * `OfferSession`, resolves entirely out of C013. [fareWalletModule] (C016) binds nothing at all:
 * every money rule it owns is a value type built from server-supplied config, and holding one in
 * the graph would pin a tariff or a fee tier an operator has since moved. Its KDoc has the
 * reasoning.
 *
 * [geoRealtimeModule] (C017) binds one thing, an
 * [lk.mageride.shared.domain.geo.H3Grid]. Android resolves it from `com.uber:h3`; **an iOS app
 * must bind its own**, and because app modules come last, that binding wins. Nothing else in the
 * geo, MQTT or SignalR surface is in the graph — same reasoning as C016.
 *
 * **Order matters here.** `authModule` comes after `apiModule` and overrides its
 * [lk.mageride.shared.data.api.TokenProvider] placeholder with the real session-backed one.
 */
public val sharedModules: List<Module> =
    listOf(sharedCoreModule, apiModule, authModule, rideDispatchModule, fareWalletModule, geoRealtimeModule)
