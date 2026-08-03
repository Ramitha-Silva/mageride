package lk.mageride.driver.tracker

import lk.mageride.shared.data.api.registry.RegistryApi
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.registry.BindVehicleDeviceRequest

/**
 * SCR-DA-027's data: bind an IMEI to a vehicle, and remember that it is bound.
 *
 * **One route, and it is the owner-facing wrapper** — `POST /v1/vehicles/{vehicleId}/device`
 * (US-3.1). registry-svc delegates to provisioning-svc's `POST /v1/trackers/bind` (T-02), which is
 * where the credential mint, the T-08 anti-clone quarantine and the Redis `imei:{imei}` cache all
 * live. The app never talks to provisioning-svc: `:shared`'s `MageRideApi` has no client for it,
 * deliberately (C019's service list), because everything a *driver* may do to a tracker is meant to
 * come through this wrapper.
 *
 * **What the wrapper does not carry** (C074 spec gaps, in the handoff):
 *
 * * **No unbind.** `provisioning.yaml` has `POST /v1/trackers/unbind` — added by C030 for exactly
 *   this case, an owner moving a tracker between vehicles — and `registry.yaml` has no wrapper for
 *   it. So this class deliberately offers **no release of its own**: forgetting the binding locally
 *   would let the phone start publishing again while the device is still bound and publishing, and
 *   two publishers on one vehicle is the state US-3.6 exists to prevent. A dead button would have
 *   been kinder than a dangerous one.
 * * **No `method` and no `bindCode`.** provisioning-svc's bind takes `method: [manual, qr,
 *   admin_code]` and a `bindCode`; the wrapper's body is `{ imei }` alone, so a typed IMEI and a
 *   scanned one are indistinguishable to the server and the admin-code path is not reachable from
 *   the app at all. AL-43's provenance argument applies here as much as it does to a document
 *   scan — see the C074 handoff.
 * * **No read-back.** The `201` carries a `bindingId` and nothing else; `GET /v1/trackers/{imei}`
 *   (binding + `lastSeen`/`battery`/`signal`) is provisioning-svc's and has no client here, and no
 *   registry read carries a device. [TrackerBindingStore] is what stands in for it.
 */
internal class TrackerRepository(private val registry: RegistryApi, private val bindings: TrackerBindingStore) {

    /**
     * `POST /v1/vehicles/{vehicleId}/device` — bind [imei] and mint its credential.
     *
     * `409 imei-duplicate` is T-08's anti-clone check and is **not** a retryable conflict: the same
     * serial is already active somewhere on the platform, and the answer is a quarantine notice
     * rather than a second attempt. The binding is recorded locally only after the server has
     * accepted it, so a failed pair never stops the phone publishing.
     */
    suspend fun pair(vehicleId: Ulid, imei: String): TrackerBinding {
        val response = registry.bindVehicleDevice(vehicleId, BindVehicleDeviceRequest(imei = imei))
        val binding = TrackerBinding(vehicleId = vehicleId, imei = imei, bindingId = response.bindingId)

        bindings.remember(binding)
        return binding
    }

    /** What this handset knows about [vehicleId]'s tracker. See [TrackerBindingStore]. */
    fun bindingFor(vehicleId: Ulid): TrackerBinding? = bindings.bindingFor(vehicleId)
}
