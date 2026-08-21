package lk.mageride.driver.vehicle

import androidx.annotation.StringRes
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import lk.mageride.driver.onboarding.OnboardingErrors
import lk.mageride.shared.data.models.VehicleType
import lk.mageride.shared.data.models.registry.OnboardingStatus
import lk.mageride.shared.data.models.registry.OnboardingStep
import lk.mageride.shared.data.models.registry.OnboardingStepVerdicts
import lk.mageride.shared.data.models.registry.RegistrationStatus
import lk.mageride.shared.data.models.registry.StepVerdict

/**
 * SCR-DA-006's state — the four-document verdict list.
 *
 * @property registrationNumber The plate, for the *"Sedan · ABC-1234"* header.
 * @property vehicleType The type in the same header.
 * @property verdicts The four verdicts. `null` while the first read is in flight.
 * @property registrationStatus The vehicle's own status; [RegistrationStatus.APPROVED] is the
 *   auto-approval AL-27 makes happen with no officer step.
 * @property onboardingStatus Derived incomplete / approved (AL-30).
 * @property rejectionReason Why it was rejected, when it was — US-2.15's re-upload prompt.
 * @property loading A read is in flight.
 * @property error Resolved copy for the last failure.
 * @property unknownVehicle Nothing named a vehicle for this screen. Not an error: it is what a
 *   restore onto a deactivated vehicle looks like, and the screen offers a way back.
 */
internal data class VehicleOnboardingStatusState(
    val registrationNumber: String? = null,
    val vehicleType: VehicleType? = null,
    val verdicts: OnboardingStepVerdicts? = null,
    val registrationStatus: RegistrationStatus? = null,
    val onboardingStatus: OnboardingStatus? = null,
    val rejectionReason: String? = null,
    val loading: Boolean = true,
    @param:StringRes val error: Int? = null,
    val unknownVehicle: Boolean = false,
) {

    /** The four rows, in the order the wireframe lists them. */
    val rows: List<Pair<OnboardingStep, StepVerdict>>
        get() = verdicts?.let { steps -> OnboardingStep.entries.map { it to steps.verdictFor(it) } }.orEmpty()

    /** How many documents are not yet verified — the wireframe's *"⚠ 1 pending → officer"*. */
    val pendingCount: Int get() = rows.count { (_, verdict) -> verdict != StepVerdict.VERIFIED }

    /**
     * Whether the vehicle came out the other side approved.
     *
     * Both halves, because AL-30 makes them different questions: `onboardingStatus` says the four
     * steps are done and `status` says the registration stands. C029's decision (5) is why they can
     * disagree — a renewal whose scan was blurry takes `onboardingStatus` back to incomplete and
     * leaves an APPROVED vehicle on the road until E-03's expiry sweep says otherwise.
     */
    val isApproved: Boolean
        get() = registrationStatus == RegistrationStatus.APPROVED && onboardingStatus == OnboardingStatus.APPROVED

    /** US-2.15 — rejected, with a reason, and the driver has to re-upload. */
    val isRejected: Boolean get() = registrationStatus == RegistrationStatus.REJECTED
}

/**
 * **SCR-DA-006 · vehicle onboarding status** — the four-document verdict list (Change 6/22).
 *
 * > *"All Verified → vehicle status APPROVED automatically (no human step) → appears in My
 * > Vehicles as Approved … Any Pending → Verification Officer queue (US-2.10)."*
 *
 * **The transition is the server's, and this screen only reports it.** registry-svc sets
 * `status=APPROVED` when the fourth step verifies (AL-27, and the save that caused it says so in
 * its own response), so there is nothing here to poll for and nothing to decide. What the screen
 * does own is [refresh]: a Pending document is confirmed by a Verification Officer minutes or days
 * later, and US-2.14's FCM push is what tells the driver to come back and look.
 */
internal class VehicleOnboardingStatusViewModel(
    private val vehicles: VehicleOnboardingRepository,
    private val session: VehicleOnboardingSession,
) : ViewModel() {

    private val mutableState = MutableStateFlow(VehicleOnboardingStatusState())

    val state: StateFlow<VehicleOnboardingStatusState> = mutableState.asStateFlow()

    init {
        refresh()
    }

    /**
     * *"Continue"* — names this vehicle for the wizard before the screen navigates to it.
     *
     * Without it the wizard would fall back to searching for something `INCOMPLETE`, and this
     * screen already knows which vehicle it is about: a driver with two unfinished vehicles would
     * be sent to whichever `GET /v1/vehicles/mine` returned first, not the one whose verdicts they
     * are reading. Nothing happens when the vehicle is unknown — that state has no CTA.
     */
    fun continueOnboarding() {
        session.vehicleId.value?.let(session::resumeVehicle)
    }

    /** Re-reads the verdicts. The screen's own action, and what a US-2.14 push brings a driver to. */
    @Suppress("TooGenericExceptionCaught")
    fun refresh() {
        val vehicleId = session.vehicleId.value
        if (vehicleId == null) {
            mutableState.update { it.copy(loading = false, unknownVehicle = true) }
            return
        }

        mutableState.update { it.copy(loading = true, error = null) }
        viewModelScope.launch {
            try {
                // Two reads: the verdicts, and the vehicle whose plate and type the header shows.
                val status = vehicles.onboardingStatus(vehicleId)
                val vehicle = vehicles.vehicle(vehicleId)

                mutableState.update {
                    it.copy(
                        registrationNumber = vehicle.registrationNumber,
                        vehicleType = vehicle.vehicleType,
                        verdicts = status.steps,
                        registrationStatus = status.status,
                        onboardingStatus = status.onboardingStatus,
                        rejectionReason = vehicle.rejectionReason,
                        loading = false,
                    )
                }
            } catch (cause: CancellationException) {
                throw cause
            } catch (cause: Throwable) {
                mutableState.update { it.copy(loading = false, error = OnboardingErrors.messageFor(cause)) }
            }
        }
    }
}
