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
        self.positions = PositionService(graph: shared, connectivity: connectivity)

        let preferences = UserDefaultsOnboardingPreferences()
        self.preferences = preferences
        self.sessions = SharedDriverSessions(sessions: shared.sessions)
        self.onboarding = ApiOnboardingRepository(
            content: shared.api.content,
            iam: shared.api.iam,
            preferences: preferences
        )
        self.profiles = ApiDriverProfileRepository(registry: shared.api.registry, iam: shared.api.iam)
        self.permissions = SystemDriverPermissions(pushTokens: pushTokens)
        self.vehicles = ApiVehicleOnboardingRepository(registry: shared.api.registry)
        self.activeVehicle = UserDefaultsActiveVehicleStore()
        self.camera = SystemCameraAuthoriser()

        // Before the first frame, so a driver who chose සිංහල never sees an English one. This is
        // the earliest point at which it can happen — `DriverLocale` redirects the bundle every
        // lookup goes through, and a view built before it would have resolved its strings already.
        DriverLocale.applyStored(preferences)
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
