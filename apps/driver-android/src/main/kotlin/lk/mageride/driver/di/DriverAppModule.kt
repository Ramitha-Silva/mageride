package lk.mageride.driver.di

import io.ktor.client.engine.HttpClientEngine
import io.ktor.client.engine.okhttp.OkHttp
import lk.mageride.driver.capture.DocumentCaptureCoordinator
import lk.mageride.driver.capture.DocumentScannerViewModel
import lk.mageride.driver.comms.AbsentVoipEngine
import lk.mageride.driver.comms.VoipCallViewModel
import lk.mageride.driver.comms.VoipEngine
import lk.mageride.driver.delivery.DeliveryRepository
import lk.mageride.driver.delivery.DeliveryViewModel
import lk.mageride.driver.delivery.ProofUploadQueue
import lk.mageride.driver.earnings.EarningsRepository
import lk.mageride.driver.earnings.EarningsViewModel
import lk.mageride.driver.history.RideHistoryRepository
import lk.mageride.driver.history.RideHistoryViewModel
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
import lk.mageride.driver.notifications.LocalNotificationInbox
import lk.mageride.driver.notifications.NotificationInbox
import lk.mageride.driver.notifications.NotificationsViewModel
import lk.mageride.driver.onboarding.AndroidOnboardingPreferences
import lk.mageride.driver.onboarding.DriverPermissions
import lk.mageride.driver.onboarding.DriverProfileRepository
import lk.mageride.driver.onboarding.LanguageCityViewModel
import lk.mageride.driver.onboarding.LoginViewModel
import lk.mageride.driver.onboarding.OnboardingPreferences
import lk.mageride.driver.onboarding.OnboardingRepository
import lk.mageride.driver.onboarding.ProfileSetupViewModel
import lk.mageride.driver.onboarding.SplashViewModel
import lk.mageride.driver.profile.DriverProfileViewModel
import lk.mageride.driver.profile.ProfileRepository
import lk.mageride.driver.push.PushRouter
import lk.mageride.driver.push.PushTokenProvider
import lk.mageride.driver.ride.ActiveRideRepository
import lk.mageride.driver.ride.ActiveRideViewModel
import lk.mageride.driver.ride.RideContact
import lk.mageride.driver.safety.SosViewModel
import lk.mageride.driver.sharing.SharingRepository
import lk.mageride.driver.sharing.SharingViewModel
import lk.mageride.driver.shell.BufferedSampleCounter
import lk.mageride.driver.shell.ConnectivityMonitor
import lk.mageride.driver.support.SupportRepository
import lk.mageride.driver.support.SupportViewModel
import lk.mageride.driver.tracker.AndroidTrackerBindingStore
import lk.mageride.driver.tracker.TrackerBindingStore
import lk.mageride.driver.tracker.TrackerPairingViewModel
import lk.mageride.driver.tracker.TrackerPositionPublisher
import lk.mageride.driver.tracker.TrackerRepository
import lk.mageride.driver.vehicle.ActiveVehicleStore
import lk.mageride.driver.vehicle.AndroidActiveVehicleStore
import lk.mageride.driver.vehicle.VehicleOnboardingRepository
import lk.mageride.driver.vehicle.VehicleOnboardingSession
import lk.mageride.driver.vehicle.VehicleOnboardingStatusViewModel
import lk.mageride.driver.vehicle.VehicleOnboardingViewModel
import lk.mageride.driver.vehicle.VehiclesViewModel
import lk.mageride.driver.wallet.AndroidPaymentHandoff
import lk.mageride.driver.wallet.AndroidStatementExporter
import lk.mageride.driver.wallet.AndroidWalletPreferences
import lk.mageride.driver.wallet.CreditTransferRepository
import lk.mageride.driver.wallet.CreditTransferViewModel
import lk.mageride.driver.wallet.PaymentHandoff
import lk.mageride.driver.wallet.RequestCreditViewModel
import lk.mageride.driver.wallet.StatementExporter
import lk.mageride.driver.wallet.TopUpRepository
import lk.mageride.driver.wallet.TopUpViewModel
import lk.mageride.driver.wallet.WalletHistoryViewModel
import lk.mageride.driver.wallet.WalletPreferences
import lk.mageride.driver.wallet.WalletRepository
import lk.mageride.driver.wallet.WalletViewModel
import lk.mageride.shared.data.api.ApiConfig
import lk.mageride.shared.data.api.AttestationProvider
import lk.mageride.shared.data.api.query.AppLanguage
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

    // The open database C018 deliberately leaves to the app, deferred so the Keystore round trip
    // is not on `Application.onCreate` (Δ C075). One handle for the position service, SCR-DA-034's
    // inbox and SCR-DA-035's backlog count — see `DriverDatabase`.
    single { DriverDatabase(factory = get()) }

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

    // D-26 — the language every geocode is read in. A supplier rather than a value: the graph is a
    // `single` and outlives the `recreate()` a language change performs, so a snapshot taken here
    // would pin the language the app opened in. `:shared`'s `LocalisedQueryApi` picks this up,
    // which is why SCR-DA-013 passes no `lang` and a later screen cannot forget to.
    single<AppLanguage> { AppLanguage { get<OnboardingPreferences>().language } }
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
    single<JourneyPreferences> { AndroidJourneyPreferences(androidContext()) }
    // `PositionPublisher` is bound in `trackerAndProfileBindings`, not here (Δ C074): the interface
    // has one definition and it is the tracker-aware decorator, so nothing depends on which of two
    // declarations of the same type Koin happens to keep.

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

    walletBindings()
}

/**
 * The C073 slice — SCR-DA-021…025.
 *
 * **Three repositories rather than one**, split by the service pair each screen talks to: the
 * balance and the ledger are wallet-svc plus subscription-svc's fee reads, the top-up rails are
 * wallet-svc plus subscription-svc's voucher purchase, and the transfers are wallet-svc alone. One
 * class would have been eighteen methods and would have put the voucher-versus-top-up distinction
 * — which is the easiest thing on this screen group to get wrong — inside a `when`.
 *
 * [PaymentHandoff] and [StatementExporter] are singles for the reason `PositionPublisher` is: both
 * need a `Context` to leave the app, and a view model that held one could not be run on this host.
 * `TopupMethod`, `VoucherCatalogue` and `DailyFeeSchedule` are deliberately **not** bound — every
 * one is built from admin-tunable config that has just been read, which is the rule `fareWalletModule`
 * states in `:shared` and the reason it binds nothing at all.
 */
private fun Module.walletBindings() {
    // US-9.9's "driver-set threshold". Local because no route stores one — see WalletPreferences.
    single<WalletPreferences> { AndroidWalletPreferences(androidContext()) }
    single<PaymentHandoff> { AndroidPaymentHandoff(androidContext()) }
    single<StatementExporter> { AndroidStatementExporter(androidContext()) }

    single { WalletRepository(wallet = get(), subscription = get()) }
    single { TopUpRepository(wallet = get(), subscription = get()) }
    single { CreditTransferRepository(wallet = get()) }

    viewModel { WalletViewModel(identity = get(), wallet = get(), preferences = get()) }
    viewModel { TopUpViewModel(topUps = get(), handoff = get()) }
    viewModel { RequestCreditViewModel(identity = get(), transfers = get()) }
    viewModel { CreditTransferViewModel(identity = get(), transfers = get(), wallet = get()) }
    viewModel { WalletHistoryViewModel(identity = get(), wallet = get(), exporter = get()) }

    trackerAndProfileBindings()
}

/**
 * The C074 slice — SCR-DA-027/028/029/030.
 *
 * **The one binding here that is not a screen's is `PositionPublisher`.** [TrackerPositionPublisher]
 * wraps the Android one and refuses to start the phone's stream for a vehicle that has a hardware
 * tracker bound to it (US-3.6, and the C074 fence). C070's `dashboardBindings` used to declare the
 * interface; that line moved here rather than being *overridden* here, so the graph has exactly one
 * definition of the type and nothing rests on which of two Koin would have kept. The decorator asks
 * for `AndroidPositionPublisher` **by class**, which is what makes it a decorator rather than a
 * recursion into itself.
 *
 * [TrackerBindingStore] is a `single` because it is what that decorator reads on every go-online,
 * and `SharingRepository` is one class over two services for the reason its KDoc gives: the
 * entitlement is registry-svc's and the request queue is subscription-svc's, and the screen is
 * about one vehicle's sharing rather than about either service.
 */
private fun Module.trackerAndProfileBindings() {
    single<TrackerBindingStore> { AndroidTrackerBindingStore(androidContext()) }
    single { AndroidPositionPublisher(androidContext()) }
    single<PositionPublisher> {
        TrackerPositionPublisher(delegate = get<AndroidPositionPublisher>(), bindings = get())
    }

    single { TrackerRepository(registry = get(), bindings = get()) }
    single { SharingRepository(registry = get(), subscription = get()) }
    single { ProfileRepository(iam = get(), jobs = get(), sessions = get(), preferences = get()) }
    single { RideHistoryRepository(query = get(), ride = get(), tripState = get()) }

    viewModel { TrackerPairingViewModel(identity = get(), trackers = get(), publisher = get()) }
    viewModel { SharingViewModel(identity = get(), sharing = get()) }
    viewModel { DriverProfileViewModel(identity = get(), profiles = get()) }
    viewModel { RideHistoryViewModel(identity = get(), history = get()) }

    commsSafetySupportBindings()
}

/**
 * The C075 slice — SCR-DA-031/032/033/033a/034/035.
 *
 * **[VoipEngine] is the one binding here that is a seam rather than a screen's**, and the
 * implementation bound is [AbsentVoipEngine]: this build carries no WebRTC client, because
 * `io.livekit:livekit-android` resolves `audioswitch` from JitPack and this repository's repository
 * set is `google()` + `mavenCentral()` with `FAIL_ON_PROJECT_REPOS`. Read that class's KDoc before
 * changing this line — it is the whole of what SCR-DA-031 can and cannot do, and swapping it is the
 * only edit landing the real engine needs on this side.
 *
 * [NotificationInbox] and [BufferedSampleCounter] are `single`s because both hold the process-wide
 * [DriverDatabase] handle: two instances would be two callers of one `suspend` open, which is
 * exactly what that class's mutex exists to make harmless — but a second instance would also mean a
 * second `DriverAlert` cache and no reason for one.
 *
 * `SosViewModel` and `VoipCallViewModel` take a `rideId` parameter for the reason
 * `ActiveRideViewModel` does: both routes are parameterised, and the alarm and the call are about a
 * specific trip.
 */
private fun Module.commsSafetySupportBindings() {
    single<VoipEngine> { AbsentVoipEngine() }
    single<NotificationInbox> { LocalNotificationInbox(databases = get()) }
    single { BufferedSampleCounter(databases = get(), vehicles = get()) }
    single { SupportRepository(support = get(), query = get()) }

    viewModel { (rideId: Ulid) ->
        VoipCallViewModel(rideId = rideId, rides = get(), contact = get(), engine = get())
    }
    viewModel { (rideId: Ulid) ->
        SosViewModel(rideId = rideId, contact = get(), profiles = get(), location = get())
    }
    viewModel { SupportViewModel(identity = get(), support = get()) }
    viewModel { NotificationsViewModel(inbox = get()) }
}
