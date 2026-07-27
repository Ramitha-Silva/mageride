package lk.mageride.shared

import app.cash.turbine.test
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.flow
import kotlinx.coroutines.test.runTest
import kotlinx.datetime.TimeZone
import kotlinx.datetime.toLocalDateTime
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue
import kotlin.time.Clock
import kotlin.time.Duration.Companion.hours
import kotlin.time.ExperimentalTime
import kotlin.time.Instant

/**
 * The scaffold's smoke test for the shared toolchain: coroutines + `runTest`'s virtual clock,
 * Turbine's Flow assertions and kotlinx-datetime's Asia/Colombo zone. A wave-1 component that
 * finds one of these missing has a build-script problem, not a logic problem — this test says
 * which.
 */
@OptIn(ExperimentalTime::class)
class ToolchainTest {
    @Test
    fun turbine_observes_a_flow_on_the_test_dispatcher() = runTest {
        val positions = flow {
            emit(1)
            delay(4_000) // the §7.5 moving cadence; virtual time, so the test does not wait
            emit(2)
        }

        positions.test {
            assertEquals(1, awaitItem())
            assertEquals(2, awaitItem())
            awaitComplete()
        }
    }

    @Test
    fun asia_colombo_is_a_real_zone_and_is_utc_plus_5_30() {
        // D-38: every business date on this platform settles in Asia/Colombo. If the target's
        // tz database cannot resolve it, every date boundary in wave 3 is silently wrong.
        val colombo = TimeZone.of("Asia/Colombo")
        val midnightUtc = Instant.parse("2026-07-27T00:00:00Z")

        val local = midnightUtc.toLocalDateTime(colombo)

        assertEquals(5, local.hour)
        assertEquals(30, local.minute)
        assertEquals(2026, local.year)
    }

    @Test
    fun the_clock_advances_forward() {
        val before = Clock.System.now()

        assertTrue(before < before + 1.hours)
    }
}
