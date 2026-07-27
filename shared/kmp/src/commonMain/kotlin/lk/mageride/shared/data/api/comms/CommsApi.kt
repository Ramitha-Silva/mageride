package lk.mageride.shared.data.api.comms

import lk.mageride.shared.data.api.ApiService
import lk.mageride.shared.data.api.ApiTransport
import lk.mageride.shared.data.api.apiPost
import lk.mageride.shared.data.api.apiPut
import lk.mageride.shared.data.api.decode
import lk.mageride.shared.data.api.jsonBody
import lk.mageride.shared.data.models.comms.IssueVoipTokenRequest
import lk.mageride.shared.data.models.comms.NotificationPreferences
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
}
