package lk.mageride.shared.data.api.transit

import io.ktor.client.request.accept
import io.ktor.client.request.parameter
import io.ktor.http.ContentType
import io.ktor.http.HttpHeaders
import io.ktor.http.encodeURLPathPart
import kotlin.coroutines.cancellation.CancellationException
import lk.mageride.shared.data.api.ApiService
import lk.mageride.shared.data.api.ApiTransport
import lk.mageride.shared.data.api.FileUpload
import lk.mageride.shared.data.api.MageRideError
import lk.mageride.shared.data.api.apiGet
import lk.mageride.shared.data.api.apiPost
import lk.mageride.shared.data.api.decode
import lk.mageride.shared.data.api.filePart
import lk.mageride.shared.data.api.jsonBody
import lk.mageride.shared.data.api.multipartBody
import lk.mageride.shared.data.api.pageParameters
import lk.mageride.shared.data.models.Page
import lk.mageride.shared.data.models.PageRequest
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.transit.FeedUploadStatus
import lk.mageride.shared.data.models.transit.FeedVersion
import lk.mageride.shared.data.models.transit.GtfsUploadAccepted
import lk.mageride.shared.data.models.transit.GtfsValidationReport
import lk.mageride.shared.data.models.transit.ImportGtfsFeedRequest
import lk.mageride.shared.data.models.transit.ImportGtfsFeedResponse
import lk.mageride.shared.data.models.transit.ParsedMapsLink
import lk.mageride.shared.data.models.transit.TransitOptionsResponse
import lk.mageride.shared.data.models.transit.TransitRoute

/**
 * transit-svc — GTFS public-transport planning and the Dataset Manager
 * (`backend/contracts/transit.yaml`, I-23.2/AL-18, AL-54).
 *
 * Two audiences in one contract: the three app-facing reads at the top, and the operator's GTFS
 * upload → validate → activate pipeline underneath. The operator half is Admin Portal work and
 * lives here only because the contract does.
 *
 * [parseMapsLink] is the AL-20 Google-Maps-link paste: the platform resolves the link
 * server-side rather than embedding a Google SDK in the apps.
 */
@Suppress("TooManyFunctions")
public interface TransitApi {

    /** `GET /v1/transit/options` — bus and train itineraries between two points. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun getTransitOptions(
        fromLat: Double,
        fromLng: Double,
        toLat: Double,
        toLng: Double,
    ): TransitOptionsResponse

    /**
     * `GET /v1/transit/routes/{routeId}` — a route's shape and stops.
     *
     * @param routeId GTFS `route_id` from the active feed.
     * @param lat Optional caller position, so the response can name the nearest stops.
     */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun getTransitRoute(routeId: String, lat: Double? = null, lng: Double? = null): TransitRoute

    /** `GET /v1/geo/parse-maps-link` — resolve a pasted Google Maps link to a point (AL-20). */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun parseMapsLink(url: String): ParsedMapsLink

    /**
     * `POST /v1/admin/transit/gtfs/uploads` — upload a GTFS zip for validation (AL-54).
     *
     * Deduped on sha256: re-uploading a byte-identical feed is `409 feed-duplicate`, not a second
     * version. `202`; poll [getGtfsUpload].
     */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun uploadGtfsFeed(file: FileUpload, idempotencyKey: String? = null): GtfsUploadAccepted

    /** `GET /v1/admin/transit/gtfs/uploads/{feedVersionId}` — validation progress and counts. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun getGtfsUpload(feedVersionId: Ulid): FeedUploadStatus

    /** `GET /v1/admin/transit/gtfs/uploads/{feedVersionId}/report` — errors and warnings, as JSON. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun getGtfsValidationReport(feedVersionId: Ulid): GtfsValidationReport

    /** The same report as CSV, for an operator who wants it in a spreadsheet. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun getGtfsValidationReportCsv(feedVersionId: Ulid): String

    /**
     * `POST /v1/admin/transit/gtfs/uploads/{feedVersionId}/activate` — make this feed live.
     *
     * `409 feed-not-validated` before validation finishes; `409 feed-already-active` if it is.
     */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun activateGtfsFeed(feedVersionId: Ulid, idempotencyKey: String? = null): FeedVersion

    /** `GET /v1/admin/transit/gtfs/versions` — every feed version, newest first. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun listGtfsVersions(page: PageRequest = PageRequest.FIRST): Page<FeedVersion>

    /**
     * `GET /v1/admin/transit/gtfs/versions/{feedVersionId}/download` — the original zip.
     *
     * The server answers `302` with a short-lived signed object-storage URL. Redirects are
     * **not** followed: the caller wants the URL, and following it would hand back the zip's
     * bytes through the JSON pipeline.
     *
     * @return The signed URL from the `Location` header.
     */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun downloadGtfsFeedUrl(feedVersionId: Ulid): String

    /**
     * `POST /v1/admin/transit/gtfs-import` — the internal import step.
     *
     * Superseded as an operator action by upload + activate (AL-54); the contract keeps it, so
     * this client does too.
     */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun importGtfsFeed(
        request: ImportGtfsFeedRequest,
        idempotencyKey: String? = null,
    ): ImportGtfsFeedResponse
}

@Suppress("TooManyFunctions")
internal class KtorTransitApi(private val transport: ApiTransport) : TransitApi {

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun getTransitOptions(
        fromLat: Double,
        fromLng: Double,
        toLat: Double,
        toLng: Double,
    ): TransitOptionsResponse = transport.apiGet(SERVICE, "getTransitOptions", "/v1/transit/options") {
        parameter("fromLat", fromLat)
        parameter("fromLng", fromLng)
        parameter("toLat", toLat)
        parameter("toLng", toLng)
    }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun getTransitRoute(routeId: String, lat: Double?, lng: Double?): TransitRoute =
        transport.apiGet(SERVICE, "getTransitRoute", "/v1/transit/routes/${routeId.encodeURLPathPart()}") {
            parameter("lat", lat)
            parameter("lng", lng)
        }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun parseMapsLink(url: String): ParsedMapsLink =
        transport.apiGet(SERVICE, "parseMapsLink", "/v1/geo/parse-maps-link") {
            parameter("url", url)
        }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun uploadGtfsFeed(file: FileUpload, idempotencyKey: String?): GtfsUploadAccepted =
        transport.apiPost(SERVICE, "uploadGtfsFeed", UPLOADS_PATH, idempotencyKey) {
            multipartBody { filePart("file", file) }
        }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun getGtfsUpload(feedVersionId: Ulid): FeedUploadStatus =
        transport.apiGet(SERVICE, "getGtfsUpload", "$UPLOADS_PATH/$feedVersionId").decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun getGtfsValidationReport(feedVersionId: Ulid): GtfsValidationReport =
        transport.apiGet(SERVICE, "getGtfsValidationReport", "$UPLOADS_PATH/$feedVersionId/report") {
            parameter("format", REPORT_FORMAT_JSON)
        }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun getGtfsValidationReportCsv(feedVersionId: Ulid): String =
        transport.apiGet(SERVICE, "getGtfsValidationReport", "$UPLOADS_PATH/$feedVersionId/report") {
            parameter("format", REPORT_FORMAT_CSV)
            accept(ContentType.Text.CSV)
        }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun activateGtfsFeed(feedVersionId: Ulid, idempotencyKey: String?): FeedVersion =
        transport.apiPost(
            service = SERVICE,
            operationId = "activateGtfsFeed",
            path = "$UPLOADS_PATH/$feedVersionId/activate",
            idempotencyKey = idempotencyKey,
        ).decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun listGtfsVersions(page: PageRequest): Page<FeedVersion> =
        transport.apiGet(SERVICE, "listGtfsVersions", VERSIONS_PATH) { pageParameters(page) }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun downloadGtfsFeedUrl(feedVersionId: Ulid): String =
        transport.apiGet(SERVICE, "downloadGtfsFeed", "$VERSIONS_PATH/$feedVersionId/download")
            .headers[HttpHeaders.Location]
            .orEmpty()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun importGtfsFeed(
        request: ImportGtfsFeedRequest,
        idempotencyKey: String?,
    ): ImportGtfsFeedResponse = transport.apiPost(
        service = SERVICE,
        operationId = "importGtfsFeed",
        path = "/v1/admin/transit/gtfs-import",
        idempotencyKey = idempotencyKey,
    ) { jsonBody(request) }.decode()

    private companion object {
        val SERVICE = ApiService.TRANSIT
        const val UPLOADS_PATH = "/v1/admin/transit/gtfs/uploads"
        const val VERSIONS_PATH = "/v1/admin/transit/gtfs/versions"
        const val REPORT_FORMAT_JSON = "json"
        const val REPORT_FORMAT_CSV = "csv"
    }
}
