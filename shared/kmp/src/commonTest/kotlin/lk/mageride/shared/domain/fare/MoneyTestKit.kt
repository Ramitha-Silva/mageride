package lk.mageride.shared.domain.fare

import kotlinx.datetime.LocalDate
import kotlinx.datetime.LocalDateTime
import kotlinx.datetime.toInstant
import lk.mageride.shared.data.models.PaymentState
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.fare.PaymentMethod
import lk.mageride.shared.data.models.fare.PaymentStatus
import lk.mageride.shared.util.BusinessCalendar

// Shared fixtures for the C016 money tests.
//
// Everything here builds an instant from an ASIA/COLOMBO wall clock, because every rule under test
// — the peak and night windows, the fee date, a subscription's due date — is stated in Colombo
// local time (D-38). A test that wrote `Instant.parse("...Z")` would be asserting about UTC and
// would pass or fail for the wrong reason.

/** A date in the tests' reference month. */
internal fun day(day: Int, month: Int = 7, year: Int = 2026): LocalDate = LocalDate(year, month, day)

/** An instant at a given **Colombo** wall-clock time on 27 July 2026. */
internal fun colombo(hour: Int, minute: Int = 0, day: Int = 27, month: Int = 7, year: Int = 2026): Timestamp =
    LocalDateTime(year, month, day, hour, minute).toInstant(BusinessCalendar.ZONE)

/** Midday — outside every default peak and night window. */
internal val OFF_PEAK: Timestamp = colombo(12)

/** 08:00 Colombo — inside the morning peak window. */
internal val MORNING_PEAK: Timestamp = colombo(8)

/** 23:00 Colombo — inside the night window, which wraps midnight. */
internal val NIGHT: Timestamp = colombo(23)

/** The one payment every projection test is about. */
internal const val TEST_PAYMENT_ID: String = "01JPAY0000000000000000000"

/** The ride it settles. */
internal const val TEST_RIDE_ID: String = "01JRIDE000000000000000000"

/** A `PaymentStatus` in [state], on the one payment the projection tests key on. */
internal fun paymentStatus(
    state: PaymentState,
    method: PaymentMethod = PaymentMethod.CASH,
    amountMinor: Long = 48_000,
    surchargeMinor: Long? = null,
    tipMinor: Long? = null,
): PaymentStatus = PaymentStatus(
    paymentId = TEST_PAYMENT_ID,
    rideId = TEST_RIDE_ID,
    state = state,
    method = method,
    amountMinor = amountMinor,
    surchargeMinor = surchargeMinor,
    tipMinor = tipMinor,
)
