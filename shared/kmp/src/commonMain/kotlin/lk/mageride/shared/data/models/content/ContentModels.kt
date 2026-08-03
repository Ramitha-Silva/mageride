package lk.mageride.shared.data.models.content

import kotlinx.serialization.SerialName
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
    val placeholders: List<String> = emptyList(),
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
public data class NotificationTemplateVersion(val key: String, val version: Int, val status: TemplateVersionStatus)

/**
 * Whether an admin write went live (`content.yaml#/components/schemas/TemplateVersionRef`).
 *
 * `Content:PublishOnEdit` is the deployment's policy and defaults to off, so an edit normally
 * creates a **draft** that `POST /v1/admin/content/{key}/approve` publishes. The response says
 * which happened rather than leaving the caller to know the configuration.
 *
 * @property wire The value as it appears on the wire.
 */
@Serializable
public enum class TemplateVersionStatus(public val wire: String) {
    @SerialName("draft")
    DRAFT("draft"),

    @SerialName("published")
    PUBLISHED("published"),
}

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

/**
 * `GET /v1/content/faq` — 200 (Δ C045, renamed by MCS-02).
 *
 * **The authoring surface.** C053's fence gives FAQ content to content-svc and the app-facing
 * read to support-svc's `GET /v1/support/faq`; the two carried one `operationId` until MCS-02
 * renamed this one, which is why an app read was silently checked against the wrong schema.
 *
 * @property language The language actually served.
 * @property items Articles, ordered by `sortOrder` then category.
 */
@Serializable
public data class AuthoredFaqListResponse(val language: Language, val items: List<AuthoredFaqArticle> = emptyList())

/**
 * One authored FAQ article (`content.yaml#/components/schemas/FaqArticle`).
 *
 * **Not support-svc's `FaqSummary`.** Articles are stored one row per language
 * (`content.faq_articles`) and this is the authored row with its body; the app-facing list
 * carries a summary and fetches the body per article.
 *
 * @property articleId The article.
 * @property category e.g. `wallet`, `daily_fee`, `booking`.
 * @property title Heading in the resolved language.
 * @property body The article itself.
 * @property sortOrder Position within its category.
 */
@Serializable
public data class AuthoredFaqArticle(
    val articleId: Ulid,
    val category: String,
    val title: String,
    val body: String,
    val sortOrder: Int,
)

/**
 * `GET /v1/content/onboarding/{audience}` — 200 (Δ C045, AL-28 / BR-25.1).
 *
 * The 3-slide feature carousel above SCR-DA/DI-002's language and city selectors. Public and
 * cacheable, because it is drawn on the same pre-sign-in screen as `/v1/config/cities`.
 *
 * @property slides In pager order.
 */
@Serializable
public data class OnboardingSlidesResponse(val slides: List<OnboardingSlide> = emptyList())

/**
 * One carousel slide (`content.yaml#/components/schemas/OnboardingSlide`, AL-28).
 *
 * **All three languages arrive at once, and there is no `lang` parameter.** The language picker is
 * on this very screen, so the client re-renders from the response when the reader switches
 * instead of re-fetching — which is also why a slide carries [TrilingualText] rather than a
 * resolved string.
 *
 * @property slot 1-based position in the pager.
 * @property illustrationRef An **app-bundled asset key** (`onboarding/driver-wallet`), or an
 *   absolute https URL when the deployment sets an asset base. content-svc serves the reference
 *   and never image bytes.
 * @property title Heading, in Si/Ta/En.
 * @property body Copy, in Si/Ta/En.
 */
@Serializable
public data class OnboardingSlide(
    val slot: Int,
    val illustrationRef: String,
    val title: TrilingualText,
    val body: TrilingualText,
)

/**
 * Which app's first-run carousel is being asked for (`content.yaml`, AL-28).
 *
 * @property wire The value as it appears in the `{audience}` path segment.
 */
@Serializable
public enum class OnboardingAudience(public val wire: String) {
    @SerialName("driver")
    DRIVER("driver"),

    @SerialName("passenger")
    PASSENGER("passenger"),
}
