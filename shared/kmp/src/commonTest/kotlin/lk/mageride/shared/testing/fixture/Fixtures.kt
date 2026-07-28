package lk.mageride.shared.testing.fixture

import kotlinx.datetime.LocalDate
import lk.mageride.shared.data.models.BusinessDate
import lk.mageride.shared.data.models.Currency
import lk.mageride.shared.data.models.GeoPoint
import lk.mageride.shared.data.models.GeoPointWithAccuracy
import lk.mageride.shared.data.models.Money
import lk.mageride.shared.data.models.PhoneE164
import lk.mageride.shared.data.models.PhoneMasked
import lk.mageride.shared.data.models.Place
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.util.BusinessCalendar
import kotlin.time.ExperimentalTime
import kotlin.time.Instant

/**
 * The values every MageRide fixture, fake and scenario is built from.
 *
 * One place, so a failure message reads as a sentence: `01JQ9F8Z6N0000000000000003` is *the* ride
 * in every test in every module, not a ULID someone typed. The ids are deliberately shaped
 * `01JQ9F8Z6N` + a zero-padded ordinal — valid Crockford base32 of the right length, distinct from
 * one another, and greppable.
 *
 * | id | entity |
 * |---|---|
 * | `…0001` | passenger |
 * | `…0002` | driver |
 * | `…0003` | ride |
 * | `…0004` | vehicle |
 * | `…0005` | device |
 * | `…0006` | wallet transaction |
 * | `…0007` | subscription |
 * | `…0008` | subscriber |
 * | `…0009` | recipient (package) |
 * | `…0010` | fleet owner |
 * | `…0011` | support ticket |
 * | `…0012` | trip / tracking session |
 *
 * **Time.** [NOW] is `2026-07-27T04:15:00Z`, which is `09:45` on the 27th in `Asia/Colombo` — the
 * middle of a business day on purpose, so a test that is not *about* the D-13 date boundary never
 * accidentally straddles it. [MIDNIGHT_EDGE] is the instant that does straddle it, for the tests
 * that are.
 *
 * (`data/models` has its own `Sample` object from C012. That one is local to the round-trip tests
 * and predates this kit; it is not a second source of truth for anything outside that package.)
 */
@Suppress("TooManyFunctions")
public object Fixtures {

    // ---- identities ---------------------------------------------------------------------

    /** The passenger in every scenario. */
    public const val PASSENGER_ID: Ulid = "01JQ9F8Z6N0000000000000001"

    /** The driver in every scenario. */
    public const val DRIVER_ID: Ulid = "01JQ9F8Z6N0000000000000002"

    /** The ride in every scenario. */
    public const val RIDE_ID: Ulid = "01JQ9F8Z6N0000000000000003"

    /** The driver's vehicle. */
    public const val VEHICLE_ID: Ulid = "01JQ9F8Z6N0000000000000004"

    /** The handset the driver app runs on. */
    public const val DEVICE_ID: Ulid = "01JQ9F8Z6N0000000000000005"

    /** A wallet ledger row. */
    public const val TRANSACTION_ID: Ulid = "01JQ9F8Z6N0000000000000006"

    /** A Mode B subscription. */
    public const val SUBSCRIPTION_ID: Ulid = "01JQ9F8Z6N0000000000000007"

    /** A Mode B subscriber (the passenger side of that subscription). */
    public const val SUBSCRIBER_ID: Ulid = "01JQ9F8Z6N0000000000000008"

    /** The recipient of a package delivery — not necessarily a registered user (P-07). */
    public const val RECIPIENT_ID: Ulid = "01JQ9F8Z6N0000000000000009"

    /** The fleet owner who owns [VEHICLE_ID]. */
    public const val OWNER_ID: Ulid = "01JQ9F8Z6N0000000000000010"

    /** A support ticket. */
    public const val TICKET_ID: Ulid = "01JQ9F8Z6N0000000000000011"

    /** A Mode A/B tracking session — trip-state-svc's aggregate, never a Mode C ride. */
    public const val TRIP_ID: Ulid = "01JQ9F8Z6N0000000000000012"

    /** The passenger's number, in the `^\+947\d{8}$` shape `_shared.yaml` requires. */
    public const val PASSENGER_PHONE: PhoneE164 = "+94771234567"

    /** The driver's number. */
    public const val DRIVER_PHONE: PhoneE164 = "+94777654321"

    /** [PASSENGER_PHONE] as a directory read sees it (AL-40/41/42). */
    public const val PASSENGER_PHONE_MASKED: PhoneMasked = "+9477*****67"

    /** The four-digit ride / package OTP (`^\d{4}$`). */
    public const val OTP: String = "4271"

    // ---- time ---------------------------------------------------------------------------

    /**
     * The instant every fixture is dated from — `09:45` on 2026-07-27 in `Asia/Colombo`.
     *
     * Mid-morning on purpose: a peak window, a night surcharge and a business-date roll are all
     * far enough away that a test which does not mean to touch them cannot.
     */
    @OptIn(ExperimentalTime::class)
    public val NOW: Timestamp = Instant.parse("2026-07-27T04:15:00Z")

    /**
     * `2026-07-27T19:00:00Z` — already the 28th in Colombo (D-13, D-38).
     *
     * For the tests that are about the boundary: anything answering "today" from UTC gets the
     * wrong answer here, and that is the point.
     */
    @OptIn(ExperimentalTime::class)
    public val MIDNIGHT_EDGE: Timestamp = Instant.parse("2026-07-27T19:00:00Z")

    /** The Colombo business date [NOW] falls on. */
    public val TODAY: BusinessDate = BusinessCalendar.businessDate(NOW)

    /** The first of [TODAY]'s month — the shape every `period_month` column is CHECKed into. */
    public val PERIOD_MONTH: BusinessDate = BusinessCalendar.firstOfMonth(TODAY)

    /** The month after [PERIOD_MONTH] — a `nextDue`. */
    public val NEXT_PERIOD_MONTH: BusinessDate = BusinessCalendar.plusMonths(PERIOD_MONTH)

    // ---- geography ----------------------------------------------------------------------

    /** Colombo Fort — the pickup in every scenario. */
    public val PICKUP: Place = Place(lat = 6.933540, lng = 79.844780, address = "Colombo Fort")

    /** Bambalapitiya — the dropoff in every scenario, about 4 km from [PICKUP]. */
    public val DROPOFF: Place = Place(lat = 6.892390, lng = 79.856110, address = "Bambalapitiya")

    /** [PICKUP] as a bare point. */
    public val PICKUP_POINT: GeoPoint = GeoPoint(lat = PICKUP.lat, lng = PICKUP.lng)

    /** [DROPOFF] as a bare point. */
    public val DROPOFF_POINT: GeoPoint = GeoPoint(lat = DROPOFF.lat, lng = DROPOFF.lng)

    /** A GNSS sample at [PICKUP], with the accuracy a handset actually reports. */
    public val PICKUP_FIX: GeoPointWithAccuracy =
        GeoPointWithAccuracy(lat = PICKUP.lat, lng = PICKUP.lng, accuracy = 8.0)

    // ---- money --------------------------------------------------------------------------

    /** Rs 480.00 — the canonical Mode C fare, in minor units as everything else is. */
    public val FARE: Money = Money(amountMinor = 48_000L, currency = Currency.LKR)

    /** Rs 1,250.00 — the driver's opening wallet balance, comfortably over the D-08 gate. */
    public val WALLET_BALANCE: Money = Money(amountMinor = 125_000L, currency = Currency.LKR)

    /** Rs 50.00 — the D-05 cross-trip cancellation penalty. */
    public val CANCELLATION_PENALTY: Money = Money(amountMinor = 5_000L, currency = Currency.LKR)

    // ---- opaque server strings ------------------------------------------------------------

    /** The token `GET /v1/fare/estimate` mints and `POST /v1/rides/request` must echo. */
    public const val FARE_ESTIMATE_TOKEN: String = "fet_01JQ9F8Z6N5R7T2V4X6Y8A0B2C"

    /** A cursor, for the second page of any paged read. */
    public const val CURSOR: String = "eyJvIjoyMH0"

    /** The passenger's client-side request id — the idempotency partner of the header key (R-18). */
    public const val CLIENT_REQUEST_ID: Ulid = "01JQ9F8Z6N000000000000CR01"

    /** A public trip-share handle. Not a credential — it is meant to be sent to a relative. */
    public const val TRIP_SHARE_TOKEN: String = "ts_4Kq8Rm2Xw9"

    /** A CDN asset, for any DTO carrying a photo or a document. */
    public const val ASSET_URL: String = "https://cdn.mageride.lk/asset.png"

    /**
     * A day [days] after [TODAY], for a subscription roll or a retention sweep.
     */
    public fun daysFromToday(days: Int): BusinessDate = BusinessCalendar.plusDays(TODAY, days)

    /** A `LocalDate` spelled out, for a fixture that needs a specific day. */
    public fun date(year: Int, month: Int, day: Int): BusinessDate = LocalDate(year, month, day)
}
