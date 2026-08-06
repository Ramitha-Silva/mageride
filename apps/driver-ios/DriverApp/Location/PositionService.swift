import Combine
import CoreLocation
import Foundation
import MageRideShared
import UIKit

/// The Driver App's position publisher — the iOS half of D6' §3.
///
/// **Why this shape.** Android needs a `ForegroundService` because an Android process with no
/// visible component stops receiving fixes; iOS needs no service object at all, but it needs three
/// specific things or the same failure appears in a different disguise:
///
/// 1. **`.authorizedAlways`.** `whenInUse` stops delivering the moment the app leaves the
///    foreground, and a driver's phone is in a mount with the screen off for most of a ride.
/// 2. **`allowsBackgroundLocationUpdates = true`** plus the `location` background mode. Without the
///    pair, iOS suspends the app on backgrounding and the socket dies with it. With it, the process
///    keeps running — which is what also keeps the CocoaMQTT connection alive, so the "background
///    task" D2' §C names for MQTT is really this: the location mode is what buys the runtime, and
///    the MQTT client rides on it.
/// 3. **`showsBackgroundLocationIndicator = true`.** The blue pill is iOS's version of the Android
///    ongoing notification — the driver-visible proof that position is being shared. It is on by
///    choice: hiding it while publishing a driver's location all shift would be the wrong default
///    even where the platform allows it.
///
/// **What it does not decide.** Every rule lives in `:shared` and is called from here:
/// `IosPositionPipeline` for the cadence, the durable `seq` and the replay pacing; `IosMqttPlan` for
/// the session; ``CocoaMqttTransport`` for the socket. This class is the CoreLocation lifecycle, the
/// fix source and the reconnect loop, and nothing else.
///
/// **The cadence is enforced on receipt, not on registration** — the one real difference from the
/// Android service. `CLLocationManager` has no update interval: it delivers on distance and on its
/// own schedule. So the manager is registered once at navigation accuracy with no distance filter,
/// and `IosPositionPipeline.onFix` rejects anything the D5' §5.2 phase table says is too soon. The
/// rule is identical; only who applies it moves.
@MainActor
final class PositionService: NSObject, ObservableObject {

    /// How many samples are waiting to be replayed (R-17). C088's dashboard shows this.
    @Published private(set) var bufferedCount: Int64 = 0

    /// Whether the broker link is up. Distinct from ``ConnectivityMonitor``: a handset can have a
    /// network and no MQTT session, which is precisely the state a driver needs to see.
    @Published private(set) var isPublishing = false

    /// The vehicle being published for, or `nil` when the app is not publishing.
    @Published private(set) var vehicleId: String?

    private let graph: IosAppGraph
    private let connectivity: ConnectivityMonitor
    private let manager = CLLocationManager()
    private let transport = CocoaMqttTransport()

    private var pipeline: IosPositionPipeline?
    private var tokens: FlowSubscription?
    private var drainTimer: Timer?
    private var backoff = IosReconnectBackoff()
    private var reconnectTask: Task<Void, Never>?
    private var backgroundTask: UIBackgroundTaskIdentifier = .invalid

    init(graph: IosAppGraph, connectivity: ConnectivityMonitor) {
        self.graph = graph
        self.connectivity = connectivity
        super.init()

        manager.delegate = self
        manager.desiredAccuracy = kCLLocationAccuracyBestForNavigation
        manager.distanceFilter = kCLDistanceFilterNone
        // Pausing looks like a battery win and is a correctness loss: iOS decides on its own that a
        // stationary vehicle needs no updates, and a driver waiting at a rank would drop off the map
        // until they moved. R-15's "stale" state exists for a vehicle that has genuinely stopped
        // reporting, not for one the OS quietly muted.
        manager.pausesLocationUpdatesAutomatically = false
        manager.activityType = .automotiveNavigation

        transport.onConnected = { [weak self] in
            Task { @MainActor in self?.onBrokerConnected() }
        }
        transport.onCommand = { [weak self] payload in
            Task { @MainActor in self?.onCommand(payload) }
        }
    }

    // MARK: - Lifecycle

    /// Starts publishing for [vehicleId]. Idempotent — a second call for the same vehicle is a no-op.
    ///
    /// The two doors that reach here are SCR-DI-010's go-online toggle and SCR-DI-011's Start
    /// Journey, both C088's. Going online is two calls in one order — `POST /v1/standby/online`
    /// **then** start publishing — because a driver in the candidate pool who is not publishing is
    /// offered rides that cannot find them.
    func start(vehicleId: String, mode: ServiceMode? = nil, vehicleType: VehicleType? = nil) async {
        guard self.vehicleId != vehicleId || pipeline == nil else { return }
        self.vehicleId = vehicleId

        // C018 deliberately binds no open database — opening it unwraps a key and is `suspend`, so
        // it happens here rather than at launch. One handle for the whole process; C093's alert
        // inbox and C088's buffered count share it rather than opening a second connection to one
        // protected file.
        guard let database = try? await graph.databases.openDriver(inMemory: false) else { return }

        pipeline = IosPositionPipeline(
            buffer: database.gpsBuffer(vehicleId: vehicleId),
            transport: transport,
            mode: mode,
            vehicleType: vehicleType
        )

        requestAuthorisation()
        startLocationUpdates()
        watchSessionToken()
        startDraining()
    }

    /// Stops publishing and announces `offline` on `veh/{vehicleId}/status`.
    func stop() {
        stopLocationUpdates()
        drainTimer?.invalidate()
        drainTimer = nil
        reconnectTask?.cancel()
        reconnectTask = nil
        tokens?.cancel()
        tokens = nil
        withBackgroundTime { [transport] in transport.disconnect() }
        pipeline = nil
        vehicleId = nil
        isPublishing = false
    }

    // MARK: - Authorisation

    /// Asks for Always, in the order iOS requires.
    ///
    /// `requestAlwaysAuthorization` on a fresh install shows the When-In-Use prompt first and the
    /// Always upgrade later, on the system's own schedule — there is no way to ask for Always
    /// directly, and an app that treats the intermediate state as a denial would send the driver to
    /// Settings for a permission iOS is about to offer. SCR-DI-007 (C086) owns the explanatory
    /// screen; this is the request behind it.
    private func requestAuthorisation() {
        switch manager.authorizationStatus {
        case .notDetermined:
            manager.requestAlwaysAuthorization()
        case .authorizedWhenInUse:
            manager.requestAlwaysAuthorization()
        default:
            break
        }
    }

    private func startLocationUpdates() {
        let status = manager.authorizationStatus
        guard status == .authorizedAlways || status == .authorizedWhenInUse else { return }

        // Only legal with `.authorizedAlways`; setting it under When-In-Use throws. The app still
        // publishes in the foreground under When-In-Use, which is the honest degraded mode while
        // the driver has not granted the upgrade yet.
        manager.allowsBackgroundLocationUpdates = status == .authorizedAlways
        manager.showsBackgroundLocationIndicator = true
        manager.startUpdatingLocation()
    }

    private func stopLocationUpdates() {
        manager.stopUpdatingLocation()
        manager.allowsBackgroundLocationUpdates = false
    }

    // MARK: - The socket

    /// Reconnects whenever the MQTT session token changes.
    ///
    /// **EMQX validates the JWT at CONNECT only**, so a rotation has no effect on a live session —
    /// the socket has to be rebuilt. `MqttSessionTokenManager` renews at `T−10 min` precisely so
    /// this reconnect happens with time to spare rather than at the moment the credential dies.
    private func watchSessionToken() {
        tokens?.cancel()
        tokens = graph.mqttTokens.watch { [weak self] token in
            Task { @MainActor in self?.connect(with: token) }
        }
    }

    private func connect(with token: MqttSessionToken) {
        reconnectTask?.cancel()
        let plan = IosMqttPlan.companion.of(config: graph.config.mqtt, token: token)
        backoff.reset()
        transport.connect(plan: plan)

        // `:shared`'s backoff and not a local loop: R-09 bounds how hard a city's worth of handsets
        // may retry after a broker restart — a fixed one-second retry from every driver in Colombo
        // is the reconnect storm itself.
        reconnectTask = Task { [weak self] in
            while let self, !Task.isCancelled, !self.transport.isConnected {
                let seconds = self.backoff.nextSeconds()
                try? await Task.sleep(nanoseconds: UInt64(seconds * 1_000_000_000))
                guard !Task.isCancelled, !self.transport.isConnected else { return }
                self.transport.connect(plan: plan)
            }
        }
    }

    private func onBrokerConnected() {
        isPublishing = true
        reconnectTask?.cancel()
        reconnectTask = nil
        pipeline?.onReconnected()
        refreshBufferedCount()
    }

    /// A downlink command on `veh/{vehicleId}/cmd`. Decoded by `:shared` (R-07).
    private func onCommand(_ payload: Data) {
        _ = pipeline?.onCommand(payload: payload as NSData)
    }

    // MARK: - The drain

    /// Drains the offline backlog on a timer while the socket is up (R-17).
    ///
    /// A tick rather than a one-shot after reconnect, because the pacing rules hand the backlog out
    /// in slices: the 2 s live-drain window, the 4:1 live preemption and the 20 msg/s ceiling all
    /// mean a single pass would publish a handful and stop with hours of history still on disk.
    private func startDraining() {
        drainTimer?.invalidate()
        drainTimer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in
            Task { @MainActor in
                guard let self, let pipeline = self.pipeline else { return }
                if pipeline.drainReplay(livePending: false) > 0 { self.refreshBufferedCount() }
            }
        }
    }

    private func refreshBufferedCount() {
        bufferedCount = pipeline?.bufferedCount ?? 0
        isPublishing = transport.isConnected
    }

    /// Runs [work] with a short grace window, so a disconnect started as the app is backgrounded
    /// finishes rather than being cut mid-publish.
    ///
    /// The `offline` announcement is the whole reason: it is one QoS-1 publish, and losing it makes
    /// a deliberate sign-off look like a dead handset (R-15, T-04).
    private func withBackgroundTime(_ work: @escaping () -> Void) {
        backgroundTask = UIApplication.shared.beginBackgroundTask(withName: "lk.mageride.driver.mqtt-offline") {
            UIApplication.shared.endBackgroundTask(self.backgroundTask)
            self.backgroundTask = .invalid
        }
        work()
        if backgroundTask != .invalid {
            UIApplication.shared.endBackgroundTask(backgroundTask)
            backgroundTask = .invalid
        }
    }
}

extension PositionService: CLLocationManagerDelegate {

    nonisolated func locationManager(_ manager: CLLocationManager, didUpdateLocations locations: [CLLocation]) {
        Task { @MainActor [weak self] in
            guard let self, let pipeline = self.pipeline else { return }
            for location in locations {
                _ = pipeline.onFix(
                    lat: location.coordinate.latitude,
                    lng: location.coordinate.longitude,
                    sampleTsEpochSeconds: location.timestamp.timeIntervalSince1970,
                    // CoreLocation reports "unknown" as a NEGATIVE value, and a `-1` accuracy that
                    // reached `telemetry.positions` would be read as a metre. Absent is absent.
                    accuracyM: location.horizontalAccuracy >= 0 ? KotlinDouble(value: location.horizontalAccuracy) : nil,
                    speedMps: location.speed >= 0 ? KotlinDouble(value: location.speed) : nil,
                    headingDeg: location.course >= 0 ? KotlinInt(value: Int32(location.course)) : nil,
                    // iOS does not expose a satellite count on any public API. Absent, not zero:
                    // zero satellites is a claim about the fix, and this is a claim about the SDK.
                    satCount: nil
                )
            }
            self.refreshBufferedCount()
        }
    }

    nonisolated func locationManager(_ manager: CLLocationManager, didFailWithError error: Error) {
        // A transient failure is normal in a tunnel and is not a reason to tear anything down: the
        // pipeline keeps its backlog and the next fix resumes the stream.
    }

    nonisolated func locationManagerDidChangeAuthorization(_ manager: CLLocationManager) {
        Task { @MainActor [weak self] in
            guard let self, self.pipeline != nil else { return }
            self.startLocationUpdates()
        }
    }
}
