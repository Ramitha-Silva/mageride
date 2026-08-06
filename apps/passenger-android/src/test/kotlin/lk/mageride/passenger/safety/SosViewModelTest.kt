package lk.mageride.passenger.safety

import io.ktor.http.HttpStatusCode
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.runBlocking
import lk.mageride.passenger.MainDispatcher
import lk.mageride.passenger.R
import lk.mageride.passenger.await
import lk.mageride.passenger.location.PassengerFix
import lk.mageride.passenger.location.PassengerLocationSource
import lk.mageride.passenger.ride.FakeRideRepository
import lk.mageride.passenger.settings.SosContacts
import lk.mageride.shared.data.api.MageRideError
import lk.mageride.shared.data.models.ErrorCode
import lk.mageride.shared.data.models.PhoneE164
import lk.mageride.shared.data.models.ProblemDetails
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.iam.EmergencyContact
import lk.mageride.shared.data.models.safety.SosSmsStatus
import kotlin.test.AfterTest
import kotlin.test.BeforeTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * SCR-PA-029, and the Definition-of-Done line *"SOS shows the dispatched state within the SLO and
 * surfaces the share link"*.
 *
 * What is actually assertable about D-33 on a handset is **what the client spends before the
 * request**: the SLO is p99 ≤ 5 s measured over safety-svc's parallel gateways, and the only part of
 * it this app owns is not standing in the way. So the assertions are that a deliberate tap sends
 * *immediately* rather than waiting out the countdown, that the coordinate is the last known fix
 * rather than a fresh lock, and that D-34's link is minted **after** the alarm — never in front of
 * it, where it would spend the budget on a URL.
 */
class SosViewModelTest {

    private val main = MainDispatcher()
    private val rides = FakeRideRepository()
    private val contacts = FakeSosContacts()
    private val locations = FakeLocationSource()

    @BeforeTest
    fun setUp() {
        main.install()
    }

    @AfterTest
    fun tearDown() {
        main.uninstall()
    }

    @Test
    fun a_deliberate_tap_sends_the_last_known_fix_immediately() = runBlocking {
        // The countdown is a cancel window, not a delay: a passenger who taps has confirmed, and
        // every further second is taken off somebody's help.
        val model = viewModel()
        locations.emit(PassengerFix(lat = 6.9344, lng = 79.8428))
        model.state.await { it.position != null }

        model.raise()
        val state = model.state.await { it.stage == SosStage.DISPATCHED }

        assertEquals(
            listOf(Triple(FakeRideRepository.RIDE_ID, 6.9344, 79.8428)),
            rides.soses,
            "one alarm, carrying the fix already in hand",
        )
        assertEquals(SosSmsStatus.DISPATCHED, state.smsStatus)
        assertTrue(state.isRaised)
    }

    @Test
    fun the_share_link_is_minted_after_the_alarm_and_never_before_it() = runBlocking {
        // D-34. `POST /v1/trip-share/{id}` is a second round trip; putting it in front of
        // `POST /v1/sos` would spend the five-second budget on a link.
        val model = viewModel()
        locations.emit(PassengerFix(lat = 6.9, lng = 79.8))
        model.state.await { it.position != null }

        model.raise()
        val state = model.state.await { it.shareLink != null }

        assertEquals(listOf(FakeRideRepository.RIDE_ID), rides.shares)
        assertEquals(FakeRideRepository.SHARE_URL, state.shareLink)
        assertEquals(SosStage.DISPATCHED, state.stage, "the alarm was already out when the link arrived")
    }

    @Test
    fun a_share_link_that_fails_leaves_the_alarm_dispatched() = runBlocking {
        // An alarm that went out with no link to hand on is still an alarm that went out.
        rides.shareFails = MageRideError.Conflict(problem(ErrorCode.RIDE_TERMINAL, HttpStatusCode.Conflict))
        val model = viewModel()
        locations.emit(PassengerFix(lat = 6.9, lng = 79.8))
        model.state.await { it.position != null }

        model.raise()
        val state = model.state.await { it.stage == SosStage.DISPATCHED }

        assertNull(state.shareLink)
        assertNull(state.error, "a missing link is not a failure worth showing over an alarm that went")
    }

    @Test
    fun a_failed_sms_leg_is_not_a_failed_sos() = runBlocking {
        // `SosSmsStatus.FAILED` means the event IS recorded and IS on the admin live feed, and the
        // SMS did not manage it. Telling somebody in trouble that nothing happened would be worse.
        rides.smsStatus = SosSmsStatus.FAILED
        val model = viewModel()
        locations.emit(PassengerFix(lat = 6.9, lng = 79.8))
        model.state.await { it.position != null }

        model.raise()
        val state = model.state.await { it.smsStatus != null }

        assertEquals(SosStage.DISPATCHED, state.stage)
        assertTrue(state.isRaised)
    }

    @Test
    fun an_empty_contact_list_is_warned_about_before_the_alarm_not_after_it() = runBlocking {
        // AL-13. With `Safety:RequireEmergencyContact` at its default the alarm is refused outright,
        // so the fix — SCR-PA-027b — has to be named while there is still time to use it.
        contacts.answer = emptyList()
        val model = viewModel()
        val state = model.state.await { it.contactsLoaded }

        assertTrue(state.warnsNoContact)
        assertNull(state.primaryContact)
    }

    @Test
    fun the_contact_the_sms_reaches_is_the_one_iam_promoted() = runBlocking {
        // D-33's budget is met off a denormalised column, so exactly one contact is texted. The
        // screen puts the `Sent` pill on that one and lists the rest without inventing a fan-out.
        contacts.answer = listOf(
            contact(id = "01JEC00000000000000000002", name = "Nimal", primary = false),
            contact(id = "01JEC00000000000000000001", name = "Amma", primary = true),
        )
        val model = viewModel()
        val state = model.state.await { it.contactsLoaded }

        assertFalse(state.warnsNoContact)
        assertEquals("Amma", state.primaryContact?.name)
    }

    @Test
    fun a_refused_alarm_says_which_contact_to_add() = runBlocking {
        rides.sosFails = MageRideError.BadRequest(problem(ErrorCode.NO_EMERGENCY_CONTACT, HttpStatusCode.BadRequest))
        val model = viewModel()
        locations.emit(PassengerFix(lat = 6.9, lng = 79.8))
        model.state.await { it.position != null }

        model.raise()
        val state = model.state.await { it.error != null }

        assertEquals(SosStage.FAILED, state.stage)
        assertEquals(R.string.error_no_emergency_contact, state.error)
        assertTrue(rides.shares.isEmpty(), "no alarm, no link")
    }

    @Test
    fun with_no_fix_there_is_nothing_to_send_and_the_screen_says_so() = runBlocking {
        // `TriggerSosRequest.lat`/`.lng` are required — `POST /v1/sos` has no positionless form.
        val model = viewModel()

        model.raise()
        val state = model.state.await { it.stage == SosStage.FAILED }

        assertEquals(R.string.sos_no_position, state.error)
        assertTrue(rides.soses.isEmpty(), "a request with no coordinate is not a request")
    }

    @Test
    fun a_second_tap_after_dispatch_raises_nothing_further() = runBlocking {
        // One alarm per trip. A second POST would be a second row on the operator's feed for the
        // same emergency.
        val model = viewModel()
        locations.emit(PassengerFix(lat = 6.9, lng = 79.8))
        model.state.await { it.position != null }

        model.raise()
        model.state.await { it.stage == SosStage.DISPATCHED }
        model.raise()

        assertEquals(1, rides.soses.size)
    }

    @Test
    fun retrying_after_a_transport_failure_arms_the_disc_again() = runBlocking {
        rides.sosFails = MageRideError.Network(IllegalStateException("no route to host"))
        val model = viewModel()
        locations.emit(PassengerFix(lat = 6.9, lng = 79.8))
        model.state.await { it.position != null }

        model.raise()
        model.state.await { it.stage == SosStage.FAILED }

        rides.sosFails = null
        model.retry()
        model.state.await { it.stage == SosStage.ARMED }
        model.raise()

        assertEquals(SosStage.DISPATCHED, model.state.await { it.stage == SosStage.DISPATCHED }.stage)
    }

    // ------------------------------------------------------------------------------------------

    private fun viewModel() = main.own(
        SosViewModel(
            rideId = FakeRideRepository.RIDE_ID,
            rides = rides,
            contacts = contacts,
            locations = locations,
        ),
    )

    private fun problem(code: ErrorCode, status: HttpStatusCode) =
        ProblemDetails(type = code.typeUri, title = code.wire, status = status.value)

    private fun contact(id: Ulid, name: String, primary: Boolean) = EmergencyContact(
        contactId = id,
        name = name,
        phone = "+94770001111",
        isPrimary = primary,
    )

    /** `iam.emergency_contacts` in memory — C083's seam, read here rather than written. */
    private class FakeSosContacts : SosContacts {

        var answer: List<EmergencyContact> = listOf(
            EmergencyContact(
                contactId = "01JEC00000000000000000001",
                name = "Amma",
                phone = "+94770001111",
                isPrimary = true,
            ),
        )

        override suspend fun list(): List<EmergencyContact> = answer

        override suspend fun add(name: String, phone: PhoneE164, idempotencyKey: String?): EmergencyContact =
            error("SCR-PA-029 never writes a contact — SCR-PA-027b does")

        override suspend fun replace(contactId: Ulid, name: String, phone: PhoneE164): EmergencyContact =
            error("SCR-PA-029 never writes a contact — SCR-PA-027b does")

        override suspend fun remove(contactId: Ulid) = error("SCR-PA-029 never writes a contact")
    }

    /**
     * Fixes on demand.
     *
     * A `MutableSharedFlow` with a replay of one, because the production source emits the **last
     * known** fix the moment something collects — which is the behaviour the countdown's start
     * depends on, and a test that emitted only after subscription would be testing a different
     * source.
     */
    private class FakeLocationSource : PassengerLocationSource {

        private val emissions = MutableSharedFlow<PassengerFix>(replay = 1)

        override val fixes: Flow<PassengerFix> = emissions

        suspend fun emit(fix: PassengerFix) {
            emissions.emit(fix)
        }
    }
}
