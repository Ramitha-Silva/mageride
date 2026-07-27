package lk.mageride.shared.data.models

import kotlinx.datetime.LocalDate
import lk.mageride.shared.serialization.MageRideJson
import kotlin.test.assertEquals
import kotlin.time.Instant

/**
 * Encodes [value] through [MageRideJson] and decodes it back, asserting the result is identical.
 *
 * This is the C012 definition of done — "every DTO round-trips through kotlinx.serialization" —
 * applied one DTO at a time. Each caller builds an instance with **every** property populated, so
 * a field that is missing a `@Serializable` annotation, collides on a serial name, or cannot
 * survive `explicitNulls = false` fails here rather than in an app.
 *
 * The encoded form is passed as the assertion message, so a failure shows the wire shape that
 * produced it.
 */
internal inline fun <reified T> assertRoundTrips(value: T) {
    val encoded = MageRideJson.encodeToString(value)
    assertEquals(value, MageRideJson.decodeFromString<T>(encoded), encoded)
}

/** Fixed sample values, so a round-trip test reads as a list of shapes rather than of literals. */
internal object Sample {
    const val ULID_A: Ulid = "01JQ9F8Z6N5R7T2V4X6Y8A0B2C"
    const val ULID_B: Ulid = "01JR9F8Z6N5R7T2V4X6Y8A0B2C"
    const val ULID_C: Ulid = "01JS9F8Z6N5R7T2V4X6Y8A0B2C"
    const val PHONE: PhoneE164 = "+94771234567"
    const val PHONE_MASKED: PhoneMasked = "+9477*****67"
    const val URL: String = "https://cdn.mageride.lk/asset.png"

    val AT: Timestamp = Instant.parse("2026-07-27T04:15:00Z")
    val LATER: Timestamp = Instant.parse("2026-07-27T05:15:00Z")
    val DAY: BusinessDate = LocalDate(2026, 7, 27)
    val MONTH: BusinessDate = LocalDate(2026, 8, 1)

    val PLACE: Place = Place(lat = 6.927079, lng = 79.861243, address = "Colombo Fort")
    val POINT: GeoPoint = GeoPoint(lat = 6.9271, lng = 79.8612)
    val POINT_WITH_ACCURACY: GeoPointWithAccuracy =
        GeoPointWithAccuracy(lat = 6.9271, lng = 79.8612, accuracy = 8.0)

    val EXTRACTED_FIELD: ExtractedField = ExtractedField(
        key = "licenceNo",
        value = "B1234567",
        source = FieldSource.OCR,
        confidence = 0.94,
        verifyStatus = VerifyStatus.CONFIRMED,
    )
}
