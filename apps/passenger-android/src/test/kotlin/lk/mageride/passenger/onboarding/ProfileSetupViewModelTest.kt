package lk.mageride.passenger.onboarding

import io.ktor.http.HttpStatusCode
import kotlinx.coroutines.runBlocking
import lk.mageride.passenger.MainDispatcher
import lk.mageride.passenger.R
import lk.mageride.passenger.await
import lk.mageride.shared.data.models.Language
import lk.mageride.shared.data.models.Role
import lk.mageride.shared.data.models.iam.UserProfile
import lk.mageride.shared.serialization.MageRideJson
import lk.mageride.shared.testing.fake.FakeApiBackend
import lk.mageride.shared.testing.fake.mageRideApi
import kotlin.test.AfterTest
import kotlin.test.BeforeTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/**
 * SCR-PA-004 — the first profile.
 *
 * Three things carry consequences and are asserted here: the save is **one** `PUT` and not three,
 * the notification switch reaches `iam.users.notif_prefs` under a key that survives a round trip,
 * and the chosen language is written to the local preference as well as sent — because
 * `attachBaseContext` reads the preference, and a passenger who changed it here would otherwise
 * keep the old locale.
 */
class ProfileSetupViewModelTest {

    private val main = MainDispatcher()
    private val backend = FakeApiBackend()
    private val preferences = FakeAppPreferences(language = Language.SI, languagePendingSync = true)

    @BeforeTest
    fun setUp() {
        main.install()
    }

    @AfterTest
    fun tearDown() {
        main.uninstall()
    }

    @Test
    fun the_form_pre_fills_from_the_profile_the_server_holds() = runBlocking {
        backend.returns("getMyProfile", profile(firstName = "Ramith de Silva", language = Language.TA))

        val state = viewModel().state.await { it.loaded }

        assertEquals("Ramith de Silva", state.name)
        // The account wins over the local preference: a passenger signing in on a second handset
        // should see what they chose on the first.
        assertEquals(Language.TA, state.language)
    }

    @Test
    fun an_absent_notification_preference_reads_as_on() = runBlocking {
        // US-10.7 is opt-OUT. A missing key is "never said otherwise", which is on — reading it as
        // off would silently mute a passenger who had never touched the switch.
        backend.returns("getMyProfile", profile(firstName = "Ramith", notifPrefs = null))

        assertTrue(viewModel().state.await { it.loaded }.notificationsEnabled)
    }

    @Test
    fun the_cta_waits_for_a_name_that_is_not_just_spaces() = runBlocking {
        backend.returns("getMyProfile", profile(firstName = null))
        val model = viewModel()
        model.state.await { it.loaded }

        model.onNameChanged("   ")
        assertFalse(model.state.value.canSubmit)

        model.onNameChanged("Ramith")
        assertTrue(model.state.value.canSubmit)
    }

    @Test
    fun saving_sends_one_put_with_everything_the_screen_owns() = runBlocking {
        // The contract's `UpdateProfileRequest` is all-optional, so three partial calls would be
        // expressible — and would leave a passenger who lost signal halfway with a name and no
        // language. "Save & continue" is one action.
        backend.returns("getMyProfile", profile(firstName = null))
        val model = viewModel()
        model.state.await { it.loaded }
        model.onNameChanged("  Ramith de Silva  ")
        model.onLanguageChanged(Language.EN)
        model.onNotificationsChanged(false)

        model.submit()
        model.saved.await { it }

        assertEquals(1, backend.callsTo("updateMyProfile").size)
        val sent = MageRideJson.parseToJsonElement(backend.lastCall("updateMyProfile").body)
        val body = sent.toString()
        assertTrue(body.contains("Ramith de Silva"), "the name, trimmed")
        assertFalse(body.contains("  Ramith"), "and trimmed before it goes")
        assertTrue(body.contains("\"en\""), "the language, in its wire spelling")
        assertTrue(
            body.contains(PassengerProfileRepository.NOTIFICATIONS_AND_OFFERS),
            "the switch, under the notif_prefs key it round-trips on",
        )
    }

    @Test
    fun the_saved_language_is_written_locally_so_the_next_screen_is_in_it() = runBlocking {
        backend.returns("getMyProfile", profile(firstName = null))
        val model = viewModel()
        model.state.await { it.loaded }
        model.onNameChanged("Ramith")
        model.onLanguageChanged(Language.EN)

        model.submit()
        model.saved.await { it }

        // `MainActivity.attachBaseContext` reads this, and nothing else would have written it.
        assertEquals(Language.EN, preferences.language)
        assertFalse(preferences.languagePendingSync, "already on the server — nothing is owed")
    }

    @Test
    fun a_failed_save_says_why_and_keeps_the_passenger_on_the_form() = runBlocking {
        backend.returns("getMyProfile", profile(firstName = null))
        backend.fails("updateMyProfile", HttpStatusCode.BadRequest, "validation-failed")
        val model = viewModel()
        model.state.await { it.loaded }
        model.onNameChanged("Ramith")

        model.submit()
        model.state.await { it.error != null }

        assertEquals(R.string.error_validation_failed, model.state.value.error)
        assertFalse(model.saved.value, "and does not navigate")
    }

    @Test
    fun a_profile_that_cannot_be_read_still_leaves_a_form_that_can_be_filled_in() = runBlocking {
        // An empty form is better than a spinner nobody can leave, and the save is a `PUT` that
        // overwrites whatever is there regardless.
        backend.fails("getMyProfile", HttpStatusCode.ServiceUnavailable, "service-unavailable")

        val state = viewModel().state.await { it.loaded }

        assertEquals("", state.name)
        assertEquals(Language.SI, state.language, "falls back to the local preference")
    }

    private fun viewModel(): ProfileSetupViewModel = main.own(
        ProfileSetupViewModel(
            profiles = PassengerProfileRepository(iam = backend.mageRideApi().iam),
            preferences = preferences,
        ),
    )

    private fun profile(firstName: String?, language: Language? = null, notifPrefs: Map<String, Boolean>? = null) =
        UserProfile(
            userId = "01JPAX00000000000000000000",
            phone = "+94771234567",
            firstName = firstName,
            role = Role.PASSENGER,
            language = language,
            notifPrefs = notifPrefs,
        )
}
