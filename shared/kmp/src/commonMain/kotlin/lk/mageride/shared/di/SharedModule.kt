package lk.mageride.shared.di

import kotlinx.serialization.json.Json
import lk.mageride.shared.data.api.apiModule
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
 * [lk.mageride.shared.data.api.ApiConfig]. Koin resolves lazily, so the graph starts without
 * them; the first HTTP call is where a missing one is reported.
 */
public val sharedModules: List<Module> = listOf(sharedCoreModule, apiModule)
