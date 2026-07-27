package lk.mageride.shared.data.api.content

import io.ktor.client.request.header
import io.ktor.client.request.parameter
import io.ktor.http.HttpHeaders
import io.ktor.http.HttpStatusCode
import io.ktor.http.encodeURLPathPart
import lk.mageride.shared.data.api.ApiService
import lk.mageride.shared.data.api.ApiTransport
import lk.mageride.shared.data.api.Conditional
import lk.mageride.shared.data.api.Credential
import lk.mageride.shared.data.api.apiGet
import lk.mageride.shared.data.api.apiPut
import lk.mageride.shared.data.api.decode
import lk.mageride.shared.data.api.jsonBody
import lk.mageride.shared.data.models.Language
import lk.mageride.shared.data.models.content.BroadcastListResponse
import lk.mageride.shared.data.models.content.NotificationTemplate
import lk.mageride.shared.data.models.content.NotificationTemplateVersion
import lk.mageride.shared.data.models.content.OperatingCityListResponse
import lk.mageride.shared.data.models.content.UpdateNotificationTemplateRequest

/**
 * content-svc — operating cities, trilingual notification templates and in-app broadcasts
 * (`backend/contracts/content.yaml`, D-26/D-27).
 *
 * All user-facing copy the platform *stores* exists in Sinhala, Tamil and English (CLAUDE.md);
 * these reads are how an app gets it. Nothing here is cached by this client — [getOperatingCities]
 * hands the caller the `ETag` and lets whoever owns the on-device cache (C018) decide.
 */
public interface ContentApi {

    /**
     * `GET /v1/config/cities` — the operating cities, as a conditional GET.
     *
     * The one route in the module that declares an `ETag`, a `Cache-Control` and a `304`, and the
     * one every app hits at first run. Pass the previously returned tag as [ifNoneMatch] and the
     * server answers [Conditional.NotModified] with no body.
     *
     * Public: no credential.
     */
    public suspend fun getOperatingCities(ifNoneMatch: String? = null): Conditional<OperatingCityListResponse>

    /**
     * `GET /v1/content/templates/{key}` — render one notification template in one language.
     *
     * **Service-to-service (mTLS).** notification-svc renders through this; present for contract
     * coverage.
     */
    public suspend fun renderNotificationTemplate(key: String, lang: Language? = null): NotificationTemplate

    /** `GET /v1/content/broadcasts` — in-app banners that are live right now. */
    public suspend fun listActiveBroadcasts(lang: Language? = null): BroadcastListResponse

    /** `PUT /v1/admin/content/{key}` — Admin Portal edits a template in all three languages. */
    public suspend fun updateNotificationTemplate(
        key: String,
        request: UpdateNotificationTemplateRequest,
    ): NotificationTemplateVersion
}

internal class KtorContentApi(private val transport: ApiTransport) : ContentApi {

    override suspend fun getOperatingCities(ifNoneMatch: String?): Conditional<OperatingCityListResponse> {
        val response = transport.apiGet(
            service = SERVICE,
            operationId = "listOperatingCities",
            path = "/v1/config/cities",
            credential = Credential.NONE,
        ) { ifNoneMatch?.let { tag -> header(HttpHeaders.IfNoneMatch, tag) } }

        if (response.status == HttpStatusCode.NotModified) return Conditional.NotModified
        return Conditional.Value(response.decode(), response.headers[HttpHeaders.ETag])
    }

    override suspend fun renderNotificationTemplate(key: String, lang: Language?): NotificationTemplate =
        transport.apiGet(SERVICE, "renderNotificationTemplate", "/v1/content/templates/${key.encodeURLPathPart()}") {
            parameter("lang", lang?.wire)
        }.decode()

    override suspend fun listActiveBroadcasts(lang: Language?): BroadcastListResponse =
        transport.apiGet(SERVICE, "listActiveBroadcasts", "/v1/content/broadcasts") {
            parameter("lang", lang?.wire)
        }.decode()

    override suspend fun updateNotificationTemplate(
        key: String,
        request: UpdateNotificationTemplateRequest,
    ): NotificationTemplateVersion =
        transport.apiPut(SERVICE, "updateNotificationTemplate", "/v1/admin/content/${key.encodeURLPathPart()}") {
            jsonBody(request)
        }.decode()

    private companion object {
        val SERVICE = ApiService.CONTENT
    }
}
