import Foundation
import MageRideShared

/// SCR-DI-027's data: bind an IMEI to a vehicle, and remember that it is bound.
///
/// **One route, and it is the owner-facing wrapper** — `POST /v1/vehicles/{vehicleId}/device`
/// (US-3.1). registry-svc delegates to provisioning-svc's `POST /v1/trackers/bind` (T-02), which is
/// where the credential mint (D6' §4.2), the T-08 anti-clone quarantine and the Redis `imei:{imei}`
/// cache all live. The app never talks to provisioning-svc: `:shared`'s `MageRideApi` has no client
/// for it, deliberately (C019's service list), because everything a *driver* may do to a tracker is
/// meant to come through this wrapper.
///
/// **What the wrapper does not carry** (C074 spec gaps, carried forward):
///
/// * **No unbind.** `provisioning.yaml` has `POST /v1/trackers/unbind` — added by C030 for exactly
///   this case, an owner moving a tracker between vehicles — and `registry.yaml` has no wrapper for
///   it. So this protocol deliberately offers **no release of its own**: forgetting the binding
///   locally would let the phone start publishing again while the device is still bound and
///   publishing, and two publishers on one vehicle is the state US-3.6 exists to prevent. A dead
///   button would have been kinder than a dangerous one.
/// * **No `method` and no `bindCode`.** provisioning-svc's bind takes `method: [manual, qr,
///   admin_code]` and a `bindCode`; the wrapper's body is `{ imei }` alone, so a typed IMEI and a
///   scanned one are indistinguishable to the server and the admin-code path is not reachable from
///   the app at all. AL-43's provenance argument applies here as much as it does to a document scan.
/// * **No read-back.** The `201` carries a `bindingId` and nothing else; `GET /v1/trackers/{imei}`
///   (binding plus `lastSeen`/`battery`/`signal`, US-3.12) is provisioning-svc's and has no client
///   here, and no registry read carries a device. ``TrackerBindingStore`` is what stands in for it.
///
/// A protocol for the reason every seam in this target is one: `RegistryApi` is a Kotlin interface and
/// cannot be stood in for from Swift, so a model test with no gateway needs the seam on this side of
/// the bridge.
protocol TrackerRepository: AnyObject {

    /// `POST /v1/vehicles/{vehicleId}/device` — bind `imei` and mint its credential.
    func pair(vehicleId: String, imei: String) async throws -> TrackerBinding

    /// What this handset knows about `vehicleId`'s tracker. See ``TrackerBindingStore``.
    func bindingFor(vehicleId: String) -> TrackerBinding?
}

/// ``TrackerRepository`` over `:shared`'s typed registry client.
final class ApiTrackerRepository: TrackerRepository {

    private let registry: RegistryApi
    private let bindings: TrackerBindingStore

    init(registry: RegistryApi, bindings: TrackerBindingStore) {
        self.registry = registry
        self.bindings = bindings
    }

    /// `409 imei-duplicate` is T-08's anti-clone check and is **not** a retryable conflict: the same
    /// serial is already active somewhere on the platform, and the answer is a quarantine notice
    /// rather than a second attempt — see ``TrackerPairingModel/isQuarantine(_:)``.
    ///
    /// The binding is recorded locally only after the server has accepted it, so a failed pair never
    /// stops the phone publishing.
    func pair(vehicleId: String, imei: String) async throws -> TrackerBinding {
        let response = try await registry.bindVehicleDevice(
            vehicleId: vehicleId,
            request: BindVehicleDeviceRequest(imei: imei),
            idempotencyKey: nil
        )
        let binding = TrackerBinding(vehicleId: vehicleId, imei: imei, bindingId: response.bindingId)

        bindings.remember(binding)
        return binding
    }

    func bindingFor(vehicleId: String) -> TrackerBinding? { bindings.bindingFor(vehicleId: vehicleId) }
}
