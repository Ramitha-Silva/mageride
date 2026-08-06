package lk.mageride.passenger

import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelStore
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.test.resetMain
import kotlinx.coroutines.test.setMain
import kotlinx.coroutines.withTimeout
import lk.mageride.shared.testing.fake.FakeApiBackend
import kotlin.time.Duration.Companion.milliseconds
import kotlin.time.Duration.Companion.seconds

// The view-model test harness, shared by every screen group.
//
// It began as C077's `OnboardingTestKit`; C078 is the second cluster to need it, which is the point
// at which "the onboarding tests' helper" becomes "the module's helper" and moves to the root test
// package. `FakeAppPreferences` stayed behind — it is the onboarding cluster's own seam.

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
     * and `viewModelScope` is `Dispatchers.Main.immediate`. SCR-PA-003's resend countdown is a
     * `while (true) { delay(1.seconds) }`, so one left running wakes up inside the *next* class's
     * `Dispatchers.resetMain()` and kotlinx reports *"Dispatchers.Main is used concurrently with
     * setting it"* against a test that did nothing wrong. The driver app hit exactly this in C073.
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

/**
 * Waits for [operationId] to have reached the fake backend (Δ C084).
 *
 * The counterpart to [await] for a write that publishes **nothing** on success.
 * `SettingsViewModel.pushDefaultPayment` is the case: it touches state only when the call *fails*,
 * so there is no `StateFlow` value that means "the request went" — and awaiting the value the
 * screen set *before* launching it passes on the previous state, which is the trap
 * `apps/passenger-android/CLAUDE.md` records for SCR-PA-008's prediction list.
 *
 * Polled rather than suspended on a signal because [lk.mageride.shared.testing.fake.FakeApiBackend]
 * is a real Ktor client over MockEngine and resolves on its own engine dispatcher, which no virtual
 * clock can advance past. The timeout is what turns "the call never went" into a named failure
 * rather than a hung Gradle worker.
 */
internal suspend fun FakeApiBackend.awaitCall(operationId: String): Unit = withTimeout(AWAIT_TIMEOUT) {
    while (!called(operationId)) delay(POLL_INTERVAL)
}

private val AWAIT_TIMEOUT = 5.seconds

/** Short enough to add nothing measurable to a passing test, long enough not to spin a core. */
private val POLL_INTERVAL = 5.milliseconds
