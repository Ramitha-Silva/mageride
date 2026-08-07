import Foundation
import MageRideShared

/// SCR-DI-027's state.
///
/// - Parameters:
///   - vehicles: Every vehicle the driver owns or has been assigned — the selector's options.
///   - selectedVehicleId: Which one the form is about. `nil` only while the read is in flight or when
///     the driver has no vehicle at all.
///   - imei: The digits typed or scanned.
///   - binding: The tracker already bound to the selected vehicle, as this handset knows it.
///   - isLoading: The vehicle read is in flight.
///   - isPairing: The bind is in flight — D2' §SCR-DI-027's *"Pairing → spinner"*.
///   - isQuarantined: The last attempt met `409 imei-duplicate` (US-3.4, T-08).
///   - isScanning: The device-QR sheet is up.
///   - errorKey: Resolved copy for the last failure.
struct TrackerPairingState {

    var vehicles: [VehicleSummary] = []
    var selectedVehicleId: String?
    var imei = ""
    var binding: TrackerBinding?
    var isLoading = true
    var isPairing = false
    var isQuarantined = false
    var isScanning = false
    var errorKey: String?

    /// The vehicle the form is about.
    var selected: VehicleSummary? { vehicles.first { $0.vehicleId == selectedVehicleId } }

    /// Whether the selected vehicle already publishes through hardware.
    var isPaired: Bool { binding != nil }

    /// Whether what has been typed is fifteen digits. Blank is *"not yet"*, not *"wrong"*.
    var isImeiValid: Bool { TrackerImei.isValid(imei) }

    /// Whether the field should be drawn in `error` — typed something, and it is not an IMEI.
    var isImeiRejected: Bool { !imei.isEmpty && !isImeiValid }

    /// Whether **Pair device** is live. A vehicle that is already tracked cannot be paired again.
    var canPair: Bool { !isPairing && !isPaired && isImeiValid && selectedVehicleId != nil }

    /// Whether the driver has no vehicle to pair a tracker to at all.
    var hasNoVehicle: Bool { !isLoading && vehicles.isEmpty }
}

/// **SCR-DI-027 · GPS tracker pairing** (US-3.1/3.2, US-3.21–3.23, T-02/T-09).
///
/// Binds an IMEI to one of the driver's vehicles through registry-svc's owner-facing wrapper, and —
/// the part that matters beyond this screen — **stops this handset publishing GPS for that vehicle the
/// moment the bind is accepted**. The gate itself lives in ``TrackerPositionPublisher``, which every
/// door onto the position service goes through; what happens here is the other half of it: a driver who
/// is online *right now* on the vehicle they have just paired has a publisher running, and a gate that
/// only refused the next start would leave it running until they went offline.
///
/// **The phone is stopped, the platform is not told.** `POST /v1/trackers/{imei}/switch-source` is the
/// route that would say so and it is provisioning-svc's, with no client in `:shared` — see
/// ``TrackerRepository``. Stopping is still the right unilateral move: a phone that keeps publishing
/// beside a bound device interleaves two clocks on one topic (US-3.6), and of the two publishers the
/// device is the one the platform has just issued a credential to.
@MainActor
final class TrackerPairingModel: ObservableObject {

    @Published private(set) var state = TrackerPairingState()

    private let identity: DriverIdentity
    private let trackers: TrackerRepository
    private let publisher: PositionPublisher
    private let camera: CameraAuthoriser

    init(
        identity: DriverIdentity,
        trackers: TrackerRepository,
        publisher: PositionPublisher,
        camera: CameraAuthoriser
    ) {
        self.identity = identity
        self.trackers = trackers
        self.publisher = publisher
        self.camera = camera
    }

    /// Whether the **▣ Scan device QR** button is offered at all.
    ///
    /// A device that cannot run the scanner gets a disabled button rather than a hidden one, for the
    /// reason **Bind code** is drawn disabled: a control the wireframe draws and the driver cannot see
    /// is a screen that does not match its baseline, and the line under the row says why.
    var isScanSupported: Bool { camera.isCodeScannerSupported }

    /// Re-reads the driver's vehicles and the binding on whichever is selected.
    func refresh() async {
        state.isLoading = true
        state.errorKey = nil
        do {
            let live = try await identity.liveVehicle()
            // The live vehicle is the sensible default — it is the one this handset publishes for, so
            // it is the one a driver holding a new tracker is standing next to.
            let held = state.selectedVehicleId.flatMap { id in
                live.vehicles.contains { $0.vehicleId == id } ? id : nil
            }
            let selected = held ?? live.live?.vehicleId ?? live.vehicles.first?.vehicleId

            state.vehicles = live.vehicles
            state.selectedVehicleId = selected
            state.binding = selected.flatMap { trackers.bindingFor(vehicleId: $0) }
        } catch {
            state.errorKey = OnboardingErrors.messageKey(for: error)
        }
        state.isLoading = false
    }

    /// The wireframe's `Vehicle   ABC-1234 ›` selector. Re-scopes the whole form.
    func select(vehicleId: String) {
        guard state.selectedVehicleId != vehicleId else { return }
        state.selectedVehicleId = vehicleId
        state.binding = trackers.bindingFor(vehicleId: vehicleId)
        // The IMEI belongs to the vehicle it was typed for. Carrying it across the selector is how a
        // tracker gets bound to the wrong one.
        state.imei = ""
        state.isQuarantined = false
        state.errorKey = nil
    }

    /// The IMEI field. Reduced to digits on every keystroke, so the field can never hold a value the
    /// validator would reject.
    func onImeiChange(_ raw: String) {
        state.imei = TrackerImei.digits(raw)
        state.isQuarantined = false
        state.errorKey = nil
    }

    /// **▣ Scan device QR** — asks for the camera, then raises the sheet.
    ///
    /// The grant is requested here rather than left to `DataScannerViewController`: its own
    /// `isAvailable` is `false` until the camera is granted, so presenting first would show a sheet
    /// that could not scan and had nothing to say about why.
    func startScan() async {
        guard camera.isCodeScannerSupported else {
            state.errorKey = "tracker_scan_unsupported"
            return
        }
        if camera.authorisation == .notDetermined {
            _ = await camera.request()
        }
        guard camera.authorisation == .granted else {
            state.errorKey = "tracker_scan_blocked"
            return
        }
        state.isQuarantined = false
        state.errorKey = nil
        state.isScanning = true
    }

    /// The sheet was dismissed without a read.
    func cancelScan() {
        state.isScanning = false
    }

    /// A QR code was decoded.
    ///
    /// The payload is searched for an IMEI rather than parsed, because no spec says what a tracker
    /// vendor prints in it — see ``TrackerImei/imeiIn(_:)``. A payload with no single candidate leaves
    /// the field alone and says so, which puts the driver back on the path that always works: typing.
    func onScanned(_ payload: String) {
        state.isScanning = false
        guard let imei = TrackerImei.imeiIn(payload) else {
            state.errorKey = "tracker_scan_unreadable"
            return
        }
        state.imei = imei
        state.isQuarantined = false
        state.errorKey = nil
    }

    /// **Pair device** — `POST /v1/vehicles/{vehicleId}/device`.
    ///
    /// On success the phone stops publishing for that vehicle. ``PositionPublisher/stop()`` is
    /// unconditional rather than conditional on this being the live vehicle: the service publishes for
    /// one vehicle at a time and stopping one that was never started is a no-op, so the cheap call is
    /// also the one that cannot leave a stream running.
    func pair() async {
        guard state.canPair, let vehicleId = state.selectedVehicleId else { return }
        let imei = state.imei

        state.isPairing = true
        state.isQuarantined = false
        state.errorKey = nil
        do {
            let binding = try await trackers.pair(vehicleId: vehicleId, imei: imei)
            publisher.stop()
            state.binding = binding
            state.imei = ""
        } catch {
            state.isQuarantined = Self.isQuarantine(error)
            state.errorKey = state.isQuarantined
                ? "tracker_quarantined"
                : OnboardingErrors.messageKey(for: error)
        }
        state.isPairing = false
    }

    /// Clears the last failure once its copy has been read.
    func dismissError() {
        state.errorKey = nil
        state.isQuarantined = false
    }

    /// Whether `error` is T-08's anti-clone quarantine rather than an ordinary failure.
    ///
    /// `409 imei-duplicate` is lifted out of the generic path because it is not a failure to retry: the
    /// serial is already active elsewhere on the platform, and the screen says so rather than offering
    /// the same button again. Through ``OnboardingErrors/kotlinCause(of:)``, because a Kotlin exception
    /// reaches Swift wrapped in an `NSError` and a bare `as? MageRideError` never matches.
    private static func isQuarantine(_ error: Error) -> Bool {
        guard let failure = OnboardingErrors.kotlinCause(of: error) as? MageRideError else { return false }
        return failure.code == ErrorCode.imeiDuplicate
    }
}
