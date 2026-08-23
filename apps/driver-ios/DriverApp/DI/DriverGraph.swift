import Combine
import Foundation
import MageRideShared

/// The app's object graph: `:shared`'s Koin, plus the handful of things that are native by
/// construction.
///
/// **Why there is no Koin on the Swift side.** `Module.single`, `module { }` and `Koin.get` are all
/// `inline` + `reified`, and an inline reified function is not exported to Objective-C at all — so
/// Swift can neither build a Koin module nor resolve a definition from one. `:shared`'s
/// `startIosGraph` is the seam: the app passes **values**, Kotlin does the wiring, and
/// `IosAppGraph` hands back typed properties. What is native — the location manager, the MQTT
/// socket, connectivity, push routing — is constructed here, exactly as `driverAppModule` does on
/// Android for the Android half.
///
/// One instance, held by the `App`. Constructing a second would start a second Koin (which throws)
/// and a second session manager racing the first on the same single-use refresh token (D-29).
@MainActor
final class DriverGraph: ObservableObject {

    /// The five bindings `sharedModules` leaves to an app, resolved.
    let shared: IosAppGraph

    /// The build's own values. Read once — see ``DriverEnvironment``.
    let environment: DriverEnvironment

    /// Navigation state, held above the view tree so a push can reach it.
    let navigator = DriverNavigator()

    /// The offline banner's only input (US-15.6).
    let connectivity = ConnectivityMonitor()

    /// Where a `mageride://…` link becomes a destination.
    let pushes = PushRouter()

    /// APNs-via-FCM registration.
    let pushTokens = PushTokenProvider()

    /// The position plane: CLLocationManager in, CocoaMQTT out (R-17, D6' §3).
    let positions: PositionService

    // MARK: - C086 · cluster 1
    //
    // The first-run answers, the capture seam and the two repositories cluster 1 reads. They are
    // here rather than constructed per screen because each is a **process** singleton: the
    // preferences are read at launch to pick the interface language, and the capture coordinator is
    // the only thing that survives the trip to SCR-DI-005 and back.

    /// The three first-run answers, before there is a session to write them against.
    let preferences: OnboardingPreferences

    /// C014's session manager, as the five calls SCR-DI-003 makes.
    let sessions: DriverSessions

    /// SCR-DI-002's cities, and where its two answers end up.
    let onboarding: OnboardingRepository

    /// SCR-DI-003a's upload, and the splash's "has this driver a profile?".
    let profiles: DriverProfileRepository

    /// The seam between a capture slot and SCR-DI-005 (AL-43).
    let captures = DocumentCaptureCoordinator()

    /// SCR-DI-007's two rows.
    let permissions: DriverPermissions

    // MARK: - C087 · cluster 2
    //
    // The Mode-C wizard's data layer and the two holders its screens read across a navigation. Both
    // holders are **process** singletons for `DocumentCaptureCoordinator`'s reason: the screen that
    // set the value is not on screen while the screen that reads it is on top.

    /// SCR-DI-004…004c, SCR-DI-006 and SCR-DI-026's reads and writes (AL-27, AL-30).
    let vehicles: VehicleOnboardingRepository

    /// Which vehicle SCR-DI-006 is about — the route carries no argument.
    let vehicleSession = VehicleOnboardingSession()

    /// D-03's single active publisher, chosen on SCR-DI-026 and read by C088's dashboard.
    let activeVehicle: ActiveVehicleStore

    /// SCR-DI-005's camera grant and whether VisionKit can run on this device at all.
    let camera: CameraAuthoriser

    // MARK: - C088 · cluster 3
    //
    // The dashboard's data layer and the two things that outlive a screen. Everything else C088 owns
    // is a per-screen model, built by the `make…` factories below.

    /// Who the driver is and which vehicle is live (US-9.6, D-03).
    let identity: DriverIdentity

    /// dispatch-svc, wallet-svc, subscription-svc and query-svc, as SCR-DI-010 and SCR-DI-013 use them.
    let standby: StandbyRepository

    /// trip-state-svc's Mode A/B sessions, and **nothing else** (R-01).
    let journeys: JourneyRepository

    /// ride-svc's write surface as SCR-DI-015 uses it, plus AL-47's settlement pair.
    let rides: ActiveRideRepository

    /// The driver's single offer slot (ADD Appendix B.2 invariant 3).
    let offerSlot: OfferSlot

    /// **Where a `ride_offer` push becomes the live offer.** A process singleton, because an offer
    /// arrives with no view anywhere — see its own KDoc.
    let offers: OfferInbox

    /// voip-svc's call log and the CallKit-aware dialler.
    let contact: RideContact

    /// Turning ``positions`` on and off without a screen holding the service.
    let publisher: PositionPublisher

    // MARK: - C089 · the delivery
    //
    // ride-svc's package surface and P-10's photograph. The queue is a **process** singleton for
    // ``DocumentCaptureCoordinator``'s reason: SCR-DI-005 is a full-screen takeover, so the delivery
    // sheet is not on screen while the picture is being taken.

    /// ride-svc's package commands, as SCR-DI-016a/b/c uses them (AL-33).
    let deliveries: DeliveryRepository

    /// The delivery photograph, held until the server has it (P-10, §3.6).
    let proofs = ProofUploadQueue()

    // MARK: - C090 · the board, the level and the money
    //
    // Two repositories and no holder: every screen in this cluster is a read, and the one piece of
    // state that outlives a view — which board rows this driver has posted intent on — deliberately
    // does not (see ``JobBoardModel``, and the C072 handoff's spec gap 3).

    /// dispatch-svc's board, upcoming list, level and stats, as SCR-DI-017/018/019 use them.
    let jobs: JobsRepository

    /// query-svc's earnings read model, as SCR-DI-020 uses it (R-05).
    let earnings: EarningsRepository

    // MARK: - C091 · the wallet
    //
    // Three repositories and one preference. The preference is here rather than constructed per screen
    // because it is a **device** setting SCR-DI-021 both reads and writes, and a second store over the
    // same `UserDefaults` key would be two objects disagreeing about the driver's own threshold until
    // the next launch. The two system seams are held for the same reason C088 holds ``contact``: each
    // wraps a process-wide facility and neither has any per-screen state.

    /// wallet-svc's balance and ledger plus subscription-svc's fee table, as SCR-DI-021/025 use them.
    let walletReads: WalletRepository

    /// wallet-svc's driver-to-driver surface, as SCR-DI-023/024 use it (AL-01, AL-34).
    let creditTransfers: CreditTransferRepository

    /// The two top-up rails and the bulk-voucher ladder, as SCR-DI-022 uses them (AL-05, AL-15).
    let topUps: TopUpRepository

    /// US-9.9's driver-set low-balance line — **local, because no route stores one**.
    let walletPreferences: WalletPreferences

    /// AL-15's bank-app hand-off. OnePay is deliberately not behind it — see ``PaymentHandoff``.
    let payments: PaymentHandoff

    /// Where US-9A.19's statement lands before the share sheet picks it up.
    let statements: StatementExporter

    // MARK: - C092 · the tracker, the sharing, the profile and the history
    //
    // Three repositories and one store. The store is here rather than constructed per screen for
    // ``activeVehicle``'s reason and one more: it is read by ``publisher`` on **every** go-online, and a
    // second store over the same `UserDefaults` keys would be two objects disagreeing about which
    // vehicles have a tracker on them.

    /// Which vehicles a hardware tracker publishes for, as this handset knows it (US-3.6).
    let trackerBindings: TrackerBindingStore

    /// registry-svc's owner-facing device bind, as SCR-DI-027 uses it (T-02).
    let trackers: TrackerRepository

    /// registry-svc's grants and subscription-svc's request queue, as SCR-DI-028 uses them (AL-24).
    let sharing: SharingRepository

    /// `GET`/`PUT /v1/users/me`, the emergency contacts and the way out, as SCR-DI-029 uses them.
    ///
    /// Distinct from ``profiles``, which is C086's SCR-DI-003a **upload**; this one is the settings
    /// screen's read and write surface.
    let profileSettings: ProfileRepository

    /// query-svc's trips read model and the platform's one rating route, as SCR-DI-030 uses them.
    let history: RideHistoryRepository

    // MARK: - C093 · comms, safety, support and the system states
    //
    // Everything here is a **process** singleton and none of it could be built per screen. The
    // database is opened once and shared by three callers (see ``DriverDatabase``); the inbox is
    // written by the push delegate, which runs with no view anywhere — the same argument
    // ``OfferInbox`` makes; the CallKit provider must outlive SCR-DI-031 because the system can end
    // a call after the screen has gone; and the engine is a binding rather than a value, which is
    // the whole point of the seam.

    /// C018's `mageride_driver.db`, opened on first use. The position buffer, the alert inbox and
    /// SCR-DI-035's backlog count are three callers of one handle.
    let databases: DriverDatabase

    /// `mobile_db_schema.md` §1.6 — every push this handset saw, as SCR-DI-034 reads it.
    let alerts: NotificationInbox

    /// SCR-DI-035's *"128 queued"*, read from the table rather than from the live pipeline.
    let bufferedSamples: BufferedSampleCounter

    /// D6' §6's LiveKit room, behind a seam. **This build binds ``AbsentVoipEngine``** — read its
    /// documentation before concluding that SCR-DI-031's connected state is unreachable by accident.
    let voip: VoipEngine

    /// The system's idea that this app has a call up (D2' §SCR-DI-031, *"**iOS** CallKit"*).
    let calls: CallSession

    /// support-svc's FAQ and ticket queue plus the trips SCR-DI-033a attaches to.
    let support: SupportRepository

    init(environment: DriverEnvironment = .current) {
        self.environment = environment

        // `AppSurface.driver` is the `app` claim AL-08 scopes the session by. Getting it wrong does
        // not fail loudly — it signs the driver in as a passenger and revokes the session they
        // wanted. `MageRideApp.driver` is the other half: `mobile_db_schema.md` §0.2 gives each app
        // its own file, and this one physically cannot open the passenger tables.
        let config = IosAppConfig(
            baseUrl: environment.apiBaseUrl,
            appVersion: environment.appVersion,
            userAgent: environment.userAgent,
            debug: environment.isDebug,
            mqttHost: environment.mqttHost,
            mqttPort: environment.mqttPort,
            mqttTls: environment.mqttTls,
            surface: .driver,
            app: .driver,
            keychainService: environment.keychainService
        )

        let shared = IosAppGraphKt.startIosGraph(config: config)
        self.shared = shared

        // C093. Built first because ``PositionService`` takes it: the database handle is the one
        // thing three separate planes share, and constructing it here is what makes "one connection
        // to one protected file" a property of the graph rather than a convention three callers have
        // to remember. **Nothing is opened yet** — see ``DriverDatabase``.
        let databases = DriverDatabase(factory: shared.databases)
        self.databases = databases

        let positions = PositionService(graph: shared, connectivity: connectivity, databases: databases)
        self.positions = positions

        let preferences = UserDefaultsOnboardingPreferences()
        self.preferences = preferences
        let sessions = SharedDriverSessions(sessions: shared.sessions)
        self.sessions = sessions
        self.onboarding = ApiOnboardingRepository(
            content: shared.api.content,
            iam: shared.api.iam,
            preferences: preferences
        )
        self.profiles = ApiDriverProfileRepository(registry: shared.api.registry, iam: shared.api.iam)
        self.permissions = SystemDriverPermissions(pushTokens: pushTokens)
        let vehicles = ApiVehicleOnboardingRepository(registry: shared.api.registry)
        let activeVehicle = UserDefaultsActiveVehicleStore()
        self.vehicles = vehicles
        self.activeVehicle = activeVehicle
        self.camera = SystemCameraAuthoriser()

        // C088. `GET /v1/vehicles/mine` is read through the repository C087 already owns rather than
        // through a second `RegistryApi` here, so `VehicleSummary.canGoLive` — US-9.6's rule — has one
        // home on this platform.
        self.identity = ApiDriverIdentity(sessions: sessions, vehicles: vehicles, activeVehicle: activeVehicle)
        self.standby = ApiStandbyRepository(
            dispatch: shared.api.dispatch,
            wallet: shared.api.wallet,
            subscription: shared.api.subscription,
            query: shared.api.query,
            iam: shared.api.iam
        )
        self.journeys = ApiJourneyRepository(
            tripState: shared.api.tripState,
            transit: shared.api.transit,
            preferences: UserDefaultsJourneyPreferences()
        )
        self.rides = ApiActiveRideRepository(ride: shared.api.ride, fare: shared.api.fare)
        self.offerSlot = SharedOfferSlot(offers: shared.offers, states: shared.offerStates)
        self.offers = OfferInbox(offers: shared.offers, sessions: sessions)

        // C093 folded safety-svc in here rather than giving SCR-DI-032 a repository of its own: the
        // alarm is raised from the same sheet as the call button, it is *about* the same ride, and
        // `POST /v1/sos` is the only safety operation this app reaches. One seam, two services.
        self.contact = SystemRideContact(voip: shared.api.voip, safety: shared.api.safety)

        // **C092's fence, and it is wired here because there is nowhere else it could close all three
        // doors.** SCR-DI-010's go-online toggle, SCR-DI-011's Start Journey and US-5.10's Restart all
        // reach the position service through this one property, so decorating it once is US-3.6's
        // *"exactly one publisher at a time"* for every caller — see ``TrackerPositionPublisher``.
        // There is exactly ONE binding of `PositionPublisher` in this graph; the decorator replaced the
        // bare `ServicePositionPublisher` rather than being added beside it.
        let trackerBindings = UserDefaultsTrackerBindingStore()
        self.trackerBindings = trackerBindings
        self.publisher = TrackerPositionPublisher(
            delegate: ServicePositionPublisher(positions: positions),
            bindings: trackerBindings
        )
        self.deliveries = ApiDeliveryRepository(ride: shared.api.ride)

        // C090. dispatch-svc's board and reputation reads are one repository because the US-6A.8
        // gate joins them — see ``JobsRepository``. The dashboard's own level badge still comes
        // through ``StandbyRepository``: that is one field of a five-field status header, and asking
        // for the stats read as well to draw a badge would be a round trip a dashboard never uses.
        let jobs = ApiJobsRepository(dispatch: shared.api.dispatch)
        self.jobs = jobs
        self.earnings = ApiEarningsRepository(query: shared.api.query)

        // C091. Three repositories over two services, split by what a screen asks rather than by which
        // client answers: SCR-DI-021/025 read money, SCR-DI-023/024 move it between drivers, and
        // SCR-DI-022 buys it. The wallet reads and the top-up rails both touch wallet-svc **and**
        // subscription-svc — the fee table and the voucher purchase are the second service's — which is
        // why neither is a thin wrapper over one client.
        self.walletReads = ApiWalletRepository(wallet: shared.api.wallet, subscription: shared.api.subscription)
        self.creditTransfers = ApiCreditTransferRepository(wallet: shared.api.wallet)
        self.topUps = ApiTopUpRepository(wallet: shared.api.wallet, subscription: shared.api.subscription)
        self.walletPreferences = UserDefaultsWalletPreferences()
        self.payments = SystemPaymentHandoff()
        self.statements = FileStatementExporter()

        // C092. The tracker bind is registry's owner-facing wrapper and sharing is registry **and**
        // subscription, because accepting a request creates the grant and starts the subscription in
        // one transaction (AL-24). The profile reads the level through C090's `jobs` rather than a
        // second `GET /v1/drivers/{id}/level`, which is what stops two screens disagreeing about a
        // driver's level.
        self.trackers = ApiTrackerRepository(registry: shared.api.registry, bindings: trackerBindings)
        self.sharing = ApiSharingRepository(
            registry: shared.api.registry,
            subscription: shared.api.subscription
        )
        self.profileSettings = ApiProfileRepository(
            iam: shared.api.iam,
            jobs: jobs,
            sessions: sessions,
            preferences: preferences,
            // Δ MCS-25 — the avatar comes from registry-svc, because Profile Setup writes
            // `registry.driver_profiles` and never `iam.users`, so the photo on `GET /v1/users/me`
            // is nil for every driver who onboarded in this app.
            registry: shared.api.registry,
            // Δ MCS-27 — §3.16, so both headers paint before a call is made.
            databases: databases
        )
        self.history = ApiRideHistoryRepository(
            query: shared.api.query,
            ride: shared.api.ride,
            tripState: shared.api.tripState
        )

        // C093. The handle itself is built at the top of this initialiser, because
        // ``PositionService`` takes it; these are the three collaborators over it.
        self.alerts = LocalNotificationInbox(databases: databases)
        self.bufferedSamples = BufferedSampleCounter(databases: databases, vehicles: activeVehicle)
        self.voip = AbsentVoipEngine()
        self.calls = CallKitSession()
        self.support = ApiSupportRepository(support: shared.api.support, query: shared.api.query)

        // Before the first frame, so a driver who chose සිංහල never sees an English one. This is
        // the earliest point at which it can happen — `DriverLocale` redirects the bundle every
        // lookup goes through, and a view built before it would have resolved its strings already.
        DriverLocale.applyStored(preferences)
    }

    // MARK: - C088 · the per-screen models
    //
    // A factory rather than a property: each of these is a `@StateObject` owned by the screen that
    // shows it, and a model held here would outlive the view, keep its GNSS subscription open and hand
    // the *next* driver the last one's dashboard. `DriverLocationSource` is constructed per model for
    // the same reason — it is a screen's subscription, not a shift's.
    //
    // `OfferInbox`, `OfferSlot` and the repositories above are the opposite and are properties, because
    // each genuinely outlives every view: an offer arrives with no composition anywhere.

    /// SCR-DI-010 / SCR-DI-011.
    func makeHomeModel() -> HomeModel {
        HomeModel(
            identity: identity,
            standby: standby,
            journeys: journeys,
            rides: rides,
            location: CoreLocationDriverLocationSource(),
            publisher: publisher
        )
    }

    /// SCR-DI-014 — the takeover Home hosts.
    func makeOfferModel() -> OfferModel {
        OfferModel(slot: offerSlot, rides: rides)
    }

    /// SCR-DI-013.
    func makeDirectionalModel() -> DirectionalModel {
        DirectionalModel(standby: standby, location: CoreLocationDriverLocationSource())
    }

    /// SCR-DI-015.
    func makeActiveRideModel(rideId: String) -> ActiveRideModel {
        ActiveRideModel(
            rideId: rideId,
            rides: rides,
            contact: contact,
            location: CoreLocationDriverLocationSource()
        )
    }

    /// SCR-DI-016a/b/c — the three sheets SCR-DI-015 hands a **package** ride over to (C089).
    func makeDeliveryModel(rideId: String) -> DeliveryModel {
        DeliveryModel(
            rideId: rideId,
            deliveries: deliveries,
            contact: contact,
            location: CoreLocationDriverLocationSource(),
            proofs: proofs,
            captures: captures
        )
    }

    // MARK: - C090 · the per-screen models
    //
    // Factories for the reason C088's are: each is a `@StateObject` owned by the screen that shows
    // it, and a model held here would outlive the view and keep its ticker — and, on the board, its
    // GNSS subscription — running for the life of the process.

    /// SCR-DI-017.
    func makeJobBoardModel() -> JobBoardModel {
        JobBoardModel(identity: identity, jobs: jobs, location: CoreLocationDriverLocationSource())
    }

    /// SCR-DI-018.
    func makeScheduledRidesModel() -> ScheduledRidesModel {
        ScheduledRidesModel(identity: identity, jobs: jobs)
    }

    /// SCR-DI-019.
    func makeDriverLevelModel() -> DriverLevelModel {
        DriverLevelModel(identity: identity, jobs: jobs)
    }

    /// SCR-DI-020.
    func makeEarningsModel() -> EarningsModel {
        EarningsModel(identity: identity, earnings: earnings)
    }

    // MARK: - C091 · the per-screen models
    //
    // Factories for the reason C088's and C090's are: each is a `@StateObject` owned by the screen that
    // shows it. SCR-DI-022's would be the worst one to hold — its model owns D6' §7.1's poll, and one
    // that outlived the screen would keep re-reading a gateway session for the life of the process.

    /// SCR-DI-021.
    func makeWalletModel() -> WalletModel {
        WalletModel(identity: identity, wallet: walletReads, preferences: walletPreferences)
    }

    /// SCR-DI-022.
    func makeTopUpModel() -> TopUpModel {
        TopUpModel(topUps: topUps, handoff: payments)
    }

    /// SCR-DI-023.
    func makeRequestCreditModel() -> RequestCreditModel {
        RequestCreditModel(identity: identity, transfers: creditTransfers)
    }

    /// SCR-DI-024.
    func makeCreditTransferModel() -> CreditTransferModel {
        CreditTransferModel(identity: identity, transfers: creditTransfers, wallet: walletReads)
    }

    /// SCR-DI-025.
    func makeWalletHistoryModel() -> WalletHistoryModel {
        WalletHistoryModel(identity: identity, wallet: walletReads, exporter: statements)
    }

    // MARK: - C092 · the per-screen models
    //
    // Factories for the reason every other cluster's are: each is a `@StateObject` owned by the screen
    // that shows it. SCR-DI-030's would be the worst one to hold — its model fans out a detail read per
    // trip on `refresh()`, and one that outlived the screen would keep a page of twenty results for the
    // life of the process.

    /// SCR-DI-027.
    func makeTrackerPairingModel() -> TrackerPairingModel {
        TrackerPairingModel(identity: identity, trackers: trackers, publisher: publisher, camera: camera)
    }

    /// SCR-DI-028.
    func makeSharingModel() -> SharingModel {
        SharingModel(identity: identity, sharing: sharing)
    }

    /// SCR-DI-029.
    func makeDriverProfileModel() -> DriverProfileModel {
        DriverProfileModel(identity: identity, profiles: profileSettings)
    }

    /// SCR-DI-036's header (Δ MCS-24). The same two dependencies SCR-DI-029's model takes,
    /// because it shows the same block: the tab drew a generic label, which needed nothing, and
    /// now draws the driver, which needs both reads.
    func makeMenuModel() -> MenuModel {
        MenuModel(identity: identity, profiles: profileSettings)
    }

    /// SCR-DI-029a (Δ MCS-28) — the driver's own documents, cached under the §0.4 exception.
    func makeDocumentsModel() -> DocumentsModel {
        DocumentsModel(store: ApiDriverDocumentStore(registry: shared.api.registry, databases: databases))
    }

    /// SCR-DI-030.
    func makeRideHistoryModel() -> RideHistoryModel {
        RideHistoryModel(identity: identity, history: history)
    }

    // MARK: - C093 · the per-screen models
    //
    // Factories for the reason every other cluster's are: each is a `@StateObject` owned by the
    // screen that shows it. SCR-DI-031's and SCR-DI-032's would be the worst two to hold — one owns
    // a call timer and the other a three-second cancel window that raises an irrevocable alarm when
    // it reaches zero, and either running for the life of the process is the failure this rule
    // exists to prevent.

    /// SCR-DI-031 — the takeover SCR-DI-015's **Free call** opens.
    func makeVoipCallModel(rideId: String) -> VoipCallModel {
        VoipCallModel(rideId: rideId, rides: rides, contact: contact, engine: voip, session: calls)
    }

    /// SCR-DI-032 — the takeover SCR-DI-015's and SCR-DI-016's **SOS** open.
    ///
    /// Its own ``DriverLocationSource``, per model, for the reason C088 gives: the alarm's coordinate
    /// is a *screen's* subscription and not the publisher's, and this screen is reachable on a ride
    /// whose own source belongs to the screen underneath the takeover.
    func makeSosModel(rideId: String) -> SosModel {
        SosModel(
            rideId: rideId,
            contact: contact,
            profiles: profileSettings,
            location: CoreLocationDriverLocationSource()
        )
    }

    /// SCR-DI-033 / 033a.
    func makeSupportModel() -> SupportModel {
        SupportModel(identity: identity, support: support)
    }

    /// SCR-DI-034.
    func makeNotificationsModel() -> NotificationsModel {
        NotificationsModel(inbox: alerts)
    }

    /// Start-up work that outlives any view.
    ///
    /// Deliberately small. Anything that can wait for a screen waits for a screen — a driver on a
    /// five-year-old handset feels every millisecond before the first frame.
    func warmUp() {
        PushTokenProvider.configureIfAvailable()

        Task { [shared] in
            // C014's handoff asks for a warm-up so the first attested call does not pay the whole
            // preparation cost, and on iOS the first attested call is `POST /v1/auth/otp/request`.
            // The expensive half of App Attest is generating the Secure Enclave key, which
            // `attestationToken` does on first use and then keeps — so asking for one token against
            // the route the shell is about to call anyway both warms the key up and costs nothing
            // extra. It answers `nil` on a simulator and on any device without App Attest, which is
            // the fail-soft rule and not an error to report.
            _ = try? await shared.attestation.attestationToken(
                request: AttestationRequest(
                    operationId: "checkAppVersion",
                    method: "GET",
                    path: "/v1/version/check"
                )
            )
        }
    }
}
