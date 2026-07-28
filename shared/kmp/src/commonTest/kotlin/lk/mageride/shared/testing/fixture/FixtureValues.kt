package lk.mageride.shared.testing.fixture

/**
 * What [DtoFixtures] puts in a field, chosen from the field's **name**.
 *
 * A synthesised fixture is only useful if it is also *plausible*: `driverPhone` has to match
 * `_shared.yaml`'s `^\+947\d{8}$`, `otp` has to match `^\d{4}$`, and a `…Minor` field has to be an
 * integer number of cents rather than a 1. Contract checks compare these documents against the
 * OpenAPI schemas, and a screenshot taken against the fake backend is only worth looking at if the
 * numbers on it could have come from the platform.
 *
 * Everything unrecognised falls through to the field's own name, which makes an unexpected value
 * in a failure message point straight at the field that produced it.
 */
internal object FixtureValues {

    /** Serial names kotlinx gives the wire primitives that are not really strings. */
    private const val INSTANT = "kotlin.time.Instant"
    private const val LOCAL_DATE = "kotlinx.datetime.LocalDate"
    private const val LOCAL_TIME = "kotlinx.datetime.LocalTime"
    private const val LOCAL_DATE_TIME = "kotlinx.datetime.LocalDateTime"

    fun string(serialName: String, field: String): String =
        // A nullable field's descriptor is the same descriptor with a `?` on its serial name, and
        // a nullable `Timestamp` still has to be an ISO instant. Dropping the marker first is what
        // stops `scheduledAt` being filled in with the string "scheduledAt".
        wireType(serialName.removeSuffix("?")) ?: identity(field) ?: text(field)

    /** The four wire primitives whose shape is fixed by their type, not by their name. */
    private fun wireType(serialName: String): String? = when (serialName) {
        INSTANT -> Fixtures.NOW.toString()
        LOCAL_DATE -> Fixtures.TODAY.toString()
        LOCAL_TIME -> "09:45:00"
        LOCAL_DATE_TIME -> "2026-07-27T09:45:00"
        else -> null
    }

    /** Identifiers, phone numbers and the other pattern-constrained strings. */
    @Suppress("ReturnCount")
    private fun identity(field: String): String? {
        val lower = field.lowercase()
        if (field == "id" || field.endsWith("Id") || field.endsWith("Ids")) return identifierFor(lower)
        if (lower.contains("phone") || lower.contains("msisdn")) {
            return if (lower.contains("mask")) Fixtures.PASSENGER_PHONE_MASKED else Fixtures.PASSENGER_PHONE
        }
        if (lower.contains("url") || lower.contains("uri") || lower.endsWith("link")) return Fixtures.ASSET_URL
        if (lower.endsWith("otp")) return Fixtures.OTP
        return null
    }

    /** Which of the [Fixtures] ids an `…Id` field should carry, so a fixture graph joins up. */
    @Suppress("CyclomaticComplexMethod")
    private fun identifierFor(lower: String): String = when {
        lower.contains("passenger") || lower.contains("rider") -> Fixtures.PASSENGER_ID
        lower.contains("driver") -> Fixtures.DRIVER_ID
        lower.contains("recipient") -> Fixtures.RECIPIENT_ID
        lower.contains("vehicle") -> Fixtures.VEHICLE_ID
        lower.contains("device") -> Fixtures.DEVICE_ID
        lower.contains("owner") -> Fixtures.OWNER_ID
        lower.contains("subscriber") -> Fixtures.SUBSCRIBER_ID
        lower.contains("subscription") -> Fixtures.SUBSCRIPTION_ID
        lower.contains("ticket") -> Fixtures.TICKET_ID
        lower.contains("transaction") -> Fixtures.TRANSACTION_ID
        lower.contains("trip") || lower.contains("session") -> Fixtures.TRIP_ID
        lower.contains("clientrequest") -> Fixtures.CLIENT_REQUEST_ID
        lower.contains("ride") -> Fixtures.RIDE_ID
        else -> Fixtures.RIDE_ID
    }

    /** Free text, and the handful of opaque server strings that have a canonical value. */
    @Suppress("CyclomaticComplexMethod")
    private fun text(field: String): String {
        val lower = field.lowercase()
        return when {
            lower == "cursor" -> Fixtures.CURSOR

            lower.contains("fareestimatetoken") -> Fixtures.FARE_ESTIMATE_TOKEN

            lower.contains("etag") -> "W/\"cities-1\""

            lower.contains("token") -> Fixtures.TRIP_SHARE_TOKEN

            lower.contains("email") -> "qa@mageride.lk"

            lower.contains("address") -> Fixtures.PICKUP.address.orEmpty()

            lower.contains("polyline") -> "_p~iF~ps|U_ulLnnqC"

            lower.contains("plate") || lower.contains("registration") || lower == "vehicleno" -> "WP CAB-1234"

            lower.contains("name") -> "A. Perera"

            lower.contains("code") -> "CMB"

            lower.contains("reason") || lower.contains("note") || lower.contains("description") ->
                "fixture $field"

            else -> field.ifEmpty { "fixture" }
        }
    }

    /** Whole numbers. `version` is 1 because every aggregate starts there. */
    fun int(field: String): Int {
        val lower = field.lowercase()
        return when {
            lower.contains("version") -> 1
            lower.contains("pct") || lower.contains("percent") -> 20
            lower.contains("limit") -> 20
            lower.contains("second") -> 15
            lower.contains("minute") -> 5
            lower.contains("attempt") || lower.contains("remaining") -> 3
            lower.contains("level") -> 3
            lower.contains("count") || lower.contains("total") -> 2
            else -> 1
        }
    }

    /** Money is minor units and nothing else — a `…Minor` field is never a 1. */
    fun long(field: String): Long {
        val lower = field.lowercase()
        return when {
            lower.contains("minor") && lower.contains("penalt") -> Fixtures.CANCELLATION_PENALTY.amountMinor
            lower.contains("minor") && lower.contains("balance") -> Fixtures.WALLET_BALANCE.amountMinor
            lower.contains("minor") -> Fixtures.FARE.amountMinor
            lower.contains("bytes") || lower.contains("size") -> 2_048L
            lower.contains("seq") -> 1L
            else -> int(field).toLong()
        }
    }

    /** Decimals. Coordinates are Colombo's, so a fixture plotted on a map lands in the city. */
    fun double(field: String): Double {
        val lower = field.lowercase()
        return when {
            lower.endsWith("lat") || lower.contains("latitude") -> Fixtures.PICKUP.lat
            lower.endsWith("lng") || lower.contains("longitude") -> Fixtures.PICKUP.lng
            lower.contains("accuracy") -> 8.0
            lower.contains("confidence") -> 0.94
            lower.contains("rating") -> 4.8
            lower.contains("km") || lower.contains("distance") -> 4.2
            lower.contains("speed") -> 8.5
            lower.contains("heading") || lower.contains("bearing") -> 135.0
            else -> 1.0
        }
    }
}
