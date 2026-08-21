package lk.mageride.driver.vehicle

import kotlinx.coroutines.runBlocking
import lk.mageride.driver.onboarding.MainDispatcher
import lk.mageride.driver.onboarding.await
import lk.mageride.shared.data.models.ServiceMode
import lk.mageride.shared.data.models.VehicleType
import lk.mageride.shared.data.models.registry.OnboardingStatus
import lk.mageride.shared.data.models.registry.OnboardingStep
import lk.mageride.shared.data.models.registry.RegistrationStatus
import lk.mageride.shared.data.models.registry.StepVerdict
import lk.mageride.shared.data.models.registry.VehicleListResponse
import lk.mageride.shared.testing.fake.FakeApiBackend
import lk.mageride.shared.testing.fake.mageRideApi
import kotlin.test.AfterTest
import kotlin.test.BeforeTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * SCR-DA-026 / 026a — the go-live gate (US-9.6), the two groups, and the empty-state popup.
 *
 * The DoD lines: *"My Vehicles list with per-vehicle Incomplete (Resume → next step) / Approved,
 * temporarily-assigned group, deactivate confirm"*, *"only an Approved Mode C vehicle can be
 * selected to go live"* and *"empty-state popup routing to Step 1/4"*.
 */
class VehiclesViewModelTest {

    private val main = MainDispatcher()
    private val backend = FakeApiBackend()
    private val session = VehicleOnboardingSession()
    private val activeVehicle = FakeActiveVehicleStore()

    @BeforeTest
    fun setUp() {
        main.install()
    }

    @AfterTest
    fun tearDown() {
        main.uninstall()
    }

    @Test
    fun an_empty_list_raises_the_onboard_a_mode_c_vehicle_popup() = runBlocking {
        backend.returns("listMyVehicles", VehicleListResponse(emptyList()))

        val model = viewModel()
        model.state.await { !it.loading }

        assertTrue(model.state.value.isEmpty)
        assertTrue(model.state.value.onboardPromptVisible, "SCR-DA-026a")
        assertFalse(model.state.value.canGoOnline, "US-9.6 — no vehicle, no going online")

        model.dismissOnboardPrompt()
        assertFalse(model.state.value.onboardPromptVisible, "\"Not now\" leaves the empty list")
    }

    @Test
    fun owned_mode_c_vehicles_and_temporarily_assigned_fleet_ones_are_two_groups() = runBlocking {
        // The Driver App onboards Mode C only (AL-27), so a Mode A/B vehicle in this driver's list
        // arrived by fleet assignment or share (US-13.9) — there is no other way for one to be
        // there, and that is what splits the wireframe's two groups.
        backend.returns(
            "listMyVehicles",
            VehicleListResponse(
                listOf(
                    summary(status = RegistrationStatus.APPROVED, onboardingStatus = OnboardingStatus.APPROVED),
                    summary(
                        vehicleId = OTHER_VEHICLE_ID,
                        registrationNumber = "VN-3321",
                        vehicleType = VehicleType.VAN,
                        mode = ServiceMode.B,
                        status = RegistrationStatus.APPROVED,
                        onboardingStatus = OnboardingStatus.APPROVED,
                    ),
                ),
            ),
        )

        val model = viewModel()
        model.state.await { !it.loading }

        assertEquals(listOf(VEHICLE_ID), model.state.value.owned.map { it.vehicleId })
        assertEquals(listOf(OTHER_VEHICLE_ID), model.state.value.assigned.map { it.vehicleId })
    }

    @Test
    fun only_an_approved_mode_c_vehicle_can_be_selected_to_go_live() = runBlocking {
        // US-9.6 / D5' §14.1a. AL-30 makes `onboarding_status` the gate and `status` the
        // registration decision, and a vehicle needs both.
        backend.returns(
            "listMyVehicles",
            VehicleListResponse(
                listOf(
                    // Registered but still walking the wizard.
                    summary(status = RegistrationStatus.APPROVED, onboardingStatus = OnboardingStatus.INCOMPLETE),
                    summary(
                        vehicleId = OTHER_VEHICLE_ID,
                        registrationNumber = "CAB-9920",
                        status = RegistrationStatus.APPROVED,
                        onboardingStatus = OnboardingStatus.APPROVED,
                    ),
                ),
            ),
        )
        backend.returns(
            "getVehicleOnboardingStatus",
            onboardingStatus(
                steps = verdicts(details = StepVerdict.VERIFIED, insurance = StepVerdict.VERIFIED),
                nextStep = OnboardingStep.REVENUE,
            ),
        )

        val model = viewModel()
        model.state.await { !it.loading }

        val incomplete = model.state.value.owned.first { it.vehicleId == VEHICLE_ID }
        val approved = model.state.value.owned.first { it.vehicleId == OTHER_VEHICLE_ID }

        assertFalse(incomplete.canGoLive)
        assertTrue(approved.canGoLive)
        // The wireframe prints "Incomplete · Step 3 of 4", not just "Incomplete" — a driver who
        // knows which step they are on finishes; one told only that something is missing does not.
        assertEquals(OnboardingStep.REVENUE, incomplete.nextStep)
        assertEquals(3, incomplete.nextStepNumber)

        model.select(incomplete)
        assertNull(model.state.value.activeVehicleId, "an Incomplete vehicle cannot be made live")

        model.select(approved)
        assertEquals(OTHER_VEHICLE_ID, model.state.value.activeVehicleId)
        assertEquals(OTHER_VEHICLE_ID, activeVehicle.activeVehicleId, "and it survives a restart")
        assertTrue(model.state.value.canGoOnline)
    }

    @Test
    fun a_shared_mode_a_or_b_vehicle_is_go_live_eligible_without_being_onboarded_here() = runBlocking {
        // D5' §14.1a's other half: "or a shared / temporarily-assigned Mode A/B vehicle". The
        // Fleet Portal approved it, so it carries no onboarding steps of its own to be gated on.
        backend.returns(
            "listMyVehicles",
            VehicleListResponse(
                listOf(
                    summary(
                        mode = ServiceMode.A,
                        status = RegistrationStatus.APPROVED,
                        onboardingStatus = OnboardingStatus.INCOMPLETE,
                    ),
                ),
            ),
        )

        val model = viewModel()
        model.state.await { !it.loading }

        assertTrue(model.state.value.assigned.single().canGoLive)
        assertTrue(model.state.value.canGoOnline)
    }

    @Test
    fun deactivating_the_live_vehicle_clears_the_selection() = runBlocking {
        // Going online as a vehicle the platform has retired is a connection that fails with no
        // way for the driver to see why.
        activeVehicle.activeVehicleId = VEHICLE_ID
        backend.returns(
            "listMyVehicles",
            VehicleListResponse(
                listOf(summary(status = RegistrationStatus.APPROVED, onboardingStatus = OnboardingStatus.APPROVED)),
            ),
        )

        val model = viewModel()
        model.state.await { !it.loading }
        assertEquals(VEHICLE_ID, model.state.value.activeVehicleId)

        val row = model.state.value.owned.single()
        model.confirmDeactivate(row)
        assertEquals(row, model.state.value.deactivating, "US-2.16 confirms before removing")

        backend.returns("listMyVehicles", VehicleListResponse(emptyList()))
        model.deactivate()
        model.state.await { !it.loading }

        assertTrue(backend.called("deactivateVehicle"))
        assertNull(model.state.value.activeVehicleId)
        assertNull(activeVehicle.activeVehicleId)
    }

    @Test
    fun a_selection_that_is_no_longer_in_the_list_is_dropped_on_refresh() = runBlocking {
        // Deactivated from another device, or the fleet assignment expired.
        activeVehicle.activeVehicleId = "01JVEHICLE0000000000000009"
        backend.returns(
            "listMyVehicles",
            VehicleListResponse(
                listOf(summary(status = RegistrationStatus.APPROVED, onboardingStatus = OnboardingStatus.APPROVED)),
            ),
        )

        val model = viewModel()
        model.state.await { !it.loading }

        assertNull(model.state.value.activeVehicleId)
        assertNull(activeVehicle.activeVehicleId)
    }

    @Test
    fun the_plus_and_resume_are_two_different_instructions_to_the_wizard() = runBlocking {
        // They used to be one: both navigated to the same argument-less route and let the wizard
        // search for something INCOMPLETE, so ＋ was Resume wearing a different icon. The route
        // still carries no arguments — the intent goes through the session instead.
        backend.returns(
            "listMyVehicles",
            VehicleListResponse(listOf(summary(vehicleId = OTHER_VEHICLE_ID, registrationNumber = "ZZZ-9999"))),
        )

        val model = viewModel()
        model.state.await { !it.loading }
        val row = model.state.value.owned.single()

        model.startNewVehicle()
        assertEquals(WizardIntent.NewVehicle, session.consumeIntent(), "＋ adds")

        model.resumeOnboarding(row)
        assertEquals(WizardIntent.Continue(OTHER_VEHICLE_ID), session.consumeIntent(), "Resume continues that row")
    }

    @Test
    fun an_intent_belongs_to_one_visit_and_is_not_inherited_by_the_next() = runBlocking {
        // The session is process-wide. A ＋ that was never followed through must not turn the Menu
        // tab's "Vehicle Onboarding" row into a fresh start half an hour later.
        val model = viewModel()

        model.startNewVehicle()

        assertEquals(WizardIntent.NewVehicle, session.consumeIntent())
        assertNull(session.consumeIntent(), "read once, by the wizard that was opened")
    }

    @Test
    fun opening_a_row_for_its_verdicts_is_not_an_instruction_to_the_wizard() = runBlocking {
        // SCR-DA-006 needs the vehicle named; that is a different question from why the wizard was
        // opened, and answering both with one field is what made ＋ ambiguous in the first place.
        backend.returns("listMyVehicles", VehicleListResponse(listOf(summary())))

        val model = viewModel()
        model.state.await { !it.loading }

        model.open(model.state.value.owned.single())

        assertEquals(VEHICLE_ID, session.vehicleId.value, "named for the status screen")
        assertNull(session.consumeIntent(), "but the wizard was not told to do anything")
    }

    private fun viewModel(): VehiclesViewModel = VehiclesViewModel(
        vehicles = VehicleOnboardingRepository(backend.mageRideApi().registry),
        session = session,
        activeVehicle = activeVehicle,
    )
}
