package lk.mageride.passenger.di

import io.ktor.client.engine.HttpClientEngine
import io.ktor.client.engine.okhttp.OkHttp
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import lk.mageride.passenger.booking.ApiBookingRepository
import lk.mageride.passenger.booking.BookingDraft
import lk.mageride.passenger.booking.BookingRepository
import lk.mageride.passenger.booking.PackageBookingViewModel
import lk.mageride.passenger.booking.PasteLinkViewModel
import lk.mageride.passenger.booking.ProxyRiderViewModel
import lk.mageride.passenger.booking.RideBookingViewModel
import lk.mageride.passenger.booking.ScheduleRideViewModel
import lk.mageride.passenger.history.ApiHistoryRepository
import lk.mageride.passenger.history.HistoryRepository
import lk.mageride.passenger.history.PackageOtps
import lk.mageride.passenger.home.LiveMapViewModel
import lk.mageride.passenger.home.LocalRecentPlaces
import lk.mageride.passenger.home.RecentPlaces
import lk.mageride.passenger.home.SearchLocationViewModel
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
import lk.mageride.passenger.ride.ApiRideRepository
import lk.mageride.passenger.ride.CallChoice
import lk.mageride.passenger.ride.RideRepository
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
        liveMapScreenBindings()
        bookingBindings()
        activeRideBindings()
        historyBindings()
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
 * The C078 slice — SCR-PA-006/007/008/010/032.
 *
 * One seam, and it is the local one: [RecentPlaces] is `mobile_db_schema.md` §2.2's
 * `place_recents`, which is a table rather than a route because nothing about a passenger's recent
 * searches is sent anywhere. Everything else this cluster needs — the live plane, the location
 * source, two query-svc routes and one iam route — already has a binding.
 *
 * The filter is **not** bound. It is a value type held in view-model state, and a `single` would
 * pin whatever a passenger had switched on when the app launched.
 */
private fun Module.liveMapScreenBindings() {
    single<RecentPlaces> { LocalRecentPlaces(databases = get()) }

    viewModel {
        LiveMapViewModel(live = get(), locations = get(), iam = get(), query = get(), recents = get())
    }
    viewModel { SearchLocationViewModel(query = get(), iam = get(), recents = get()) }
}

/**
 * The C079 slice — SCR-PA-009/010b/011/012/012a/013.
 *
 * **[BookingDraft] is a `single` and that is the design.** Six screens edit one booking — a
 * destination on SCR-PA-008, a tier on SCR-PA-009, a rider on SCR-PA-010b, a parcel on SCR-PA-012,
 * a time on SCR-PA-013 and a payment method on C080's SCR-PA-016 — and the alternatives are worse:
 * nav arguments would put a rider's phone number in a back-stack URL, and a view model per screen
 * with no shared home would lose everything downstream the moment somebody went back to change the
 * pickup. Its own KDoc argues the case; [BookingDraft.clear] is what stops it outliving a booking.
 *
 * **[ConfirmPickupViewModel] is deliberately absent.** It takes a `requestId` from the navigation
 * argument, and Koin's `parametersOf` for a view model would put a runtime cast between the route
 * and the screen. The NavHost builds it directly — see `PassengerNavHost`.
 */
private fun Module.bookingBindings() {
    single { BookingDraft() }
    single<BookingRepository> {
        ApiBookingRepository(
            transit = get(),
            fare = get(),
            rides = get(),
            dispatch = get(),
            query = get(),
            iam = get(),
        )
    }

    viewModel { RideBookingViewModel(draft = get(), bookings = get(), keys = get()) }
    viewModel { ProxyRiderViewModel(draft = get(), bookings = get(), live = get()) }
    viewModel { PackageBookingViewModel(draft = get(), bookings = get(), keys = get(), otps = get()) }
    viewModel { ScheduleRideViewModel(draft = get(), bookings = get()) }
    viewModel { PasteLinkViewModel(bookings = get()) }
}

/**
 * The C080 slice — SCR-PA-014…019.
 *
 * **Every view model here takes a `rideId` from a navigation argument, so none of them is a
 * `viewModel { }`.** Koin's `parametersOf` would put a runtime cast between the route and the
 * screen for nothing; the NavHost builds them directly from these two singles. [CallChoice] is a
 * `single` because the remembered call type outlives every ride.
 */
private fun Module.activeRideBindings() {
    single<RideRepository> {
        ApiRideRepository(rides = get(), fares = get(), wallets = get(), safety = get(), comms = get())
    }
    single { CallChoice(preferences = get()) }
}

/**
 * The C081 slice — SCR-PA-020…023.
 *
 * [PackageOtps] is a `single` and holds the two handover codes for the life of the process, because
 * **neither can be read back from the platform** — the pickup code is returned once at booking and
 * the delivery code arrives only on a push. Its own KDoc argues the case; the C081 handoff records
 * it as two contract gaps.
 */
private fun Module.historyBindings() {
    single<HistoryRepository> { ApiHistoryRepository(rides = get(), query = get(), dispatch = get()) }
    single { PackageOtps() }
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
