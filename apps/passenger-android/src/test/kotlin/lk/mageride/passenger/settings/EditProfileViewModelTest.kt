package lk.mageride.passenger.settings

import io.ktor.http.HttpStatusCode
import kotlinx.coroutines.runBlocking
import lk.mageride.passenger.MainDispatcher
import lk.mageride.passenger.R
import lk.mageride.passenger.await
import lk.mageride.passenger.onboarding.PassengerProfileRepository
import lk.mageride.shared.data.models.Role
import lk.mageride.shared.data.models.iam.EmergencyContact
import lk.mageride.shared.data.models.iam.EmergencyContactListResponse
import lk.mageride.shared.data.models.iam.UserProfile
import lk.mageride.shared.testing.fake.FakeApiBackend
import lk.mageride.shared.testing.fake.mageRideApi
import kotlin.test.AfterTest
import kotlin.test.BeforeTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/**
 * SCR-PA-027b — "Edit profile".
 *
 * The assertion this screen exists to make is a **negative** one: AL-26 removed the language
 * selector, and the fence is only real if nothing this screen saves can carry a language. The rest
 * is the SOS list, which is what makes `POST /v1/sos` answer anything but `400 no-emergency-contact`.
 */
class EditProfileViewModelTest {

    private val main = MainDispatcher()
    private val backend = FakeApiBackend()

    @BeforeTest
    fun setUp() {
        main.install()
    }

    @AfterTest
    fun tearDown() {
        main.uninstall()
    }

    @Test
    fun saving_never_sends_a_language() = runBlocking {
        // AL-26: "language selection removed from Edit-profile", kept on onboarding and Settings.
        // `PassengerProfileRepository.update` has no language parameter at all, so this holds
        // however the screen is later edited — the assertion is the fence, checked from the wire.
        backend.returns("getMyProfile", profile())
        backend.returns("listEmergencyContacts", EmergencyContactListResponse(items = emptyList()))
        backend.returns("updateMyProfile", profile())
        val model = viewModel()
        model.state.await { it.loaded }

        model.onNameChanged("Ramith de Silva")
        model.save()
        model.state.await { it.saved }

        val body = backend.lastCall("updateMyProfile").body
        assertFalse(body.contains("language"), "no language field")
        assertFalse(body.contains("\"si\"") || body.contains("\"ta\"") || body.contains("\"en\""), "and no value")
    }

    @Test
    fun the_name_and_the_switch_go_up_in_one_put() = runBlocking {
        // One `Save`, one call. `UpdateProfileRequest` is all-optional so two partial writes would
        // be expressible, and would leave a passenger who lost signal halfway with half a save.
        backend.returns("getMyProfile", profile(notifPrefs = mapOf("MARKETING" to true, "PROMOTIONS" to true)))
        backend.returns("listEmergencyContacts", EmergencyContactListResponse(items = emptyList()))
        backend.returns("updateMyProfile", profile())
        val model = viewModel()
        model.state.await { it.loaded }

        model.onNameChanged("  Ramith de Silva  ")
        model.onNotificationsChanged(false)
        model.save()
        model.state.await { it.saved }

        assertEquals(1, backend.callsTo("updateMyProfile").size)
        val body = backend.lastCall("updateMyProfile").body
        assertTrue(body.contains("\"Ramith de Silva\""), "trimmed before it goes")
        assertTrue(body.contains("\"MARKETING\":false"))
        assertTrue(body.contains("\"PROMOTIONS\":true"), "a key this build does not draw is left alone")
    }

    @Test
    fun an_absent_notification_preference_reads_as_on() = runBlocking {
        // US-10.7 is opt-OUT: a missing key is "never said otherwise". Reading it as off would
        // silently mute a passenger who has never touched the switch.
        backend.returns("getMyProfile", profile(notifPrefs = null))
        backend.returns("listEmergencyContacts", EmergencyContactListResponse(items = emptyList()))

        assertTrue(viewModel().state.await { it.loaded }.notificationsEnabled)
    }

    @Test
    fun an_sos_contact_is_saved_in_e164_from_the_national_number() = runBlocking {
        // The same field SCR-PA-003 uses: `+94` is a prefix on the field, and both `0771234567`
        // and `771234567` are what a passenger reads off the back of somebody's phone.
        backend.returns("getMyProfile", profile())
        backend.returns("listEmergencyContacts", EmergencyContactListResponse(items = emptyList()))
        backend.returns("createEmergencyContact", AMMA)
        val model = viewModel()
        model.state.await { it.loaded }

        model.addContact()
        model.onContactNameChanged("Amma")
        model.onContactPhoneChanged("0770001111")
        assertTrue(model.state.value.contactDraft!!.canSave)

        model.saveContact()
        model.state.await { it.contactDraft == null && it.contacts.isNotEmpty() }

        assertTrue(backend.lastCall("createEmergencyContact").body.contains("+94770001111"))
        assertEquals(listOf(AMMA.contactId), model.state.value.contacts.map { it.contactId })
    }

    @Test
    fun an_incomplete_number_cannot_be_saved() = runBlocking {
        backend.returns("getMyProfile", profile())
        backend.returns("listEmergencyContacts", EmergencyContactListResponse(items = emptyList()))
        val model = viewModel()
        model.state.await { it.loaded }

        model.addContact()
        model.onContactNameChanged("Amma")
        model.onContactPhoneChanged("77000")

        assertFalse(model.state.value.contactDraft!!.canSave)
    }

    @Test
    fun editing_a_contact_replaces_it_rather_than_adding_a_second() = runBlocking {
        backend.returns("getMyProfile", profile())
        backend.returns("listEmergencyContacts", EmergencyContactListResponse(items = listOf(AMMA)))
        backend.returns("updateEmergencyContact", AMMA.copy(name = "Amma (home)"))
        val model = viewModel()
        model.state.await { it.contacts.isNotEmpty() }

        model.editContact(AMMA)
        // The stored number is E.164 and the field is national — `normalise` drops the `+94` the
        // same way it drops a typed one.
        assertEquals("770001111", model.state.value.contactDraft!!.phone)

        model.onContactNameChanged("Amma (home)")
        model.saveContact()
        model.state.await { it.contactDraft == null }

        assertFalse(backend.called("createEmergencyContact"))
        assertEquals(1, model.state.value.contacts.size)
        assertEquals("Amma (home)", model.state.value.contacts.single().name)
    }

    @Test
    fun removing_a_contact_re_reads_the_list_because_the_server_promotes_the_next_one() = runBlocking {
        // Deleting the primary promotes the next contact into `iam.users.emergency_contact_name`,
        // and which one that is is the server's answer. Dropping the row locally would leave an
        // `isPrimary` on screen that is a lie about where the SOS SMS goes.
        backend.returns("getMyProfile", profile())
        backend.returns("listEmergencyContacts", EmergencyContactListResponse(items = listOf(AMMA, THATHA)))
        val model = viewModel()
        model.state.await { it.contacts.size == 2 }

        backend.returns(
            "listEmergencyContacts",
            EmergencyContactListResponse(items = listOf(THATHA.copy(isPrimary = true))),
        )
        model.removeContact(AMMA)
        // The re-read, not the local filter: the row disappears the moment the delete lands, and
        // the promotion only becomes visible when the list comes back.
        val state = model.state.await { it.contacts.singleOrNull()?.isPrimary == true }

        assertTrue(backend.called("deleteEmergencyContact"))
        assertEquals(THATHA.contactId, state.contacts.single().contactId)
        assertEquals(2, backend.callsTo("listEmergencyContacts").size, "read again after the delete")
    }

    @Test
    fun a_bad_number_the_server_refuses_is_reported_on_the_dialog() = runBlocking {
        backend.returns("getMyProfile", profile())
        backend.returns("listEmergencyContacts", EmergencyContactListResponse(items = emptyList()))
        backend.fails("createEmergencyContact", HttpStatusCode.BadRequest, "invalid-phone")
        val model = viewModel()
        model.state.await { it.loaded }

        model.addContact()
        model.onContactNameChanged("Amma")
        model.onContactPhoneChanged("770001111")
        model.saveContact()
        val state = model.state.await { it.error != null }

        assertEquals(R.string.error_phone_invalid, state.error)
        assertFalse(state.contactDraft!!.saving, "and the dialog stays open with what was typed")
    }

    @Test
    fun a_contact_list_that_cannot_be_read_still_leaves_a_profile_that_can_be_edited() = runBlocking {
        // An iam-svc hiccup on the contacts should cost the passenger the SOS section, not the
        // ability to fix their name.
        backend.returns("getMyProfile", profile())
        backend.fails("listEmergencyContacts", HttpStatusCode.ServiceUnavailable, "service-unavailable")

        val state = viewModel().state.await { it.loaded }

        assertEquals("Ramith de Silva", state.name)
        assertTrue(state.contacts.isEmpty())
        assertTrue(state.canSave)
    }

    // ------------------------------------------------------------------------------------------

    private fun viewModel(): EditProfileViewModel {
        val profiles = PassengerProfileRepository(iam = backend.mageRideApi().iam)
        return main.own(
            EditProfileViewModel(
                profiles = profiles,
                contacts = ApiSosContacts(iam = backend.mageRideApi().iam),
                identity = PassengerIdentity(profiles),
                keys = { KEY },
            ),
        )
    }

    private fun profile(notifPrefs: Map<String, Boolean>? = null) = UserProfile(
        userId = "01JPAX00000000000000000000",
        phone = "+94771234567",
        firstName = "Ramith de Silva",
        role = Role.PASSENGER,
        notifPrefs = notifPrefs,
    )

    private companion object {
        const val KEY = "01JKEY00000000000000000002"

        val AMMA = EmergencyContact(
            contactId = "01JSOS0000000000000000001",
            isPrimary = true,
            name = "Amma",
            phone = "+94770001111",
        )
        val THATHA = EmergencyContact(
            contactId = "01JSOS0000000000000000002",
            isPrimary = false,
            name = "Thatha",
            phone = "+94770002222",
        )
    }
}
