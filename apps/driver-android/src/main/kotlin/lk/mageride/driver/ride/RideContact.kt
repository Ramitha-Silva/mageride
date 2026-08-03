package lk.mageride.driver.ride

import lk.mageride.shared.data.api.comms.VoipApi
import lk.mageride.shared.data.api.safety.SafetyApi
import lk.mageride.shared.data.models.CallType
import lk.mageride.shared.data.models.GeoPoint
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.comms.CalleeRole
import lk.mageride.shared.data.models.comms.StartCallRequest
import lk.mageride.shared.data.models.comms.StartCallResponse
import lk.mageride.shared.data.models.ride.RideKind
import lk.mageride.shared.data.models.safety.SosDispatched
import lk.mageride.shared.data.models.safety.SosRole
import lk.mageride.shared.data.models.safety.TriggerSosRequest

/**
 * Reaching the other party, and raising the alarm — SCR-DA-015's two icon buttons.
 *
 * **AL-48 — there is no masking bridge left.** The call-type chooser keeps two options and only
 * two: **Free call** mints a WebRTC session, and **Normal call** is a *direct cellular dial* of the
 * counterparty's real number, which `RideDetail.counterpartyPhone` carries from `Accepted` onward.
 * The masked-number PSTN bridge and D-25's masked-SMS relay were both withdrawn; a VoIP failure
 * falls back to *"Call normally instead?"* — the same direct dial (US-26.4, BR-30.3).
 *
 * **P-05 — the driver calls the rider, never the booker.** On a proxy booking the person in the car
 * is not the person who paid, and `counterpartyPhone` is already the rider's; [calleeRole] is what
 * says so on the log.
 */
internal class RideContact(private val voip: VoipApi, private val safety: SafetyApi) {

    /**
     * `POST /v1/calls/start` — records the call and, for [CallType.FREE_VOIP], mints the session.
     *
     * A [CallType.DIRECT_DIAL] call creates no session at all: the response is a `comms.call_log`
     * row and nothing else, because a `tel:` dial cannot be server-verified. The log is
     * **best-effort** for the same reason — a failure here must never stop the dial.
     */
    suspend fun startCall(rideId: Ulid, kind: RideKind, type: CallType): StartCallResponse =
        voip.startCall(StartCallRequest(rideId = rideId, calleeRole = calleeRole(kind), callType = type))

    /**
     * `POST /v1/sos` — the driver's own SOS, during an active trip only (US-12.8, AL-13).
     *
     * D-33's dual gateway fans the SMS out in parallel and the response says which managed it.
     * `SosSmsStatus.FAILED` is **not** an error: the alert is recorded and is on the admin live
     * feed either way, and telling somebody in trouble that nothing happened would be worse than
     * telling them the SMS leg did not.
     */
    suspend fun triggerSos(rideId: Ulid, at: GeoPoint): SosDispatched = safety.triggerSos(
        TriggerSosRequest(rideId = rideId, lat = at.lat, lng = at.lng, role = SosRole.DRIVER),
    )

    /**
     * Who the driver is calling.
     *
     * A package has no rider — the person at the pickup is the **sender**, and the delivery sheets
     * (SCR-DA-016a/b/c, C071) are where the recipient is called from.
     */
    private fun calleeRole(kind: RideKind): CalleeRole =
        if (kind == RideKind.PACKAGE) CalleeRole.SENDER else CalleeRole.PASSENGER
}
