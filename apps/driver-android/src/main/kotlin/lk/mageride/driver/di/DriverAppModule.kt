package lk.mageride.driver.di

import io.ktor.client.engine.HttpClientEngine
import io.ktor.client.engine.okhttp.OkHttp
import lk.mageride.driver.capture.DocumentCaptureCoordinator
import lk.mageride.driver.capture.DocumentScannerViewModel
import lk.mageride.driver.delivery.DeliveryRepository
import lk.mageride.driver.delivery.DeliveryViewModel
import lk.mageride.driver.delivery.ProofUploadQueue
import lk.mageride.driver.earnings.EarningsRepository
import lk.mageride.driver.earnings.EarningsViewModel
import lk.mageride.driver.home.AndroidJourneyPreferences
import lk.mageride.driver.home.DirectionalViewModel
import lk.mageride.driver.home.DriverIdentity
import lk.mageride.driver.home.HomeViewModel
import lk.mageride.driver.home.JourneyPreferences
import lk.mageride.driver.home.JourneyRepository
import lk.mageride.driver.home.OfferInbox
import lk.mageride.driver.home.OfferViewModel
import lk.mageride.driver.home.StandbyRepository
import lk.mageride.driver.jobs.JobBoardViewModel
import lk.mageride.driver.jobs.JobsRepository
import lk.mageride.driver.jobs.ScheduledRidesViewModel
import lk.mageride.driver.level.DriverLevelViewModel
import lk.mageride.driver.location.AndroidDriverLocationSource
import lk.mageride.driver.location.AndroidPositionPublisher
import lk.mageride.driver.location.DriverLocationSource
import lk.mageride.driver.location.PositionPublisher
import lk.mageride.driver.onboarding.AndroidOnboardingPreferences
import lk.mageride.driver.onboarding.DriverPermissions
import lk.mageride.driver.onboarding.DriverProfileRepository
import lk.mageride.driver.onboarding.LanguageCityViewModel
import lk.mageride.driver.onboarding.LoginViewModel
import lk.mageride.driver.onboarding.OnboardingPreferences
import lk.mageride.driver.onboarding.OnboardingRepository
import lk.mageride.driver.onboarding.ProfileSetupViewModel
import lk.mageride.driver.onboarding.SplashViewModel
import lk.mageride.driver.push.PushRouter
import lk.mageride.driver.push.PushTokenProvider
import lk.mageride.driver.ride.ActiveRideRepository
import lk.mageride.driver.ride.ActiveRideViewModel
import lk.mageride.driver.ride.RideContact
import lk.mageride.driver.shell.ConnectivityMonitor
import lk.mageride.driver.vehicle.ActiveVehicleStore
import lk.mageride.driver.vehicle.AndroidActiveVehicleStore
import lk.mageride.driver.vehicle.VehicleOnboardingRepository
import lk.mageride.driver.vehicle.VehicleOnboardingSession
import lk.mageride.driver.vehicle.VehicleOnboardingStatusViewModel
import lk.mageride.driver.vehicle.VehicleOnboardingViewModel
import lk.mageride.driver.vehicle.VehiclesViewModel
import lk.mageride.shared.data.api.ApiConfig
import lk.mageride.shared.data.api.AttestationProvider
import lk.mageride.shared.data.models.AppSurface
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.db.DatabaseDriverFactory
import lk.mageride.shared.db.MageRideApp
import lk.mageride.shared.db.PlatformDatabaseDriverFactory
import lk.mageride.shared.domain.auth.AuthConfig
import lk.mageride.shared.mqtt.MqttConfig
import lk.mageride.shared.platform.PlatformAttestationProvider
import lk.mageride.shared.platform.PlatformSecureStore
import lk.mageride.shared.platform.SecureStore
import org.koin.android.ext.koin.androidContext
import org.koin.androidx.viewmodel.dsl.viewModel
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
    single { PushTokenProvider() }

    // ---- C068 · auth / onboarding -----------------------------------------------------
    // The first-run answers are given before there is a session, so they live on the device and
    // are pushed to `iam.users` on the first authenticated call (AL-26 language, AL-27 city).
    single<OnboardingPreferences> { AndroidOnboardingPreferences(androidContext()) }
    single { DriverPermissions(androidContext()) }

    // The seam to SCR-DA-005 (C069). Process-wide because the scanner is a destination, not a
    // dialog: the screen that asked for the capture is not composed while it is on screen.
    single { DocumentCaptureCoordinator() }

    single { OnboardingRepository(content = get(), iam = get(), preferences = get()) }
    single { DriverProfileRepository(registry = get(), iam = get()) }

    viewModel { SplashViewModel(sessions = get(), profiles = get(), preferences = get()) }
    viewModel { LanguageCityViewModel(repository = get()) }
    viewModel {
        LoginViewModel(
            sessions = get(),
            onboarding = get(),
            profiles = get(),
            preferences = get(),
            pushTokens = get(),
        )
    }
    viewModel { ProfileSetupViewModel(profiles = get(), captures = get()) }

    // ---- C069 · vehicle onboarding ----------------------------------------------------
    // Which vehicle SCR-DA-006 is about, held process-wide for the same reason the capture
    // coordinator is: `DriverRoute.VehicleOnboardingStatus` carries no arguments, and the screen
    // that names the vehicle is not composed while the one that reads it is on top.
    single { VehicleOnboardingSession() }

    // D-03's single active publisher. Local by design — there is no "set my active vehicle"
    // operation on the platform, and there does not need to be: the MQTT username IS the vehicle
    // id, so the broker learns the choice on CONNECT. See ActiveVehicleStore.
    single<ActiveVehicleStore> { AndroidActiveVehicleStore(androidContext()) }

    single { VehicleOnboardingRepository(registry = get()) }

    viewModel { DocumentScannerViewModel(captures = get()) }
    viewModel { VehicleOnboardingViewModel(vehicles = get(), captures = get(), session = get()) }
    viewModel { VehicleOnboardingStatusViewModel(vehicles = get(), session = get()) }
    viewModel { VehiclesViewModel(vehicles = get(), session = get(), activeVehicle = get()) }

    dashboardBindings()
}

/**
 * The C070 slice — SCR-DA-010/011/013/014/015 and the Menu drawer.
 *
 * A function on [Module] rather than a second module for the same reason the rest of this file is
 * one module: `initKoin(appModules = …)` takes the app's graph as one unit, and a second entry
 * would be a second thing every future app-side edit has to remember to append to.
 */
private fun Module.dashboardBindings() {
    // The handset's own GNSS, for a screen. Distinct from `PositionForegroundService`, which owns
    // the fixes that reach the broker and outlives every composition — see DriverLocationSource.
    single<DriverLocationSource> { AndroidDriverLocationSource(androidContext()) }
    single<PositionPublisher> { AndroidPositionPublisher(androidContext()) }
    single<JourneyPreferences> { AndroidJourneyPreferences(androidContext()) }

    // E-01's offer arrives on a background thread with no composition anywhere, so the slot it
    // lands in has to outlive the screen that draws it. `OfferSession` is `:shared`'s single-offer
    // slot (ADD Appendix B.2 invariant 3) and is already a process singleton; this is the seam
    // from `DriverMessagingService` into it.
    single { OfferInbox(offers = get(), sessions = get()) }

    single { DriverIdentity(registry = get(), sessions = get(), activeVehicle = get()) }
    single { StandbyRepository(dispatch = get(), wallet = get(), subscription = get(), query = get()) }
    single { JourneyRepository(tripState = get(), transit = get(), preferences = get()) }
    single { ActiveRideRepository(ride = get(), fare = get()) }
    single { RideContact(voip = get(), safety = get()) }

    viewModel {
        HomeViewModel(
            identity = get(),
            standby = get(),
            journeys = get(),
            rides = get(),
            location = get(),
            publisher = get(),
        )
    }
    viewModel { OfferViewModel(offers = get(), rides = get()) }
    viewModel { DirectionalViewModel(standby = get(), query = get(), iam = get(), location = get()) }
    viewModel { (rideId: Ulid) ->
        ActiveRideViewModel(rideId = rideId, rides = get(), contact = get(), location = get())
    }

    deliveryBindings()
}

/**
 * The C071 slice — SCR-DA-016a/b/c, the three delivery sheets.
 *
 * [ProofUploadQueue] is a `single` for the reason [DocumentCaptureCoordinator] is: the photograph is
 * taken on a full-screen destination, so the composition that asked for it is not alive when it
 * comes back, and the entry has to outlive both.
 */
private fun Module.deliveryBindings() {
    single { ProofUploadQueue() }
    single { DeliveryRepository(ride = get()) }

    viewModel { (rideId: Ulid) ->
        DeliveryViewModel(
            rideId = rideId,
            deliveries = get(),
            contact = get(),
            location = get(),
            proofs = get(),
            captures = get(),
        )
    }

    jobsBindings()
}

/**
 * The C072 slice — SCR-DA-017/018/019 and SCR-DA-020.
 *
 * [JobsRepository] is one binding for three screens because they are one service and one gate: the
 * Job Board is only open to a driver `GET /v1/drivers/{id}/level` says is Level 2 or above (US-6A.8),
 * and that same read is what SCR-DA-019 draws. `JobBoard` itself is deliberately **not** bound —
 * it is a value type built from `LevelConfig`, and binding one at start-up would pin whatever the
 * admin's thresholds were when the app launched (the rule `fareWalletModule` and
 * `rideDispatchModule` already follow in `:shared`).
 */
private fun Module.jobsBindings() {
    single { JobsRepository(dispatch = get()) }
    single { EarningsRepository(query = get()) }

    viewModel { JobBoardViewModel(identity = get(), jobs = get(), location = get()) }
    viewModel { ScheduledRidesViewModel(identity = get(), jobs = get()) }
    viewModel { DriverLevelViewModel(identity = get(), jobs = get()) }
    viewModel { EarningsViewModel(identity = get(), earnings = get()) }
}
