import Combine
import Foundation
import MageRideShared

/// The app's object graph: `:shared`'s Koin, plus the handful of things that are native by
/// construction.
///
/// **Why there is no Koin on the Swift side.** `Module.single`, `module { }` and `Koin.get` are all
/// `inline` + `reified`, and an inline reified function is not exported to Objective-C at all — so
/// Swift can neither build a Koin module nor resolve a definition from one. `:shared`'s
/// `startIosGraphWithH3` is the seam: the app passes **values**, Kotlin does the wiring, and
/// `IosAppGraph` hands back typed properties. What is native — the location manager, the SignalR
/// socket, connectivity, push routing, the H3 engine — is constructed here, exactly as
/// `passengerAppModule` does on Android for the Android half.
///
/// One instance, held by the `App`. Constructing a second would start a second Koin (which throws)
/// and a second session manager racing the first on the same single-use refresh token (D-29).
@MainActor
final class PassengerGraph: ObservableObject {

    /// The bindings `sharedModules` leaves to an app, resolved.
    let shared: IosAppGraph

    /// The build's own values. Read once — see ``PassengerEnvironment``.
    let environment: PassengerEnvironment

    /// Navigation state, held above the view tree so a push can reach it.
    let navigator = PassengerNavigator()

    /// SCR-PI-032's banner input (US-15.6).
    let connectivity = ConnectivityMonitor()

    /// Where a `mageride://…` link becomes a destination.
    let pushes = PushRouter()

    /// APNs-via-FCM registration. SCR-PI-005 asks; the delegate hands over the device token.
    let pushTokens = PushTokenProvider()

    /// SCR-PI-002's answers and C101's default rail, before and after there is a session.
    let preferences: AppPreferences

    /// R-06's geocell engine, over the vendored H3 C library.
    ///
    /// Held as a property as well as passed to Koin because ``PassengerLiveMap`` takes it directly —
    /// resolving it back out of the graph would be a second path to one object.
    let h3: H3Grid

    /// The whole real-time plane: one `/hubs/live` connection for the process.
    ///
    /// **A property, not a per-screen object.** SCR-PI-015 watches a ride while SCR-PI-010's map is
    /// still subscribed to nineteen cells, and a socket owned by a view would be torn down and
    /// re-dialled on every navigation between them — each re-dial costing a fresh handshake, a
    /// rejoin of all nineteen groups and a `/v1/nearby` read.
    let live: PassengerLiveMap

    /// `GET /v1/nearby`, as the **one** thing in this app that reads it.
    ///
    /// Held here as well as handed to ``live`` because SCR-PI-007 needs the same read: D6' §5.4's
    /// resync and the popup's ETA/driver/plate are one operation asked twice, and a second seam onto
    /// it would be a second place the radius and the failure behaviour have to agree.
    let nearby: NearbySnapshots

    /// Where the passenger is. The R-06 anchor, MAP-02's halo and every *"current location"*.
    let locations: PassengerLocationSource

    /// The last fix any screen saw, for the ones that need one and must not subscribe for it
    /// (Δ C097). Written by ``RecordingLocationSource``, which wraps ``locations``.
    let lastFix: LastKnownFix

    /// C018's `mageride_passenger.db`, opened on first use. **Nothing is opened yet** — see
    /// ``PassengerDatabase``.
    let databases: PassengerDatabase

    // MARK: - C095 · cluster 1
    //
    // The first-run seams. Each is here rather than constructed per screen because each is a
    // **process** singleton: the preferences are read at launch to pick the interface language, the
    // session manager is C014's one door onto a token, and the carousel's response is cached across
    // a language change on the screen that fetched it.

    /// C014's session manager, as the five calls SCR-PI-003 makes.
    let sessions: PassengerSessions

    /// SCR-PI-002's carousel, and where its language answer ends up.
    let onboarding: OnboardingRepository

    /// `GET`/`PUT /v1/users/me` — SCR-PI-004's save, and the profile check the splash and the login
    /// screen each make. C101's SCR-PI-027 reads the same pair; reuse it rather than opening a
    /// second seam.
    let profiles: PassengerProfileRepository

    /// SCR-PI-005's one grant.
    let locationPermission: LocationPermission

    /// US-1.14's *"resume the ride you were on"*, which the splash asks and nothing else does.
    let activeRides: ActiveRideLookup

    // MARK: - C096 · cluster 2
    //
    // Both are process singletons for the same reason cluster 1's are: one is a **database
    // connection** and the other is the seam two screens read the same two operations through.

    /// `GET /v1/geo/search` and `GET /v1/me/saved-addresses` — SCR-PI-008's predictions and
    /// SCR-PI-010's ★ chips.
    let places: PassengerPlaces

    /// `mobile_db_schema.md` §2.2's `place_recents`. SCR-PI-008 writes it and SCR-PI-010 reads it,
    /// so it is one object over one connection — see ``PassengerDatabase``.
    let recents: RecentPlaces

    // MARK: - C097 · cluster 3
    //
    // A booking is assembled across six screens, so all four of these are process singletons: the
    // draft *is* the booking, the repository is one door onto six services, the key generator has to
    // answer the same value across a retry, and the OTP holder catches something that exists once.

    /// The booking being assembled. **One for the process** — see ``BookingDraft``.
    let draft: BookingDraft

    /// transit-svc, fare-svc, ride-svc, dispatch-svc, query-svc and iam, behind one door.
    let bookings: BookingRepository

    /// Where a `clientRequestId` comes from (R-18).
    let idempotencyKeys: IdempotencyKeys

    /// P-07's pickup code, caught on its way past. C099's SCR-PI-020 reads it.
    let packageOtps = PackageOtps()

    // MARK: - C098 · cluster 4
    //
    // The ride, its money, its call and its rating. Four of these are process singletons because
    // they hold something that has to outlive a screen — a remembered call type, a rail chosen on one
    // screen and read on the next, the database handle behind the rating queue — and the other three
    // are seams over system APIs a model test cannot hold.

    /// ride-svc, fare-svc, wallet-svc and comms-svc, behind one door.
    let rides: RideRepository

    /// SCR-PI-015a's memory (AL-48). A singleton because *"remembers last choice"* outlives the ride,
    /// and because C102's SCR-PI-028 offers the same chooser after a failed VoIP call.
    let callChoice: CallChoice

    /// The `tel:` dial, which on this platform **places** the call — see ``RideContact``.
    let rideContact: RideContact = SystemRideContact()

    /// AL-15's LankaQR link into the passenger's own bank app.
    let bankApp: BankAppHandoff = SystemBankAppHandoff()

    /// The camera grant and whether this handset can scan at all (AL-22).
    let camera: CameraAuthoriser = SystemCameraAuthoriser()

    /// The rail SCR-PI-016 confirmed, on its way to SCR-PI-017 — see ``PaymentSelection`` for why it
    /// cannot be a route argument.
    let paymentSelection = PaymentSelection()

    /// `mobile_db_schema.md` §1.11, as SCR-PI-019 writes it and SCR-PI-018 reads it.
    let ratings: RideRatings

    init(environment: PassengerEnvironment = .current) {
        self.environment = environment

        // `AppSurface.passenger` is the `app` claim AL-08 scopes the session by. Getting it wrong
        // does not fail loudly — it signs the passenger in as a driver and revokes the session they
        // wanted. `MageRideApp.passenger` is the other half: `mobile_db_schema.md` §0.2 gives each
        // app its own file, and this one physically cannot open the driver tables.
        //
        // The three MQTT values are the one part of `IosAppConfig` this app has no use for. D3'
        // §3.3 makes position ingest the driver's plane, so nothing here constructs a broker socket
        // and `MqttConfig` is built and never read. An empty host is the honest value: a real one
        // would be a connection string in a build that has nothing to connect with, and
        // `PassengerEnvironmentTests` asserts the plist carries no MQTT key at all.
        let config = IosAppConfig(
            baseUrl: environment.apiBaseUrl,
            appVersion: environment.appVersion,
            userAgent: environment.userAgent,
            debug: environment.isDebug,
            mqttHost: "",
            mqttPort: 8883,
            mqttTls: true,
            surface: .passenger,
            app: .passenger,
            keychainService: environment.keychainService
        )

        // R-06's engine, bound before the graph starts because `geoRealtimeModule`'s default
        // *throws* on resolution when the platform has none — see `SharedH3Grid`. This is the
        // binding C017 and C085 both left to this component.
        let h3 = SharedH3Grid()
        self.h3 = h3

        let shared = IosAppGraphKt.startIosGraphWithH3(config: config, h3Grid: h3)
        self.shared = shared

        let databases = PassengerDatabase(factory: shared.databases)
        self.databases = databases
        let preferences = UserDefaultsAppPreferences()
        self.preferences = preferences
        // Δ C097. Wrapped so every fix that reaches a screen also reaches `lastFix`, which is what
        // a booking's default pickup and SCR-PI-008's geocoder bias read. Built through a local so
        // the initialiser never reads a property it is still filling in.
        let lastFix = LastKnownFix()
        self.lastFix = lastFix
        self.locations = RecordingLocationSource(delegate: CoreLocationPassengerSource(), lastFix: lastFix)

        // C095. Two seams over three services, split by what a screen asks rather than by which
        // client answers: SCR-PI-002 reads content-svc and writes a language preference to iam-svc,
        // and everything else about `iam.users` is one repository because C101 edits the same row
        // from the other end of the app's life.
        self.sessions = SharedPassengerSessions(sessions: shared.sessions)
        self.onboarding = ApiOnboardingRepository(
            content: shared.api.content,
            iam: shared.api.iam,
            preferences: preferences
        )
        self.profiles = ApiPassengerProfileRepository(iam: shared.api.iam)
        self.locationPermission = SystemLocationPermission()
        self.activeRides = ApiActiveRideLookup(rides: shared.api.ride)

        // C096. The two screens of cluster 2 read three things between them, and each is shared
        // rather than per-screen: the snapshot seam is also the live plane's recovery read, the
        // places seam is read by both screens, and the recents table is one connection.
        let nearby = ApiNearbySnapshots(query: shared.api.query)
        self.nearby = nearby
        self.places = ApiPassengerPlaces(query: shared.api.query, iam: shared.api.iam)
        self.recents = LocalRecentPlaces(databases: databases)

        // C097. The draft reads the stored rail on every fresh booking, which is what makes C101's
        // *"pre-selected at booking/checkout"* true of the **next** one rather than of the next
        // launch.
        self.draft = BookingDraft(preferences: preferences, lastFix: lastFix)
        self.bookings = ApiBookingRepository(
            transit: shared.api.transit,
            fare: shared.api.fare,
            rides: shared.api.ride,
            dispatch: shared.api.dispatch,
            query: shared.api.query,
            iam: shared.api.iam
        )
        self.idempotencyKeys = SharedIdempotencyKeys(generator: shared.idempotencyKeys)

        // C098. One repository over four clients, one memory for the call chooser, and one door onto
        // §1.11 — which shares `databases` with C096's recents rather than opening a second
        // connection to the same protected file.
        self.rides = ApiRideRepository(
            rides: shared.api.ride,
            fares: shared.api.fare,
            wallets: shared.api.wallet,
            comms: shared.api.voip
        )
        self.callChoice = CallChoice(preferences: preferences)
        self.ratings = LocalRideRatings(databases: databases)

        self.live = PassengerLiveMap(
            transport: SignalRLiveHubTransport(
                baseUrl: environment.apiBaseUrl,
                tokens: shared.tokens
            ),
            snapshots: nearby,
            grid: h3
        )

        // Before the first frame, so a passenger who chose සිංහල never sees an English one. This is
        // the earliest point at which it can happen — `PassengerLocale` redirects the bundle every
        // lookup goes through, and a view built before it would have resolved its strings already.
        PassengerLocale.applyStored(preferences)
    }

    /// Start-up work that outlives any view.
    ///
    /// Deliberately small. Anything that can wait for a screen waits for a screen — a passenger on a
    /// five-year-old handset feels every millisecond before the first frame, and the live socket is
    /// started by the shell rather than here so that it is one subscription with one owner.
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
