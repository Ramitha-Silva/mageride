package lk.mageride.driver.onboarding

import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelStore
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.test.resetMain
import kotlinx.coroutines.test.setMain
import kotlinx.coroutines.withTimeout
import lk.mageride.driver.capture.CapturedImage
import lk.mageride.shared.data.models.Language
import lk.mageride.shared.data.models.registry.CaptureSource
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
 * A one-pixel stand-in for a captured document. Nothing here reads the bytes.
 *
 * The capture source is part of the image (AL-43) — the scanner's by default, because that is what
 * a licence tile produces; the profile photo comes from the gallery and says so.
 */
internal fun testImage(name: String, capturedVia: CaptureSource = CaptureSource.CAMERA_DRAG_CROP): CapturedImage =
    CapturedImage(
        fileName = name,
        bytes = ByteArray(size = 1),
        mimeType = "image/jpeg",
        capturedVia = capturedVia,
    )

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

    /**
     * The view models this test class made, so [uninstall] can end them.
     *
     * `ViewModel.clear()` is `internal` to androidx and `onCleared()` is `protected`, so a
     * `ViewModelStore` is the only public door onto "this model is finished" — `put` and `clear`
     * are both public, and `clear` is what cancels `viewModelScope`.
     */
    private val store = ViewModelStore()
    private var owned = 0

    fun install() {
        Dispatchers.setMain(Dispatchers.Unconfined)
    }

    /**
     * Hands [model]'s lifetime to this dispatcher. Returns it, so a factory can wrap its result.
     *
     * **A view model with a loop in it outlives the test that made it unless something ends it**,
     * and `viewModelScope` is `Dispatchers.Main.immediate`. SCR-DA-003's resend countdown and
     * SCR-DA-022's gateway poll are both `while (…) { delay(…) }`, so one left running wakes up
     * inside the *next* class's `Dispatchers.resetMain()` and kotlinx reports
     * *"Dispatchers.Main is used concurrently with setting it"* against a test that did nothing
     * wrong. Δ C073, after exactly that failure.
     */
    fun <T : ViewModel> own(model: T): T {
        store.put("owned-${owned++}", model)
        return model
    }

    fun uninstall() {
        store.clear()
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
