package lk.mageride.shared.data.models.comms

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.JsonObject
import lk.mageride.shared.data.models.CallType
import lk.mageride.shared.data.models.ClientPlatform
import lk.mageride.shared.data.models.Ulid

// voip-svc and notification-svc — the two comms surfaces the apps talk to.
// Sources: backend/contracts/voip.yaml (D-24/D-25, Δ 2026-07-05 #2 AL-48) and
//          backend/contracts/notification.yaml (D3' Part 3 push events, D6' §7.4).
//
// REMOVED BY AL-48, do not re-add: callType `normal_masked` on POST /v1/calls/start, and
// POST /public/track/{token}/call. THE MASKED PSTN BRIDGE IS GONE — "normal call" is a
// client-side `tel:` dial of the counterparty MSISDN carried post-accept in
// GET /v1/rides/{rideId} (RideDetail.counterpartyPhone). No server round-trip, no CPaaS, no DID
// pool, no masked-SMS relay.
//
// The voip binding is driver <-> RIDER, never driver <-> booker: on a proxy booking the person in
// the vehicle is the counterparty (P-05).
//
// notification-svc also mints the AL-44 web share tokens (package_recipient, proxy_rider,
// pickup_confirm) and SMSes them. That is deliberately NOT an endpoint — "mint-and-SMS, never
// returned to a client" — so it has no DTO here.

/**
 * Who a VoIP call connects the caller to (`voip.yaml` `POST /v1/voip/token`).
 *
 * Never the booker (P-05).
 *
 * @property wire The value as it appears on the wire.
 */
@Serializable
public enum class CallCounterparty(public val wire: String) {
    @SerialName("rider")
    RIDER("rider"),

    @SerialName("driver")
    DRIVER("driver"),
}

/**
 * The role being called (`comms.call_log.callee_role` CHECK, C005).
 *
 * `sender` and `recipient` are the package-delivery counterparts of `passenger`.
 *
 * @property wire The value as it appears on the wire.
 */
@Serializable
public enum class CalleeRole(public val wire: String) {
    @SerialName("driver")
    DRIVER("driver"),

    @SerialName("passenger")
    PASSENGER("passenger"),

    @SerialName("sender")
    SENDER("sender"),

    @SerialName("recipient")
    RECIPIENT("recipient"),
}

/**
 * `POST /v1/voip/token`.
 *
 * The caller must be a participant and the ride must be non-terminal. Token lifetime ends at trip
 * end, so a token cannot outlive the reason it was issued.
 *
 * @property rideId The ride to open a room for.
 */
@Serializable
public data class IssueVoipTokenRequest(val rideId: Ulid)

/**
 * A LiveKit session (`voip.yaml`).
 *
 * TURN media relays over the host UDP range and **not** through HAProxy/YARP, which cannot relay
 * UDP. Recordings are off by default (PDPA).
 *
 * @property roomName `ride_{rideId}` — one room per ride.
 * @property token LiveKit JWT, expiring at trip end.
 * @property wsUrl The LiveKit signalling endpoint.
 */
@Serializable
public data class VoipSession(val roomName: String, val token: String, val wsUrl: String)

/**
 * `POST /v1/voip/token` — 200.
 *
 * @property roomName `ride_{rideId}`.
 * @property token LiveKit JWT.
 * @property wsUrl The LiveKit signalling endpoint.
 * @property callee Who the caller will be connected to — **never the booker** (P-05).
 */
@Serializable
public data class VoipTokenResponse(
    val roomName: String,
    val token: String,
    val wsUrl: String,
    val callee: CallCounterparty,
) {
    /** The session block, in the shape [StartCallResponse] carries it. */
    public val session: VoipSession
        get() = VoipSession(roomName = roomName, token = token, wsUrl = wsUrl)
}

/**
 * `POST /v1/calls/start` (AL-48).
 *
 * [CallType.FREE_VOIP] mints the WebRTC/CallKit session and writes `comms.call_log`.
 * [CallType.DIRECT_DIAL] **only records that the user tapped a `tel:` link** — no session is
 * created, and the log is best-effort because a `tel:` dial cannot be server-verified.
 *
 * @property rideId The ride the call belongs to.
 * @property calleeRole Who is being called.
 * @property callType Which of the two remaining call types this is.
 */
@Serializable
public data class StartCallRequest(val rideId: Ulid, val calleeRole: CalleeRole, val callType: CallType)

/**
 * `POST /v1/calls/start` — 200.
 *
 * @property callId The `comms.call_log` row.
 * @property callType What was started or recorded.
 * @property session Present **only** for [CallType.FREE_VOIP].
 */
@Serializable
public data class StartCallResponse(val callId: Ulid, val callType: CallType, val session: VoipSession? = null)

// ---------------------------------------------------------------------------------------------
// notification-svc
// ---------------------------------------------------------------------------------------------

/**
 * How urgently a push should be delivered.
 *
 * [HIGH] is reserved for E-01 offer pushes — it is what wakes a backgrounded driver app inside
 * the 15-second offer TTL.
 *
 * @property wire The value as it appears on the wire.
 */
@Serializable
public enum class NotificationPriority(public val wire: String) {
    @SerialName("normal")
    NORMAL("normal"),

    @SerialName("high")
    HIGH("high"),
}

/**
 * `POST /v1/notify/register-token`.
 *
 * FCM and APNs reissue a token to whichever install currently owns it, so the token is globally
 * unique: registering one another install holds **moves** it (`ux_notif_tokens_token`, C005
 * decision 8). Without that, a reinstall would leave a dead handle receiving E-01 offers.
 *
 * @property token The FCM or APNs token.
 * @property platform Which store's push service issued it.
 * @property deviceId This install's stable identifier.
 */
@Serializable
public data class RegisterPushTokenRequest(val token: String, val platform: ClientPlatform, val deviceId: String)

/**
 * `PUT /v1/notify/preferences` — the request body and the 200 are the same shape (US-10.7).
 *
 * **Safety-critical types (`SOS_*`, `RIDE_CANCELLED`) cannot be muted** and are ignored if
 * present. The keys are push-event names from D3' Part 3, not an enum: the event list grows
 * without a contract change.
 *
 * @property preferences Event name → whether it is enabled.
 */
@Serializable
public data class NotificationPreferences(val preferences: Map<String, Boolean> = emptyMap())

/**
 * `POST /v1/internal/notify/send`. Internal, mTLS only.
 *
 * Resolves the template per recipient language (D-26) and batches to FCM HTTP v1 / APNs HTTP/2.
 * Accepted asynchronously — delivery receipts are not part of this call.
 *
 * @property templateKey A content-svc template key.
 * @property recipients 1–1000 user ids.
 * @property data Template substitution values plus the client's data payload.
 * @property priority `high` is reserved for E-01 offer pushes.
 * @property silent Whether to deliver as a data-only message.
 */
@Serializable
public data class SendNotificationRequest(
    val notificationType: String,
    val templateKey: String? = null,
    val recipients: List<Ulid>? = null,
    val data: JsonObject? = null,
)

/**
 * `POST /v1/internal/notify/send` — 202.
 *
 * @property dispatchId The fan-out batch.
 * @property accepted Recipients queued for delivery.
 * @property suppressed Refused by a preference (US-10.7) or a limit (P-12).
 * @property undeliverable Recipients with no account, no number and no device.
 */
@Serializable
public data class SendNotificationResponse(
    val dispatchId: Ulid,
    val accepted: Int,
    val suppressed: Int,
    val undeliverable: Int,
)
