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
        let positions = PositionService(graph: shared, connectivity: connectivity)
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
        self.contact = SystemRideContact(voip: shared.api.voip)
        self.publisher = ServicePositionPublisher(positions: positions)
        self.deliveries = ApiDeliveryRepository(ride: shared.api.ride)

        // C090. dispatch-svc's board and reputation reads are one repository because the US-6A.8
        // gate joins them — see ``JobsRepository``. The dashboard's own level badge still comes
        // through ``StandbyRepository``: that is one field of a five-field status header, and asking
        // for the stats read as well to draw a badge would be a round trip a dashboard never uses.
        self.jobs = ApiJobsRepository(dispatch: shared.api.dispatch)
        self.earnings = ApiEarningsRepository(query: shared.api.query)

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
