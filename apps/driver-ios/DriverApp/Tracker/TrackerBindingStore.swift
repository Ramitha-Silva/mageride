import Foundation

/// One tracker bound to one vehicle, as this handset knows it.
///
/// - Parameters:
///   - vehicleId: The vehicle the device publishes for.
///   - imei: The 15-digit IMEI that was bound.
///   - bindingId: The `prov.tracker_bindings` row `POST /v1/vehicles/{id}/device` answered with. Its
///     existence **is** the credential status: provisioning-svc mints the X.509 or the signed PSK
///     inside that call (T-02, D6' §4.2), so a `201` is D2' §SCR-DI-027's *"paired → cert-issued
///     confirmation"*.
struct TrackerBinding: Equatable {

    let vehicleId: String
    let imei: String
    let bindingId: String
}

/// Which of this driver's vehicles have a hardware tracker on them.
///
/// **This is local, and it has to be** — not by preference, the way ``ActiveVehicleStore`` is, but
/// because nothing on the app-facing surface answers the question. `POST /v1/vehicles/{id}/device` is
/// the only tracker route `:shared` has; it returns a `bindingId` and nothing reads one back.
/// `VehicleSummary` and `VehicleDetail` carry no device field, `GET /v1/trackers/{imei}` is
/// provisioning-svc's and has no client here, and there is no *"which vehicles of mine are tracked"*
/// read anywhere in `registry.yaml`. Recorded as the first C074 spec gap and carried forward
/// unchanged.
///
/// What hangs off it is not cosmetic: ``TrackerPositionPublisher`` refuses to start the phone's
/// publisher for a vehicle in this store, which is US-3.6's *"exactly one publisher at a time"*. A
/// driver who pairs on one handset and drives with another therefore still publishes from the second
/// — the honest limit of a device-local answer, and the reason the gap is worth closing server-side
/// rather than working around here.
///
/// A protocol with a `UserDefaults` implementation for the reason ``OnboardingPreferences`` is one: a
/// model test has no store, and a fake is what makes *"this vehicle is tracked"* settable.
protocol TrackerBindingStore: AnyObject {

    /// The tracker bound to `vehicleId`, or `nil` when this handset has not paired one.
    func bindingFor(vehicleId: String) -> TrackerBinding?

    /// Records a successful pair. Replaces any earlier binding on the same vehicle.
    func remember(_ binding: TrackerBinding)
}

/// ``TrackerBindingStore`` over the app's own `UserDefaults` suite.
///
/// **Not the Keychain.** An IMEI is not a secret — it is printed on the device and typed into the
/// screen above — and the Keychain is for credentials. Same reasoning, same conclusion, as
/// ``UserDefaultsActiveVehicleStore`` and as `AndroidTrackerBindingStore`'s `SharedPreferences`.
final class UserDefaultsTrackerBindingStore: TrackerBindingStore {

    private let store: UserDefaults

    init(store: UserDefaults = .standard) {
        self.store = store
    }

    func bindingFor(vehicleId: String) -> TrackerBinding? {
        guard let stored = store.string(forKey: key(for: vehicleId)) else { return nil }
        let parts = stored.components(separatedBy: Self.separator)
        guard parts.count == Self.fields else { return nil }
        return TrackerBinding(vehicleId: vehicleId, imei: parts[0], bindingId: parts[1])
    }

    func remember(_ binding: TrackerBinding) {
        store.set(
            binding.imei + Self.separator + binding.bindingId,
            forKey: key(for: binding.vehicleId)
        )
    }

    /// One key per vehicle, prefixed the way every other value this app keeps in the standard suite
    /// is — see ``UserDefaultsOnboardingPreferences``.
    private func key(for vehicleId: String) -> String { Self.prefix + vehicleId }

    private static let prefix = "mageride.tracker.binding."

    /// Neither a ULID nor an IMEI can contain it, so the split is unambiguous.
    private static let separator = "|"
    private static let fields = 2
}
