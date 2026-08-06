package lk.mageride.passenger.settings

import io.ktor.http.HttpStatusCode
import kotlinx.coroutines.runBlocking
import lk.mageride.passenger.MainDispatcher
import lk.mageride.passenger.R
import lk.mageride.passenger.await
import lk.mageride.passenger.awaitCall
import lk.mageride.passenger.booking.BookingDraft
import lk.mageride.passenger.onboarding.FakeAppPreferences
import lk.mageride.passenger.onboarding.PassengerProfileRepository
import lk.mageride.passenger.subscription.signedInSession
import lk.mageride.shared.data.models.Language
import lk.mageride.shared.data.models.Place
import lk.mageride.shared.data.models.Role
import lk.mageride.shared.data.models.fare.PaymentMethod
import lk.mageride.shared.data.models.iam.DefaultPaymentMethod
import lk.mageride.shared.data.models.iam.LanguagePreference
import lk.mageride.shared.data.models.iam.UserProfile
import lk.mageride.shared.testing.fake.FakeApiBackend
import lk.mageride.shared.testing.fake.mageRideApi
import kotlin.test.AfterTest
import kotlin.test.BeforeTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * SCR-PA-027 — "Profile & settings".
 *
 * The assertions that carry consequences: **the default payment method reaches the next booking**
 * (the component's Definition of Done), a language change is written to the device before it is
 * written to the account, the notification switch sends back the whole `notif_prefs` map, and
 * *Delete account* reports a **request** rather than a deletion.
 */
class SettingsViewModelTest {

    private val main = MainDispatcher()
    private val backend = FakeApiBackend()
    private val preferences = FakeAppPreferences(language = Language.SI)

    @BeforeTest
    fun setUp() {
        main.install()
    }

    @AfterTest
    fun tearDown() {
        main.uninstall()
    }

    @Test
    fun the_chosen_default_payment_method_is_what_the_next_booking_starts_with() = runBlocking {
        // US-22.4 — "pre-selected at booking/checkout". `BookingDraft` re-reads the preference on
        // every fresh draft, which is what makes "the NEXT booking" true rather than "every booking
        // after a restart".
        backend.returns("getMyProfile", profile())
        val payments = PaymentPreference(preferences)
        val model = viewModel(payments)
        model.state.await { !it.loading }

        model.chooseDefaultPayment(PaymentMethod.WALLET)

        val draft = BookingDraft(payments)
        draft.begin(Place(lat = 6.9271, lng = 79.8612))
        assertEquals(PaymentMethod.WALLET, draft.current.paymentMethod)
    }

    @Test
    fun a_booking_already_on_screen_keeps_the_rail_it_was_given() = runBlocking {
        // A preference is not a command. The draft is seeded when it begins; changing Settings
        // afterwards must not reach into a booking the passenger is already filling in.
        backend.returns("getMyProfile", profile())
        val payments = PaymentPreference(preferences)
        val draft = BookingDraft(payments)
        draft.begin(Place(lat = 6.9271, lng = 79.8612))

        val model = viewModel(payments)
        model.state.await { !it.loading }
        model.chooseDefaultPayment(PaymentMethod.WALLET)

        assertEquals(PaymentMethod.CASH, draft.current.paymentMethod)
    }

    @Test
    fun cash_is_written_to_the_account_and_wallet_cannot_be() = runBlocking {
        // The AL-57 gap, asserted so it cannot be forgotten: `DefaultPaymentMethod` is still
        // `[cash, lankaqr, onepay]`, so the rail that REPLACED onepay has no value to be stored
        // as. The device honours it; the account is not told a value it would reject.
        backend.returns("getMyProfile", profile())
        val model = viewModel()
        model.state.await { !it.loading }

        model.chooseDefaultPayment(PaymentMethod.WALLET)
        assertFalse(backend.called("setDefaultPaymentMethod"), "there is no wire value for the wallet")
        assertEquals(PaymentMethod.WALLET.wire, preferences.defaultPaymentMethod, "but the device remembers")

        model.chooseDefaultPayment(PaymentMethod.CASH)
        // Awaited on the CALL, not on the state (Δ C084). `pushDefaultPayment` publishes nothing on
        // success, and `defaultPayment` is set synchronously *before* the request is launched — so
        // a state predicate here passes while the write is still in flight, which is what made this
        // assertion flaky on a loaded build host.
        backend.awaitCall("setDefaultPaymentMethod")
        assertTrue(backend.lastCall("setDefaultPaymentMethod").body.contains("cash"))
    }

    @Test
    fun a_retired_rail_on_the_profile_reads_as_cash() = runBlocking {
        // A row written before AL-57/AL-59 names a rail this app can no longer offer. Pre-selecting
        // it would put a booking on a method SCR-PA-016 does not even draw.
        backend.returns("getMyProfile", profile(payment = DefaultPaymentMethod.ONEPAY))

        val state = viewModel().state.await { !it.loading }

        assertEquals(PaymentMethod.CASH, state.defaultPayment)
    }

    @Test
    fun a_rail_chosen_on_this_device_survives_a_profile_that_says_cash() = runBlocking {
        // US-22.6 — the account seeds a handset with no answer of its own, and loses to one that
        // has. A profile read that reverted last week's choice would be the account overwriting
        // the passenger.
        preferences.defaultPaymentMethod = PaymentMethod.WALLET.wire
        backend.returns("getMyProfile", profile(payment = DefaultPaymentMethod.CASH))

        val state = viewModel().state.await { !it.loading }

        assertEquals(PaymentMethod.WALLET, state.defaultPayment)
    }

    @Test
    fun a_language_change_is_stored_on_the_device_and_asks_for_a_relaunch() = runBlocking {
        // `attachBaseContext` reads the preference, and it has already run by the time a composable
        // exists — so the write and the `recreate()` are what actually change the language. D-26's
        // server copy is what SMS and server-rendered strings are written in.
        backend.returns("getMyProfile", profile())
        backend.returns("setLanguagePreference", LanguagePreference(Language.TA))
        val model = viewModel()
        model.state.await { !it.loading }

        model.chooseLanguage(Language.TA)
        val state = model.state.await { it.relaunch }

        assertEquals(Language.TA, preferences.language)
        assertEquals(Language.TA, state.language)
        assertFalse(preferences.languagePendingSync, "the server has it, so nothing is owed")
        assertTrue(backend.lastCall("setLanguagePreference").body.contains("\"ta\""))
    }

    @Test
    fun a_language_change_that_could_not_be_sent_still_changes_the_app() = runBlocking {
        // A passenger on a train with no signal asked for a Tamil app. The local write is not
        // conditional on the call; `languagePendingSync` is what C077 pushes on the next pass.
        backend.returns("getMyProfile", profile())
        backend.fails("setLanguagePreference", HttpStatusCode.ServiceUnavailable, "service-unavailable")
        val model = viewModel()
        model.state.await { !it.loading }

        model.chooseLanguage(Language.TA)
        model.state.await { it.error != null }

        assertEquals(Language.TA, preferences.language)
        assertTrue(preferences.languagePendingSync, "still owed to the server")
        assertTrue(model.state.value.relaunch, "and the app still re-inflates in Tamil")
    }

    @Test
    fun the_notification_switch_sends_back_every_key_it_was_given() = runBlocking {
        // The set of notification types grows without a contract change, so sending only the key
        // this screen draws would silently re-enable a type a newer build had muted.
        backend.returns("getMyProfile", profile(notifPrefs = mapOf("MARKETING" to true, "PROMOTIONS" to false)))
        backend.returns("updateMyProfile", profile())
        val model = viewModel()
        model.state.await { !it.loading }

        model.setNotifications(false)
        model.state.await { !it.busy }

        val body = backend.lastCall("updateMyProfile").body
        assertTrue(body.contains("\"MARKETING\":false"), "the switch this screen draws")
        assertTrue(body.contains("\"PROMOTIONS\":false"), "and the key it has never heard of")
    }

    @Test
    fun a_failed_notification_write_puts_the_switch_back() = runBlocking {
        backend.returns("getMyProfile", profile())
        backend.fails("updateMyProfile", HttpStatusCode.ServiceUnavailable, "service-unavailable")
        val model = viewModel()
        model.state.await { !it.loading }

        model.setNotifications(false)
        val state = model.state.await { it.error != null }

        assertTrue(state.notificationsEnabled, "a switch that stayed flipped would be lying")
    }

    @Test
    fun deleting_the_account_reports_a_request_and_keeps_the_session() = runBlocking {
        // `202`, not `204` (E-06). A statutory hold can delay the erasure, so signing the passenger
        // out here would claim something that has not happened and take away the surface that can
        // tell them when it has.
        backend.returns("getMyProfile", profile())
        val model = viewModel()
        model.state.await { !it.loading }

        model.confirmDelete()
        assertTrue(model.state.value.confirmingDelete)

        model.deleteAccount()
        val state = model.state.await { it.deletionRequestId != null }

        assertNotNull(state.deletionRequestId)
        assertNull(state.error)
        assertFalse(backend.called("logout"), "the account is queued for erasure, not gone")
    }

    @Test
    fun a_second_deletion_request_says_one_is_already_being_handled() = runBlocking {
        // The one code with two meanings in this cluster: a `409` here is "already open", and on a
        // saved address it is "you already have a Home". See `SettingsErrors.deletionMessageFor`.
        backend.returns("getMyProfile", profile())
        backend.fails("deleteMyAccount", HttpStatusCode.Conflict, "conflict")
        val model = viewModel()
        model.state.await { !it.loading }

        model.deleteAccount()
        val state = model.state.await { it.error != null }

        assertEquals(R.string.settings_delete_already_requested, state.error)
    }

    @Test
    fun a_profile_that_cannot_be_read_still_renders_what_the_device_knows() = runBlocking {
        // The language the app is drawing in and the rail it will book with are both local facts.
        preferences.defaultPaymentMethod = PaymentMethod.WALLET.wire
        backend.fails("getMyProfile", HttpStatusCode.ServiceUnavailable, "service-unavailable")

        val state = viewModel().state.await { !it.loading }

        assertNotNull(state.error)
        assertEquals(Language.SI, state.language)
        assertEquals(PaymentMethod.WALLET, state.defaultPayment)
    }

    // ------------------------------------------------------------------------------------------

    private fun viewModel(payments: PaymentPreference = PaymentPreference(preferences)): SettingsViewModel {
        val profiles = PassengerProfileRepository(iam = backend.mageRideApi().iam)
        return main.own(
            SettingsViewModel(
                profiles = profiles,
                identity = PassengerIdentity(profiles),
                payments = payments,
                preferences = preferences,
                sessions = signedInSession(),
            ),
        )
    }

    private fun profile(payment: DefaultPaymentMethod? = null, notifPrefs: Map<String, Boolean>? = null) = UserProfile(
        userId = "01JPAX00000000000000000000",
        phone = "+94771234567",
        firstName = "Ramith de Silva",
        role = Role.PASSENGER,
        language = null,
        defaultPaymentMethod = payment,
        notifPrefs = notifPrefs,
    )
}
