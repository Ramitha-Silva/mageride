package lk.mageride.driver.di

import io.ktor.client.engine.HttpClientEngine
import io.ktor.client.engine.okhttp.OkHttp
import lk.mageride.driver.push.PushRouter
import lk.mageride.driver.shell.ConnectivityMonitor
import lk.mageride.shared.data.api.ApiConfig
import lk.mageride.shared.data.api.AttestationProvider
import lk.mageride.shared.data.models.AppSurface
import lk.mageride.shared.db.DatabaseDriverFactory
import lk.mageride.shared.db.MageRideApp
import lk.mageride.shared.db.PlatformDatabaseDriverFactory
import lk.mageride.shared.domain.auth.AuthConfig
import lk.mageride.shared.mqtt.MqttConfig
import lk.mageride.shared.platform.PlatformAttestationProvider
import lk.mageride.shared.platform.PlatformSecureStore
import lk.mageride.shared.platform.SecureStore
import org.koin.android.ext.koin.androidContext
import org.koin.core.module.Module
import org.koin.dsl.module

/**
 * What the Driver App adds to `:shared`'s graph.
 *
 * `sharedModules` resolves lazily and deliberately leaves five bindings to the app, because
 * `commonMain` cannot supply them — see the KDoc on `SharedModule.kt`, `apiModule`, `authModule`
 * and `localDbModule`. Those five are:
 *
 * | Binding | Why the app | Owner |
 * |---|---|---|
 * | [HttpClientEngine] | there is no multiplatform engine | C013 |
 * | [ApiConfig] | which gateway, which build | C013 |
 * | [AuthConfig] | which **surface** — AL-08 scopes a session by it | C014 |
 * | [SecureStore] | needs an Android `Context` | C014 |
 * | [DatabaseDriverFactory] | needs a `Context` *and* the app identity | C018 |
 *
 * Plus one C013 leaves at a fail-soft default until an app can configure it:
 * [AttestationProvider], which on Android is Play Integrity and needs the Play Console cloud
 * project number (D-30).
 *
 * **Order matters.** App modules come last in `initKoin(appModules = …)`, so every definition here
 * wins over the shared one. That is what makes the [AttestationProvider] swap a one-line change
 * rather than an edit inside `:shared`.
 */
internal fun driverAppModule(environment: DriverEnvironment = DriverEnvironment.fromBuildConfig()): Module = module {
    single { environment }

    // ---- C013 -------------------------------------------------------------------------
    // OkHttp rather than CIO or Android: it is the engine `:shared`'s own androidMain uses and
    // the one the e2e harness drives, so the app exercises the stack the contracts were tested
    // against rather than a second one.
    single<HttpClientEngine> { OkHttp.create() }
    single { environment.apiConfig() }

    // ---- C014 -------------------------------------------------------------------------
    // AppSurface.DRIVER is the `app` claim AL-08 scopes the session by. Getting it wrong does not
    // fail loudly — it signs the user in as a passenger and revokes the session they wanted.
    single { AuthConfig(app = AppSurface.DRIVER) }
    single<SecureStore> { PlatformSecureStore(androidContext(), get<AuthConfig>().storeNamespace) }

    // ---- D-30 -------------------------------------------------------------------------
    // Bound as the concrete type as well as the interface: the shell calls `warmUp()` at
    // start-up, and that method is Android's alone (it prepares the Play Integrity token
    // provider). `AttestationProvider` is what C013's request pipeline resolves.
    single { PlatformAttestationProvider(androidContext(), environment.integrityCloudProjectNumber) }
    single<AttestationProvider> { get<PlatformAttestationProvider>() }

    // ---- C018 -------------------------------------------------------------------------
    // `mobile_db_schema.md` §0.2: each app ships its own database file and its own table set.
    // This app is `MageRideApp.DRIVER` and physically cannot open the passenger tables.
    single { MageRideApp.DRIVER }
    single<DatabaseDriverFactory> { PlatformDatabaseDriverFactory(androidContext()) }

    // ---- C017 / D6' §3 ----------------------------------------------------------------
    // One config for the process. The socket itself belongs to the foreground service.
    single<MqttConfig> { environment.mqttConfig() }

    // ---- shell ------------------------------------------------------------------------
    single { ConnectivityMonitor(androidContext()) }
    single { PushRouter() }
}
