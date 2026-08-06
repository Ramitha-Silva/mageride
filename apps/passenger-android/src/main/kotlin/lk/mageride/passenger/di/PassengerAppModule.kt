package lk.mageride.passenger.di

import io.ktor.client.engine.HttpClientEngine
import io.ktor.client.engine.okhttp.OkHttp
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import lk.mageride.passenger.live.LiveHubTransport
import lk.mageride.passenger.live.PassengerLiveMap
import lk.mageride.passenger.live.SignalRLiveHubTransport
import lk.mageride.passenger.location.AndroidPassengerLocationSource
import lk.mageride.passenger.location.PassengerLocationSource
import lk.mageride.passenger.onboarding.LocationPermission
import lk.mageride.passenger.onboarding.LoginViewModel
import lk.mageride.passenger.onboarding.OnboardingRepository
import lk.mageride.passenger.onboarding.OnboardingViewModel
import lk.mageride.passenger.onboarding.PassengerProfileRepository
import lk.mageride.passenger.onboarding.ProfileSetupViewModel
import lk.mageride.passenger.onboarding.SplashViewModel
import lk.mageride.passenger.push.PushRouter
import lk.mageride.passenger.push.PushTokenProvider
import lk.mageride.passenger.shell.AndroidAppPreferences
import lk.mageride.passenger.shell.AppPreferences
import lk.mageride.passenger.shell.ConnectivityMonitor
import lk.mageride.shared.data.api.ApiConfig
import lk.mageride.shared.data.api.AttestationProvider
import lk.mageride.shared.data.models.AppSurface
import lk.mageride.shared.db.DatabaseDriverFactory
import lk.mageride.shared.db.MageRideApp
import lk.mageride.shared.db.PlatformDatabaseDriverFactory
import lk.mageride.shared.domain.auth.AuthConfig
import lk.mageride.shared.platform.PlatformAttestationProvider
import lk.mageride.shared.platform.PlatformSecureStore
import lk.mageride.shared.platform.SecureStore
import org.koin.android.ext.koin.androidContext
import org.koin.androidx.viewmodel.dsl.viewModel
import org.koin.core.module.Module
import org.koin.dsl.module

/**
 * What the Passenger App adds to `:shared`'s graph.
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
internal fun passengerAppModule(environment: PassengerEnvironment = PassengerEnvironment.fromBuildConfig()): Module =
    module {
        single { environment }

        // ---- C013 -------------------------------------------------------------------------
        // OkHttp rather than CIO or Android: it is the engine `:shared`'s own androidMain uses and
        // the one the e2e harness drives, so the app exercises the stack the contracts were tested
        // against rather than a second one. It is also what the SignalR client uses underneath, so
        // the two planes share one connection pool.
        single<HttpClientEngine> { OkHttp.create() }
        single { environment.apiConfig() }

        // ---- C014 -------------------------------------------------------------------------
        // AppSurface.PASSENGER is the `app` claim AL-08 scopes the session by. Getting it wrong does
        // not fail loudly — it signs the user in as a driver and revokes the session they wanted.
        single { AuthConfig(app = AppSurface.PASSENGER) }
        single<SecureStore> { PlatformSecureStore(androidContext(), get<AuthConfig>().storeNamespace) }

        // ---- D-30 -------------------------------------------------------------------------
        // Bound as the concrete type as well as the interface: `PassengerApplication` calls
        // `warmUp()` at start-up, and that method is Android's alone (it prepares the Play Integrity
        // token provider). `AttestationProvider` is what C013's request pipeline resolves.
        single { PlatformAttestationProvider(androidContext(), environment.integrityCloudProjectNumber) }
        single<AttestationProvider> { get<PlatformAttestationProvider>() }

        // ---- C018 -------------------------------------------------------------------------
        // `mobile_db_schema.md` §0.2: each app ships its own database file and its own table set.
        // This app is `MageRideApp.PASSENGER` and physically cannot open the driver tables.
        single { MageRideApp.PASSENGER }
        single<DatabaseDriverFactory> { PlatformDatabaseDriverFactory(androidContext()) }
        single { PassengerDatabase(factory = get()) }

        // ---- shell ------------------------------------------------------------------------
        single { ConnectivityMonitor(androidContext()) }
        single { PushRouter() }
        single { PushTokenProvider() }
        single<AppPreferences> { AndroidAppPreferences(androidContext()) }
        single<PassengerLocationSource> { AndroidPassengerLocationSource(androidContext()) }

        liveMapBindings(environment)
        onboardingBindings()
    }

/**
 * The C077 slice — SCR-PA-001…005.
 *
 * Two repositories over three services, split by what each screen actually needs rather than by
 * service: [OnboardingRepository] is the carousel plus the language answer that cannot be sent
 * until there is a session, and [PassengerProfileRepository] is `iam.users` as the first-run
 * cluster reads and writes it. [LocationPermission] is a `single` because it needs a `Context`
 * and a view model that held one could not be run on this host.
 *
 * `OnboardingViewModel` is passed to its screen explicitly rather than resolved inside it, so it
 * is scoped to the NavHost's destination entry: half of what it does is compare the chosen
 * language against the one the screen was **entered** in, and an instance rebuilt mid-screen would
 * read back the value it had just written.
 */
private fun Module.onboardingBindings() {
    single { LocationPermission(androidContext()) }
    single { OnboardingRepository(content = get(), iam = get(), preferences = get()) }
    single { PassengerProfileRepository(iam = get()) }

    viewModel { SplashViewModel(sessions = get(), profiles = get(), rides = get(), preferences = get()) }
    viewModel { OnboardingViewModel(onboarding = get()) }
    viewModel {
        LoginViewModel(
            sessions = get(),
            onboarding = get(),
            profiles = get(),
            preferences = get(),
            pushTokens = get(),
        )
    }
    viewModel { ProfileSetupViewModel(profiles = get(), preferences = get()) }
}

/**
 * The real-time plane — D6' §5.
 *
 * **One connection for the process, and one scope that outlives every screen.** SCR-PA-015 is
 * watching a ride while SCR-PA-010's map is still subscribed to nineteen cells, and a socket owned
 * by a composition would be torn down and re-dialled on every navigation between them — each
 * re-dial costing a fresh handshake, a rejoin of all nineteen groups and a `/v1/nearby` read.
 * `SupervisorJob` on [Dispatchers.Default] because the work here is decoding and set arithmetic;
 * the blocking socket calls move themselves to IO inside [SignalRLiveHubTransport].
 *
 * The scope is never cancelled — its lifetime is the process's, exactly like the connection's.
 * `PassengerLiveMap.disconnect()` is what ends the connection when the passenger signs out.
 */
private fun Module.liveMapBindings(environment: PassengerEnvironment) {
    single<LiveHubTransport> {
        SignalRLiveHubTransport(baseUrl = environment.apiBaseUrl, tokens = get())
    }

    single {
        PassengerLiveMap(
            transport = get(),
            query = get(),
            // `:shared`'s `geoRealtimeModule` binds this to `com.uber:h3` on Android. The ids have
            // to be bit-identical to `position-processor-svc`'s or the client joins `cell:{h3index}`
            // groups nothing publishes to — which looks exactly like an empty map with no error.
            grid = get(),
            scope = CoroutineScope(SupervisorJob() + Dispatchers.Default),
        )
    }
}
