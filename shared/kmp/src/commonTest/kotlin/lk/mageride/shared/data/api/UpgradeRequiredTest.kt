package lk.mageride.shared.data.api

import app.cash.turbine.test
import io.ktor.http.HttpStatusCode
import kotlinx.coroutines.test.runTest
import lk.mageride.shared.data.models.ErrorCode
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertTrue

/**
 * D-31: the gateway reads `X-App-Version` + `X-Platform` and answers `426` below the floor.
 *
 * Because the gate runs at the edge on every route, *any* of the 176 operations can answer this.
 * The typed error still reaches the caller — its own flow must not carry on — and the same payload
 * is published once on [MageRideApiSignals.upgradeRequired] for the app shell.
 */
class UpgradeRequiredTest {

    @Test
    fun a_426_surfaces_as_a_typed_upgrade_required_error() = runTest {
        val test = testApi { _, _ -> respondProblem(HttpStatusCode.UpgradeRequired, UPGRADE_CODE, UPGRADE_EXTENSIONS) }

        val error = assertFailsWith<MageRideError.UpgradeRequired> { test.api.ride.getRide("01RIDE") }

        assertEquals("https://play.google.com/store/apps/details?id=lk.mageride.passenger", error.updateUrl)
        assertEquals("2.0.0", error.latestVersion)
        assertTrue(error.isMandatory)
        assertEquals(ErrorCode.UPGRADE_REQUIRED, error.code)
    }

    @Test
    fun a_426_is_also_published_as_a_signal() = runTest {
        val test = testApi { _, _ -> respondProblem(HttpStatusCode.UpgradeRequired, UPGRADE_CODE, UPGRADE_EXTENSIONS) }

        assertFailsWith<MageRideError.UpgradeRequired> { test.api.wallet.getWallet("01USER") }

        test.signals.upgradeRequired.test {
            val signal = awaitItem()
            assertEquals("2.0.0", signal.latestVersion)
            assertTrue(signal.isMandatory)
        }
    }

    @Test
    fun the_signal_replays_so_a_later_subscriber_still_sees_it() = runTest {
        // The failing call is usually made before the shell has a collector attached.
        val test = testApi { _, _ -> respondProblem(HttpStatusCode.UpgradeRequired, UPGRADE_CODE, UPGRADE_EXTENSIONS) }

        assertFailsWith<MageRideError.UpgradeRequired> { test.api.ride.getRide("01RIDE") }
        assertFailsWith<MageRideError.UpgradeRequired> { test.api.ride.getRide("02RIDE") }

        test.signals.upgradeRequired.test {
            assertEquals("2.0.0", awaitItem().latestVersion)
        }
    }

    @Test
    fun a_soft_nudge_is_distinguishable_from_a_hard_block() = runTest {
        val test = testApi { _, _ ->
            respondProblem(
                status = HttpStatusCode.UpgradeRequired,
                code = UPGRADE_CODE,
                extensions = """"latestVersion":"1.5.0","updateUrl":"https://mageride.lk/app","isMandatory":false""",
            )
        }

        val error = assertFailsWith<MageRideError.UpgradeRequired> { test.api.ride.getRide("01RIDE") }

        assertTrue(!error.isMandatory)
    }

    @Test
    fun a_426_without_its_extensions_is_treated_as_mandatory() = runTest {
        // Fail closed: an update wall the user can dismiss by accident is worse than one they
        // cannot, when the server has told us the build is below the floor.
        val test = testApi { _, _ -> respondProblem(HttpStatusCode.UpgradeRequired, UPGRADE_CODE) }

        val error = assertFailsWith<MageRideError.UpgradeRequired> { test.api.ride.getRide("01RIDE") }

        assertTrue(error.isMandatory)
    }

    @Test
    fun a_426_is_never_retried() = runTest {
        val test = testApi { _, _ -> respondProblem(HttpStatusCode.UpgradeRequired, UPGRADE_CODE, UPGRADE_EXTENSIONS) }

        assertFailsWith<MageRideError.UpgradeRequired> { test.api.ride.getRide("01RIDE") }

        assertEquals(1, test.requests.size, "no build gets younger between two attempts")
    }

    @Test
    fun the_cold_start_version_check_publishes_the_same_signal() = runTest {
        // One update screen, fed from one place, whether the app asked at start-up or found out
        // the hard way mid-session.
        val test = testApi { _, _ ->
            respondJson(
                """
                {"updateRequired":true,"latestVersion":"2.0.0",
                 "updateUrl":"https://mageride.lk/app","isMandatory":true}
                """.trimIndent(),
            )
        }

        val result = test.api.version.checkAppVersion()

        assertTrue(result.updateRequired)
        test.signals.upgradeRequired.test {
            assertEquals("2.0.0", awaitItem().latestVersion)
        }
    }

    @Test
    fun a_version_check_that_passes_publishes_nothing() = runTest {
        val test = testApi { _, _ ->
            respondJson(
                """{"updateRequired":false,"latestVersion":"1.4.0","updateUrl":"u","isMandatory":false}""",
            )
        }

        test.api.version.checkAppVersion()

        assertEquals(0, test.signals.upgradeRequired.replayCache.size)
    }

    private companion object {
        val UPGRADE_CODE = ErrorCode.UPGRADE_REQUIRED.wire
        const val UPGRADE_EXTENSIONS = """
            "latestVersion":"2.0.0",
            "updateUrl":"https://play.google.com/store/apps/details?id=lk.mageride.passenger",
            "isMandatory":true
        """
    }
}
