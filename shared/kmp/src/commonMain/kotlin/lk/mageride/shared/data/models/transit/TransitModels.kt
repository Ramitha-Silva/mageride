package lk.mageride.shared.data.models.transit

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import lk.mageride.shared.data.models.BusinessDate
import lk.mageride.shared.data.models.GeoPoint
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.Ulid

// transit-svc — GTFS public-transport routing and the GTFS Dataset Manager.
// Source: backend/contracts/transit.yaml (D3' Δ 2026-06-21 AL-18, Δ 2026-07-22 #2 AL-54,
// ADD Appendix C).
//
// AL-18: THE GTFS FEED IS THE SOURCE OF TRUTH FOR MODE A ROUTING.
// AL-17: a destination is a GEO-LOCATION ONLY — a passenger never types a route number, and
// /v1/geo/search never returns route rows.
//
// DATASET LIFECYCLE (AL-54, SCR-AP-016): upload a full GTFS zip (<= 200 MB, sha256-deduped) →
// asynchronous validation → preview counts and warnings → ACTIVATE. Activation swaps the live
// tables in ONE transaction and NOTIFYs transit_feed_activated; the previous feed becomes
// archived. ROLLBACK IS ACTIVATION OF AN ARCHIVED, VALIDATED VERSION — the same endpoint with the
// same guarantees. EXACTLY ONE FEED IS ACTIVE, enforced by a partial unique index (C005).

/**
 * Whether a transit option runs end to end on one route or needs a change
 * (`transit.yaml#/components/schemas/TransitOption.kind`, AL-18).
 *
 * @property wire The value as it appears on the wire.
 */
@Serializable
public enum class TransitOptionKind(public val wire: String) {
    @SerialName("direct")
    DIRECT("direct"),

    @SerialName("transit")
    TRANSIT("transit"),
}

/**
 * Where a GTFS feed version is in its lifecycle
 * (`transit.gtfs_feed_versions.status` CHECK, C005).
 *
 * @property wire The value as it appears on the wire.
 */
@Serializable
public enum class FeedStatus(public val wire: String) {
    @SerialName("uploaded")
    UPLOADED("uploaded"),

    @SerialName("validating")
    VALIDATING("validating"),

    @SerialName("validated")
    VALIDATED("validated"),

    @SerialName("failed")
    FAILED("failed"),

    @SerialName("active")
    ACTIVE("active"),

    @SerialName("archived")
    ARCHIVED("archived"),
    ;

    /** Whether this version may be activated — including a rollback to an archived one. */
    public val isActivatable: Boolean get() = this == VALIDATED || this == ARCHIVED
}

/** The `?format=` on the validation-report download. */
@Serializable
public enum class ReportFormat {
    @SerialName("json")
    JSON,

    @SerialName("csv")
    CSV,
}

/**
 * One boarding-to-alighting leg of a transit option
 * (`transit.yaml#/components/schemas/TransitLeg`).
 *
 * The ids are GTFS ids from the **active** feed, not MageRide ULIDs.
 *
 * @property routeId GTFS `route_id`.
 * @property routeShortName GTFS `route_short_name`, e.g. `138`.
 * @property headsign GTFS trip headsign.
 * @property description Longer route description from the feed.
 * @property boardStopId Where to get on.
 * @property alightStopId Where to get off.
 * @property shape Encoded polyline of the GTFS shape.
 */
@Serializable
public data class TransitLeg(
    val routeId: String,
    val routeShortName: String,
    val headsign: String? = null,
    val description: String? = null,
    val boardStopId: String? = null,
    val alightStopId: String? = null,
    val shape: String? = null,
)

/**
 * One way to make a journey by public transport
 * (`transit.yaml#/components/schemas/TransitOption`).
 *
 * @property kind Direct or with at least one transfer.
 * @property totalDurationSec End-to-end duration in seconds.
 * @property walkingDistanceM Walking metres across the whole option.
 * @property legs At least one leg, in travel order.
 */
@Serializable
public data class TransitOption(
    val kind: TransitOptionKind,
    val totalDurationSec: Int? = null,
    val walkingDistanceM: Int? = null,
    val legs: List<TransitLeg> = emptyList(),
)

/**
 * `GET /v1/transit/options` — 200 (SCR-PA-009).
 *
 * If no feed is active the answer is an **empty option list rather than an error**, so the
 * booking screen degrades to private tiers instead of failing.
 *
 * @property options Direct and transfer options.
 * @property feedVersion `feed_info` version of the active feed the answer came from.
 */
@Serializable
public data class TransitOptionsResponse(
    val options: List<TransitOption> = emptyList(),
    val feedVersion: String? = null,
)

/**
 * One stop on a route (`transit.yaml#/components/schemas/TransitStop`).
 *
 * @property stopId GTFS `stop_id`.
 * @property name Stop name as the feed spells it.
 * @property lat Degrees, −90…90.
 * @property lng Degrees, −180…180.
 * @property sequence Position along the route.
 * @property distanceM Distance from the supplied reference coordinate, on a nearest-stops read.
 */
@Serializable
public data class TransitStop(
    val stopId: String,
    val name: String,
    val lat: Double,
    val lng: Double,
    val sequence: Int? = null,
    val distanceM: Int? = null,
) {
    /** The stop as a plain coordinate. */
    public val point: GeoPoint get() = GeoPoint(lat = lat, lng = lng)
}

/**
 * A route with its shape and stops (`transit.yaml#/components/schemas/TransitRoute`).
 *
 * @property routeId GTFS `route_id`.
 * @property routeShortName Short name, e.g. `138`.
 * @property routeLongName Long name from the feed.
 * @property agencyName Operating agency.
 * @property shape Encoded polyline of the GTFS shape.
 * @property stops Every stop, in order.
 * @property nearestStops Present when a reference coordinate was supplied.
 */
@Serializable
public data class TransitRoute(
    val routeId: String,
    val routeShortName: String,
    val routeLongName: String? = null,
    val agencyName: String? = null,
    val shape: String? = null,
    val stops: List<TransitStop> = emptyList(),
    val nearestStops: List<TransitStop>? = null,
)

/**
 * `GET /v1/geo/parse-maps-link` — 200 (AL-20).
 *
 * Backs the "Paste link" affordance. Full URLs are parsed client-side; **short `maps.app.goo.gl`
 * links are resolved server-side by following the redirect** — there is no Google API call, which
 * keeps the map hard rule intact (D6' I-23.1).
 *
 * @property lat Degrees, −90…90.
 * @property lng Degrees, −180…180.
 * @property label The label carried in the link, when there was one.
 */
@Serializable
public data class ParsedMapsLink(val lat: Double, val lng: Double, val label: String? = null) {
    /** The resolved coordinate. */
    public val point: GeoPoint get() = GeoPoint(lat = lat, lng = lng)
}

// ---------------------------------------------------------------------------------------------
// GTFS Dataset Manager (AL-54)
// ---------------------------------------------------------------------------------------------

/**
 * `POST /v1/admin/transit/gtfs/uploads` — 202.
 *
 * The zip is stored in object storage and **deduped on sha256** — re-uploading a byte-identical
 * feed is `409 feed-duplicate` rather than a second version.
 *
 * @property feedVersionId The version to poll for validation.
 */
@Serializable
public data class GtfsUploadAccepted(val feedVersionId: Ulid)

/**
 * One error or warning from GTFS validation
 * (`transit.yaml#/components/schemas/FeedIssue`).
 *
 * @property file Which feed file, e.g. `stop_times.txt`.
 * @property row Row number within that file.
 * @property code Stable machine key, e.g. `unknown_stop_id`.
 * @property message What is wrong. Operator-facing English from the validator, not platform copy.
 */
@Serializable
public data class FeedIssue(val file: String, val row: Long? = null, val code: String, val message: String)

/**
 * `GET /v1/admin/transit/gtfs/uploads/{feedVersionId}` — 200.
 *
 * @property feedVersionId The version.
 * @property status Where validation has got to.
 * @property counts Per-file row counts from the uploaded zip, keyed by GTFS file name.
 * @property feedInfoVersion The `feed_info` version string.
 * @property serviceStart First service day. **Read out of the feed rather than derived in
 *   Asia/Colombo, so unlike every other business date on the platform it carries no `tzAt`
 *   companion** (C005 decision 1).
 * @property serviceEnd Last service day. Same exemption.
 * @property warnings Non-blocking findings.
 * @property errorSummary **At most five entries** — the full row-level report is a separate
 *   download, so a feed with thousands of errors does not make this response unusable.
 */
@Serializable
public data class FeedUploadStatus(
    val feedVersionId: Ulid,
    val status: FeedStatus,
    val counts: Map<String, Long>? = null,
    val feedInfoVersion: String? = null,
    val serviceStart: BusinessDate? = null,
    val serviceEnd: BusinessDate? = null,
    val warnings: List<String>? = null,
    val errorSummary: List<String>? = null,
)

/**
 * `GET /v1/admin/transit/gtfs/uploads/{feedVersionId}/report` — the JSON arm.
 *
 * @property errors Blocking findings; any of these keeps the feed at [FeedStatus.FAILED].
 * @property warnings Non-blocking findings.
 */
@Serializable
public data class GtfsValidationReport(
    val errors: List<FeedIssue> = emptyList(),
    val warnings: List<FeedIssue> = emptyList(),
)

/**
 * One uploaded GTFS version (`transit.yaml#/components/schemas/FeedVersion`).
 *
 * Original zips are retained for every version, which is what makes rollback a re-import rather
 * than a restore.
 *
 * @property feedVersionId The version.
 * @property feedInfoVersion The `feed_info` version string.
 * @property fileName The uploaded zip's name.
 * @property sha256 Content hash — the dedupe key.
 * @property uploadedBy Who uploaded it.
 * @property uploadedAt When.
 * @property counts Per-file row counts.
 * @property status Where it is in the lifecycle.
 * @property activatedAt When it went live, if it ever did.
 * @property archivedAt When it was superseded.
 */
@Serializable
public data class FeedVersion(
    val feedVersionId: Ulid,
    val feedInfoVersion: String? = null,
    val fileName: String,
    val sha256: String? = null,
    val uploadedBy: Ulid,
    val uploadedAt: Timestamp,
    val counts: Map<String, Long>? = null,
    val status: FeedStatus,
    val activatedAt: Timestamp? = null,
    val archivedAt: Timestamp? = null,
)

/**
 * `POST /v1/admin/transit/gtfs-import` — **superseded** as an operator action.
 *
 * Retained only as the internal import step the activation job invokes: SCR-AP-016 never calls it
 * and it performs no validation of its own (AL-54).
 *
 * @property feedVersionId The version to import.
 */
@Serializable
public data class ImportGtfsFeedRequest(val feedVersionId: Ulid)

/**
 * `POST /v1/admin/transit/gtfs-import` — 202.
 *
 * @property feedVersionId The version being imported.
 * @property status Where it is in the lifecycle.
 */
@Serializable
public data class ImportGtfsFeedResponse(val feedVersionId: Ulid, val status: FeedStatus)
