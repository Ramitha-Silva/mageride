package lk.mageride.driver.safety

import io.ktor.http.HttpStatusCode
import kotlinx.coroutines.runBlocking
import lk.mageride.driver.home.FakeDriverLocationSource
import lk.mageride.driver.home.fix
import lk.mageride.driver.home.signedInSessions
import lk.mageride.driver.jobs.JobsRepository
import lk.mageride.driver.onboarding.FakeOnboardingPreferences
import lk.mageride.driver.onboarding.MainDispatcher
import lk.mageride.driver.onboarding.await
import lk.mageride.driver.profile.ProfileRepository
import lk.mageride.driver.ride.RideContact
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.iam.EmergencyContact
import lk.mageride.shared.data.models.iam.EmergencyContactListResponse
import lk.mageride.shared.data.models.safety.SosDispatched
import lk.mageride.shared.data.models.safety.SosSmsStatus
import lk.mageride.shared.testing.fake.FakeApiBackend
import lk.mageride.shared.testing.fake.mageRideApi
import lk.mageride.shared.testing.fixture.Fixtures
import kotlin.test.AfterTest
import kotlin.test.BeforeTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/**
 * SCR-DA-032 — the driver's alarm.
 *
 * The definition-of-done line these carry: *"SOS reaches the server and shows the dispatched state
 * within the SLO"*. D-33's budget is p99 ≤ 5 s for the **dispatch**, so what is asserted here is
 * that nothing on the client's side of that boundary waits — the alarm sends on the tap, with the
 * fix already in hand, and the dispatched state is what the response says rather than a second read.
 */
class SosViewModelTest {

    private val main = MainDispatcher()
    private val backend = FakeApiBackend()
    private val location = FakeDriverLocationSource()

    @BeforeTest
    fun setUp() {
        main.install()
        backend.returns("listEmergencyContacts", EmergencyContactListResponse(items = listOf(contact())))
        backend.returns("triggerSos", dispatched(SosSmsStatus.DISPATCHED))
    }

    @AfterTest
    fun tearDown() {
        main.uninstall()
    }

    @Test
    fun the_alarm_carries_this_ride_the_last_fix_and_the_driver_role() = runBlocking {
        val model = viewModel()
        location.emit(fix())
        model.state.await { !it.awaitingPosition }

        model.raise()
        val state = model.state.await { it.stage == SosStage.DISPATCHED }

        val body = backend.lastCall("triggerSos").json
        assertEquals(Fixtures.RIDE_ID, body["rideId"]?.toString()?.trim('"'))
        assertEquals("driver", body["role"]?.toString()?.trim('"'), "SosRole.DRIVER — US-12.8")
        assertTrue(body.containsKey("lat") && body.containsKey("lng"), "the alarm carries a position")
        assertEquals(SosSmsStatus.DISPATCHED, state.smsStatus)
        assertTrue(state.isRaised)
    }

    @Test
    fun the_emergency_contact_the_sms_will_reach_is_shown_before_the_alarm_goes() = runBlocking {
        // AL-13 — the driver should be able to see WHO this reaches while there is still time to
        // cancel, which is the whole reason the countdown exists.
        val model = viewModel()
        val state = model.state.await { it.contactLoaded }

        assertEquals(CONTACT_NAME, state.contact?.name)
        assertFalse(state.warnsNoContact)
        assertEquals(SosStage.ARMED, state.stage)
    }

    @Test
    fun a_driver_with_no_contact_is_warned_and_can_still_raise_the_alarm() = runBlocking {
        // The alert is recorded and reaches the admin live feed with nobody to SMS, so refusing to
        // send would take away the half that works. safety-svc says which half managed it.
        backend.returns("listEmergencyContacts", EmergencyContactListResponse(items = emptyList()))
        backend.returns("triggerSos", dispatched(SosSmsStatus.NO_CONTACT))

        val model = viewModel()
        location.emit(fix())
        model.state.await { it.contactLoaded && !it.awaitingPosition }

        assertTrue(model.state.value.warnsNoContact)

        model.raise()
        val state = model.state.await { it.stage == SosStage.DISPATCHED }
        assertEquals(SosSmsStatus.NO_CONTACT, state.smsStatus)
    }

    @Test
    fun a_failed_sms_is_still_a_raised_alarm() = runBlocking {
        // `SosSmsStatus.FAILED` is NOT an error response: `SafetyModels` is explicit that the event
        // is recorded and is on the live feed either way. Telling somebody in trouble that nothing
        // happened would be the worst thing this screen could do.
        backend.returns("triggerSos", dispatched(SosSmsStatus.FAILED))

        val model = viewModel()
        location.emit(fix())
        model.state.await { !it.awaitingPosition }
        model.raise()

        val state = model.state.await { it.stage == SosStage.DISPATCHED }
        assertEquals(SosSmsStatus.FAILED, state.smsStatus)
        assertTrue(state.isRaised, "the alarm is up; only the SMS leg failed")
    }

    @Test
    fun a_request_that_never_reached_safety_svc_is_a_failure_with_a_retry() = runBlocking {
        backend.fails("triggerSos", HttpStatusCode.ServiceUnavailable, "service-unavailable")

        val model = viewModel()
        location.emit(fix())
        model.state.await { !it.awaitingPosition }
        model.raise()

        val failed = model.state.await { it.stage == SosStage.FAILED }
        assertFalse(failed.isRaised)

        backend.returns("triggerSos", dispatched(SosSmsStatus.DISPATCHED))
        model.retry()
        assertEquals(SosStage.ARMED, model.state.value.stage)
        model.raise()
        assertEquals(SosStage.DISPATCHED, model.state.await { it.stage == SosStage.DISPATCHED }.stage)
    }

    @Test
    fun cancelling_the_countdown_sends_nothing() = runBlocking {
        val model = viewModel()
        location.emit(fix())
        model.state.await { !it.awaitingPosition }

        model.cancelCountdown()

        assertFalse(backend.called("triggerSos"), "a cancelled alarm is not an alarm")
        assertEquals(SosStage.ARMED, model.state.value.stage)
    }

    @Test
    fun a_second_tap_does_not_raise_a_second_alarm() = runBlocking {
        // One emergency, one row on the operator's feed. A driver hammering the disc must not
        // produce five.
        val model = viewModel()
        location.emit(fix())
        model.state.await { !it.awaitingPosition }

        model.raise()
        model.state.await { it.stage == SosStage.DISPATCHED }
        model.raise()
        model.raise()

        assertEquals(1, backend.callsTo("triggerSos").size)
    }

    @Test
    fun with_no_fix_yet_the_alarm_is_not_sent_and_says_why() = runBlocking {
        // `TriggerSosRequest.lat`/`.lng` are required — there is no positionless form on the
        // app-facing contract, which is the spec gap the C075 handoff records. The screen waits
        // rather than sending an alarm with a coordinate it invented.
        val model = viewModel()
        val armed = model.state.await { it.contactLoaded }

        assertTrue(armed.awaitingPosition)
        model.raise()

        assertEquals(SosStage.FAILED, model.state.value.stage)
        assertFalse(backend.called("triggerSos"))
    }

    private fun contact() = EmergencyContact(
        contactId = CONTACT_ID,
        isPrimary = true,
        name = CONTACT_NAME,
        phone = Fixtures.DRIVER_PHONE,
    )

    private fun dispatched(status: SosSmsStatus) = SosDispatched(
        sosId = SOS_ID,
        dispatchedAt = Fixtures.NOW,
        smsStatus = status,
    )

    private suspend fun viewModel(): SosViewModel {
        val api = backend.mageRideApi()
        return main.own(
            SosViewModel(
                rideId = Fixtures.RIDE_ID,
                contact = RideContact(voip = api.voip, safety = api.safety),
                // SCR-DA-029's repository, because the contact SCR-DA-032 reaches is the one that
                // screen writes — C074's handoff names this as the seam between the two.
                profiles = ProfileRepository(
                    iam = api.iam,
                    jobs = JobsRepository(dispatch = api.dispatch),
                    sessions = signedInSessions(backend),
                    preferences = FakeOnboardingPreferences(),
                ),
                location = location,
            ),
        )
    }

    private companion object {
        const val CONTACT_ID: Ulid = "01JCONTACT000000000000001"
        const val SOS_ID: Ulid = "01JSOS0000000000000000001"

        /** The wireframe's *"Amma · +94 77 000 1111"*. */
        const val CONTACT_NAME = "Amma"
    }
}
