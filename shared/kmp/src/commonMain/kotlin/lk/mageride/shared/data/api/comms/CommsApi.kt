package lk.mageride.shared.data.api.comms

import lk.mageride.shared.data.api.ApiService
import lk.mageride.shared.data.api.ApiTransport
import lk.mageride.shared.data.api.apiPost
import lk.mageride.shared.data.api.apiPostExempt
import lk.mageride.shared.data.api.apiPut
import lk.mageride.shared.data.api.decode
import lk.mageride.shared.data.api.jsonBody
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.comms.AcknowledgeNotificationRequest
import lk.mageride.shared.data.models.comms.IssueVoipTokenRequest
import lk.mageride.shared.data.models.comms.NotificationPreferences
import lk.mageride.shared.data.models.comms.RecordCallOutcomeRequest
import lk.mageride.shared.data.models.comms.RegisterPushTokenRequest
import lk.mageride.shared.data.models.comms.SendNotificationRequest
import lk.mageride.shared.data.models.comms.SendNotificationResponse
import lk.mageride.shared.data.models.comms.StartCallRequest
import lk.mageride.shared.data.models.comms.StartCallResponse
import lk.mageride.shared.data.models.comms.VoipTokenResponse

/**
 * voip-svc — LiveKit call signalling (`backend/contracts/voip.yaml`, D-24/D-25).
 *
 * Two entry points because AL-36 as amended by **AL-48** made the call type a choice, not a
 * policy: [startCall] takes the user's pick and answers with either a VoIP session or the
 * instruction to dial directly. [issueVoipToken] is the lower-level route that mints only a
 * LiveKit token, for a caller that already knows it wants VoIP.
 *
 * Masked calling is gone — AL-48 replaced it with the chooser, and no contract route offers it.
 */
public interface VoipApi {

    /**
     * `POST /v1/voip/token` — mint a LiveKit room token for a ride. Attested (D-30).
     *
     * `409 ride-terminal`: a finished ride has no one left to call.
     */
    public suspend fun issueVoipToken(
        request: IssueVoipTokenRequest,
        idempotencyKey: String? = null,
    ): VoipTokenResponse

    /**
     * `POST /v1/calls/start` — start a call of the type the user chose (AL-36/AL-48).
     *
     * The response's `session` is present only for a free VoIP call; a direct dial carries none
     * and the app places a PSTN call itself (AL-33).
     */
    public suspend fun startCall(request: StartCallRequest, idempotencyKey: String? = null): StartCallResponse

    /**
     * `POST /v1/calls/{callId}/outcome` — record how a call ended (Δ C055).
     *
     * Reported by the handset that placed the call, so it is best-effort in the same way
     * `comms.call_log` itself is — but it is the only way the platform sees a call that never
     * connected. [CallOutcome.VOIP_FAILED] is what AL-48's fallback hangs on: the client sends it
     * when it puts up "Call normally instead?", and a `direct_dial` row after one on the same ride
     * is the fallback being taken rather than a user who simply preferred to dial.
     *
     * Only the caller may report, and only once. Somebody else's call is `404` — a call id is
     * guessable, and "that id exists" is itself something a stranger should not learn about two
     * other people's conversation.
     */
    public suspend fun recordCallOutcome(
        callId: Ulid,
        request: RecordCallOutcomeRequest,
        idempotencyKey: String? = null,
    )
}

internal class KtorVoipApi(private val transport: ApiTransport) : VoipApi {

    override suspend fun issueVoipToken(request: IssueVoipTokenRequest, idempotencyKey: String?): VoipTokenResponse =
        transport.apiPost(
            service = ApiService.VOIP,
            operationId = "issueVoipToken",
            path = "/v1/voip/token",
            idempotencyKey = idempotencyKey,
            attested = true,
        ) { jsonBody(request) }.decode()

    override suspend fun recordCallOutcome(callId: Ulid, request: RecordCallOutcomeRequest, idempotencyKey: String?) {
        transport.apiPost(
            service = ApiService.VOIP,
            operationId = "recordCallOutcome",
            path = "/v1/calls/$callId/outcome",
            idempotencyKey = idempotencyKey,
        ) { jsonBody(request) }
    }

    override suspend fun startCall(request: StartCallRequest, idempotencyKey: String?): StartCallResponse =
        transport.apiPost(ApiService.VOIP, "startCall", "/v1/calls/start", idempotencyKey) {
            jsonBody(request)
        }.decode()
}

/**
 * notification-svc — push token registration and per-type switches
 * (`backend/contracts/notification.yaml`, E-01, D-27).
 *
 * FCM on Android, APNs on iOS; the platform sends through both from one dispatch (D6' §7.4).
 * Safety-critical notification types cannot be muted, which the server enforces — sending
 * `false` for one is accepted and ignored, not an error.
 */
public interface NotificationApi {

    /** `POST /v1/notify/register-token` — register this install's FCM/APNs token. */
    public suspend fun registerPushToken(request: RegisterPushTokenRequest, idempotencyKey: String? = null)

    /** `PUT /v1/notify/preferences` — set the per-type switches. */
    public suspend fun updateNotificationPreferences(request: NotificationPreferences): NotificationPreferences

    /**
     * `POST /v1/internal/notify/send` — fan a template out to a recipient list.
     *
     * **Service-to-service (mTLS).** Present for contract coverage; not reachable from an app.
     */
    public suspend fun sendNotification(
        request: SendNotificationRequest,
        idempotencyKey: String? = null,
    ): SendNotificationResponse

    /**
     * `POST /v1/notify/ack` — confirm a device woke up on a high-priority push (Δ C051, E-01).
     *
     * The driver app calls this from the silent data message's handler with the id the push
     * carried. **It races a three-second deadline** (D6' §7.4): an offer push not acknowledged in
     * time falls back to SMS, which is why the operation is `x-idempotency-exempt` and carries no
     * key — answering `400 idempotency-key-required` to a handset that woke up in time would fire
     * the fallback for a driver who was there.
     *
     * `404` means there was nothing left to acknowledge: the window had closed and the SMS has
     * gone, or the notification belongs to another account.
     */
    public suspend fun acknowledgeNotification(request: AcknowledgeNotificationRequest)
}

internal class KtorNotificationApi(private val transport: ApiTransport) : NotificationApi {

    override suspend fun registerPushToken(request: RegisterPushTokenRequest, idempotencyKey: String?) {
        transport.apiPost(
            service = ApiService.NOTIFICATION,
            operationId = "registerPushToken",
            path = "/v1/notify/register-token",
            idempotencyKey = idempotencyKey,
        ) { jsonBody(request) }
    }

    override suspend fun updateNotificationPreferences(request: NotificationPreferences): NotificationPreferences =
        transport.apiPut(ApiService.NOTIFICATION, "updateNotificationPreferences", "/v1/notify/preferences") {
            jsonBody(request)
        }.decode()

    override suspend fun sendNotification(
        request: SendNotificationRequest,
        idempotencyKey: String?,
    ): SendNotificationResponse = transport.apiPost(
        service = ApiService.NOTIFICATION,
        operationId = "sendNotification",
        path = "/v1/internal/notify/send",
        idempotencyKey = idempotencyKey,
    ) { jsonBody(request) }.decode()

    override suspend fun acknowledgeNotification(request: AcknowledgeNotificationRequest) {
        transport.apiPostExempt(ApiService.NOTIFICATION, "acknowledgeNotification", "/v1/notify/ack") {
            jsonBody(request)
        }
    }
}
