package lk.mageride.driver.profile

import kotlinx.coroutines.runBlocking
import lk.mageride.driver.home.signedInSessions
import lk.mageride.driver.jobs.JobsRepository
import lk.mageride.driver.jobs.identity
import lk.mageride.driver.onboarding.FakeOnboardingPreferences
import lk.mageride.driver.onboarding.MainDispatcher
import lk.mageride.driver.onboarding.await
import lk.mageride.shared.data.models.Language
import lk.mageride.shared.data.models.Role
import lk.mageride.shared.data.models.iam.EmergencyContact
import lk.mageride.shared.data.models.iam.EmergencyContactListResponse
import lk.mageride.shared.data.models.iam.UserProfile
import lk.mageride.shared.serialization.MageRideJson
import lk.mageride.shared.testing.fake.FakeApiBackend
import lk.mageride.shared.testing.fake.mageRideApi
import lk.mageride.shared.testing.fixture.Fixtures
import kotlin.test.AfterTest
import kotlin.test.BeforeTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * SCR-DA-029 — the profile, the AL-13 emergency contact and US-10.7's switches.
 *
 * The definition-of-done case is here: *"the emergency contact is saved and used by driver SOS"*.
 * The second half of that sentence is safety-svc's — `POST /v1/sos` answers
 * `400 no-emergency-contact` to an account with none — so what this asserts is the half this screen
 * owns: exactly one contact is stored, it is **replaced** rather than added to, and the number goes
 * up in the E.164 form the SMS gateway takes.
 */
class DriverProfileViewModelTest {

    private val main = MainDispatcher()
    private val backend = FakeApiBackend()
    private val preferences = FakeOnboardingPreferences()

    private val contactId = "01JCONTACT0000000000000001"

    @BeforeTest
    fun setUp() {
        main.install()
        backend.returns("getMyProfile", profile())
        backend.returns("listEmergencyContacts", EmergencyContactListResponse(items = emptyList()))
    }

    @AfterTest
    fun tearDown() {
        main.uninstall()
    }

    @Test
    fun the_screen_opens_on_the_profile_and_the_stored_contact() = runBlocking {
        backend.returns("listEmergencyContacts", EmergencyContactListResponse(items = listOf(contact())))

        val model = viewModel()
        val state = model.state.await { !it.loading }

        assertEquals("K. Fernando", state.profile?.firstName)
        assertEquals("Amma", state.contact?.name)
    }

    @Test
    fun a_driver_with_no_contact_creates_one_and_a_driver_with_one_replaces_it() = runBlocking {
        // `EmergencyContact.isPrimary` is "exactly one per account that has any" — the SOS budget is
        // p99 <= 5 s and the primary is denormalised onto `iam.users`. Adding a second would leave
        // the fast path pointing at whichever the server had already denormalised.
        backend.returns("createEmergencyContact", contact())

        val model = viewModel()
        model.state.await { !it.loading }

        model.openSheet(ProfileSheet.Emergency)
        model.onContactNameChange("Amma")
        model.onContactPhoneChange("0770001111")
        assertTrue(model.state.value.canSaveContact)
        model.saveEmergencyContact()

        model.state.await { it.contact != null }
        assertTrue(backend.called("createEmergencyContact"))
        assertFalse(backend.called("updateEmergencyContact"))

        val body = MageRideJson.parseToJsonElement(backend.lastCall("createEmergencyContact").body).toString()
        assertTrue(body.contains("\"phone\":\"+94770001111\""), "the trunk zero is dropped and +94 added: $body")

        // Second save, now that a contact exists.
        backend.returns("updateEmergencyContact", contact(name = "Thaththa"))
        model.openSheet(ProfileSheet.Emergency)
        model.onContactNameChange("Thaththa")
        model.saveEmergencyContact()

        val state = model.state.await { it.contact?.name == "Thaththa" }
        assertTrue(backend.lastCall("updateEmergencyContact").path.endsWith(contactId))
        assertNull(state.sheet, "the sheet closes on a successful save")
    }

    @Test
    fun an_incomplete_number_never_reaches_the_gateway() = runBlocking {
        val model = viewModel()
        model.state.await { !it.loading }

        model.openSheet(ProfileSheet.Emergency)
        model.onContactNameChange("Amma")
        model.onContactPhoneChange("07700")

        assertTrue(model.state.value.contactPhoneRejected)
        assertFalse(model.state.value.canSaveContact)

        model.saveEmergencyContact()
        assertFalse(backend.called("createEmergencyContact"))
    }

    @Test
    fun a_contact_picked_from_the_address_book_fills_both_fields() = runBlocking {
        val model = viewModel()
        model.state.await { !it.loading }
        model.openSheet(ProfileSheet.Emergency)

        // What `ACTION_PICK` hands back is whatever the address book stored, which is very often
        // the international form with spaces in it.
        model.onContactPicked(name = "Amma", phone = "+94 77 000 1111")

        val state = model.state.value
        assertEquals("Amma", state.contactNameDraft)
        assertEquals("770001111", state.contactPhoneDraft)
        assertTrue(state.canSaveContact)
    }

    @Test
    fun a_notification_group_writes_every_key_in_it_and_keeps_the_ones_it_does_not_know() = runBlocking {
        // The event list "grows without a contract change", so a build that sent only the keys it
        // knew would silently re-enable a type a newer build had muted.
        backend.returns("getMyProfile", profile(notifPrefs = mapOf("SOME_FUTURE_TYPE" to false)))
        backend.returns("updateMyProfile", profile())

        val model = viewModel()
        model.state.await { !it.loading }

        model.setNotificationGroup(DriverNotificationGroup.Money, enabled = false)
        model.state.await { !it.saving && backend.called("updateMyProfile") }

        val body = MageRideJson.parseToJsonElement(backend.lastCall("updateMyProfile").body).toString()
        DriverNotificationGroup.Money.types.forEach { type ->
            assertTrue(body.contains("\"$type\":false"), "$type was not switched off: $body")
        }
        assertTrue(body.contains("\"SOME_FUTURE_TYPE\":false"), "an unknown key was dropped: $body")
    }

    @Test
    fun an_unstored_preference_reads_as_on_because_muting_is_opt_out() = runBlocking {
        // `iam.users.notif_prefs` starts empty. Treating "not stored" as off would show a driver a
        // screen of switches claiming they had muted everything the first time they opened it.
        val model = viewModel()
        model.state.await { !it.loading }

        DriverNotificationGroup.entries.forEach { group ->
            assertTrue(group.isEnabled(model.state.value.notificationPreferences), "$group read as muted")
        }
    }

    @Test
    fun no_switch_is_offered_for_a_type_the_platform_refuses_to_mute() = runBlocking {
        // `NotificationCatalogue.SafetyCritical` — iam-svc drops a mute for one on the way in and
        // notification-svc ignores it on the way out. A switch that appears to work and does not is
        // worse than no switch.
        val offered = DriverNotificationGroup.entries.flatMap { it.types }
        listOf("SOS_TRIGGERED", "SOS_RESOLVED", "RIDE_CANCELLED", "SCHEDULE_NOT_STARTED").forEach { type ->
            assertFalse(type in offered, "$type cannot be muted and must not be offered")
        }
    }

    @Test
    fun choosing_a_language_stores_it_on_the_server_and_on_the_device() = runBlocking {
        // Both, because they answer different questions: the server's copy is what every rendered
        // template and SMS is written in, and the device's is what `DriverLocale.wrap` reads before
        // a single composable runs.
        val model = viewModel()
        model.state.await { !it.loading }

        model.chooseLanguage(Language.TA)
        val state = model.state.await { it.languageChanged }

        assertTrue(backend.called("setLanguagePreference"))
        assertEquals(Language.TA, preferences.language)
        assertNull(state.sheet)
    }

    @Test
    fun logging_out_ends_the_session_even_when_the_gateway_cannot_be_reached() = runBlocking {
        // A driver who has asked to be signed out on a handset with no signal must still end up
        // signed out on THIS device; `AuthSessionManager.logout()` clears the local session either
        // way, and leaving them on the profile screen would be the worst reading of the word.
        backend.fails("logout", io.ktor.http.HttpStatusCode.ServiceUnavailable, "unavailable")

        val model = viewModel()
        model.state.await { !it.loading }
        model.logOut()

        val state = model.state.await { it.signedOut }
        assertTrue(state.signedOut)
    }

    private fun profile(notifPrefs: Map<String, Boolean>? = null): UserProfile = UserProfile(
        userId = Fixtures.DRIVER_ID,
        phone = Fixtures.DRIVER_PHONE,
        firstName = "K. Fernando",
        role = Role.DRIVER,
        language = Language.SI,
        notifPrefs = notifPrefs,
    )

    private fun contact(name: String = "Amma"): EmergencyContact = EmergencyContact(
        contactId = contactId,
        isPrimary = true,
        name = name,
        phone = "+94770001111",
    )

    private suspend fun viewModel(): DriverProfileViewModel {
        val api = backend.mageRideApi()
        val sessions = signedInSessions(backend)
        return main.own(
            DriverProfileViewModel(
                identity = identity(backend, sessions),
                profiles = ProfileRepository(
                    iam = api.iam,
                    jobs = JobsRepository(dispatch = api.dispatch),
                    sessions = sessions,
                    preferences = preferences,
                    registry = api.registry,
                    gatewayOrigin = "https://api.test",
                ),
            ),
        )
    }
}
