package lk.mageride.shared.data.models.content

import kotlinx.serialization.Serializable
import lk.mageride.shared.data.models.GeoPoint
import lk.mageride.shared.data.models.Language
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.Ulid

// content-svc — launch-configuration reference data, localised notification templates, in-app
// announcements.
// Source: backend/contracts/content.yaml (D3' content-svc + config.operating_cities §17b).
//
// TRILINGUAL IS A SCHEMA CONSTRAINT, NOT A CONVENTION: ck_broadcasts_trilingual (C005) rejects a
// broadcast that would leave a language blank, and PUT /v1/admin/content/{key} answers
// validation-failed for the same reason (D-26). See TrilingualText.
//
// Templates are VERSIONED, NOT OVERWRITTEN — a publish creates version n+1 and makes it current.

/**
 * A launch city (`content.yaml#/components/schemas/OperatingCity`,
 * `config.operating_cities`, §17b).
 *
 * Backs the first-run city screen (SCR-DA/DI-002); the chosen [code] persists on
 * `iam.users.operatingCityCode`. Colombo is first and default, matching the map centroid default.
 *
 * The three name fields are **server-supplied data**, not resource strings: a city's Sinhala and
 * Tamil names live in the database because Admin can add a city without shipping an app build.
 *
 * @property code Stable machine key, e.g. `colombo`.
 * @property nameEn English name.
 * @property nameSi Sinhala name.
 * @property nameTa Tamil name.
 * @property centroid Map centre for the city.
 * @property sortOrder Display order on the picker.
 */
@Serializable
public data class OperatingCity(
    val code: String,
    val nameEn: String,
    val nameSi: String,
    val nameTa: String,
    val centroid: GeoPoint,
    val sortOrder: Int,
) {
    /** The name in one language, with no fallback — every row carries all three. */
    public fun name(language: Language): String = when (language) {
        Language.SI -> nameSi
        Language.TA -> nameTa
        Language.EN -> nameEn
    }
}

/**
 * `GET /v1/config/cities` — 200. Public, cacheable, active rows only, ordered by `sortOrder`.
 *
 * @property cities The launch cities.
 */
@Serializable
public data class OperatingCityListResponse(val cities: List<OperatingCity> = emptyList())

/**
 * One string in all three languages (`content.yaml#/components/schemas/TrilingualText`).
 *
 * **All three are mandatory** (D-26; `ck_broadcasts_trilingual`, C005). That is why this is a
 * three-field object rather than a `Map<Language, String>`: the type itself makes a missing
 * language unrepresentable.
 *
 * @property si Sinhala.
 * @property ta Tamil.
 * @property en English.
 */
@Serializable
public data class TrilingualText(val si: String, val ta: String, val en: String) {
    /** The text in one language. Total — all three are always populated. */
    public operator fun get(language: Language): String = when (language) {
        Language.SI -> si
        Language.TA -> ta
        Language.EN -> en
    }
}

/**
 * A notification template resolved into one language
 * (`content.yaml#/components/schemas/NotificationTemplate`).
 *
 * Internal, mTLS only — notification-svc calls it while composing an FCM/APNs payload (D-26,
 * D6' I-29.2). Every key exists in all three languages, so an unsupported `?lang=` falls back
 * rather than 404s.
 *
 * @property key Template key, e.g. `ride_offer`, `package_on_the_way`, `proxy_ride_link`.
 * @property language Which language this rendering is.
 * @property version The current version of the template.
 * @property title Rendered title, where the template has one.
 * @property body Rendered body, with `{{placeholders}}` still in place.
 */
@Serializable
public data class NotificationTemplate(
    val key: String,
    val language: Language,
    val version: Int,
    val title: String? = null,
    val body: String,
)

/**
 * `PUT /v1/admin/content/{key}`. Admin only, audited (D-35).
 *
 * Creates version `n+1` and makes it current. **All three languages are required** — a template
 * that would leave one blank is rejected `validation-failed` (D-26).
 *
 * @property titleByLang The title in Si/Ta/En.
 * @property bodyByLang The body in Si/Ta/En.
 */
@Serializable
public data class UpdateNotificationTemplateRequest(
    val titleByLang: TrilingualText? = null,
    val bodyByLang: TrilingualText,
)

/**
 * `PUT /v1/admin/content/{key}` — 200.
 *
 * @property key The template published.
 * @property version The new current version.
 */
@Serializable
public data class NotificationTemplateVersion(val key: String, val version: Int)

/**
 * An in-app announcement currently in force
 * (`content.yaml#/components/schemas/Broadcast`, US-14.8).
 *
 * @property broadcastId The broadcast.
 * @property message Already resolved into the requested language.
 * @property startsAt When the banner appears.
 * @property endsAt When it stops.
 */
@Serializable
public data class Broadcast(
    val broadcastId: Ulid,
    val message: String,
    val startsAt: Timestamp,
    val endsAt: Timestamp? = null,
)

/**
 * `GET /v1/content/broadcasts` — 200. Only broadcasts whose window covers now, newest first.
 *
 * @property items The active broadcasts.
 */
@Serializable
public data class BroadcastListResponse(val items: List<Broadcast> = emptyList())
