package lk.mageride.driver.onboarding

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.test.resetMain
import kotlinx.coroutines.test.setMain
import kotlinx.coroutines.withTimeout
import lk.mageride.driver.capture.CapturedImage
import lk.mageride.shared.data.models.Language
import lk.mageride.shared.data.models.Ulid
import kotlin.time.Duration.Companion.seconds

/**
 * `OnboardingPreferences` in memory.
 *
 * The production one is `SharedPreferences`, whose local-unit-test stub answers a default for
 * every member — a view model tested against it would report a first run that had already been
 * completed and vice versa.
 */
internal class FakeOnboardingPreferences(
    override var language: Language? = null,
    override var operatingCityCode: String? = null,
    override var preferencesPendingSync: Boolean = false,
    override var permissionsAcknowledged: Boolean = false,
) : OnboardingPreferences

/**
 * A [DriverDocumentUploader] that succeeds, and remembers what it was handed.
 *
 * Stands in for the upload route that does not exist yet (see [DriverDocumentUploader]); it is
 * what lets the rest of Profile Setup be tested end to end today, and what the real binding will
 * be swapped for when the route lands.
 */
internal class RecordingDocumentUploader : DriverDocumentUploader {

    /** Every upload, in the order Profile Setup made them. */
    val uploads: MutableList<DriverDocumentKind> = mutableListOf()

    override suspend fun upload(kind: DriverDocumentKind, image: CapturedImage): Ulid {
        uploads += kind
        return "01JUPLOAD${kind.ordinal}0000000000000000".take(ULID_LENGTH)
    }

    private companion object {
        const val ULID_LENGTH = 26
    }
}

/** A one-pixel stand-in for a captured document. Nothing here reads the bytes. */
internal fun testImage(name: String): CapturedImage =
    CapturedImage(fileName = name, bytes = ByteArray(size = 1), mimeType = "image/jpeg")

/**
 * Puts `Dispatchers.Main` under test control for a `ViewModel`.
 *
 * `viewModelScope` is `Dispatchers.Main.immediate`, which does not exist off a device.
 * [Dispatchers.Unconfined] rather than a `TestDispatcher`, and the tests are `runBlocking` rather
 * than `runTest`, for one reason: a call through [lk.mageride.shared.testing.fake.FakeApiBackend]
 * is a **real** Ktor client over MockEngine, and MockEngine resolves on its own engine dispatcher.
 * No virtual clock can advance past that, so the tests wait for the state they expect ([await])
 * instead of pretending the work is schedulable.
 */
internal class MainDispatcher {

    fun install() {
        Dispatchers.setMain(Dispatchers.Unconfined)
    }

    fun uninstall() {
        Dispatchers.resetMain()
    }
}

/**
 * Waits for [predicate] to hold, then answers the value that satisfied it.
 *
 * The timeout is what turns "the view model never got there" into a failure with the assertion's
 * own name on it rather than a test run that hangs until the Gradle worker is killed.
 */
internal suspend fun <T> StateFlow<T>.await(predicate: (T) -> Boolean): T =
    withTimeout(AWAIT_TIMEOUT) { first(predicate) }

private val AWAIT_TIMEOUT = 5.seconds
