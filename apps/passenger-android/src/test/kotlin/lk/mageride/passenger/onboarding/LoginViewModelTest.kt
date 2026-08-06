package lk.mageride.passenger.onboarding

import io.ktor.http.HttpStatusCode
import kotlinx.coroutines.runBlocking
import lk.mageride.passenger.MainDispatcher
import lk.mageride.passenger.R
import lk.mageride.passenger.await
import lk.mageride.passenger.push.PushTokenProvider
import lk.mageride.shared.data.models.AppSurface
import lk.mageride.shared.data.models.Language
import lk.mageride.shared.data.models.Role
import lk.mageride.shared.data.models.iam.RequestOtpResponse
import lk.mageride.shared.data.models.iam.UserProfile
import lk.mageride.shared.data.models.iam.VerifyOtpResponse
import lk.mageride.shared.domain.auth.AuthConfig
import lk.mageride.shared.domain.auth.AuthSessionManager
import lk.mageride.shared.domain.auth.AuthSessionStore
import lk.mageride.shared.testing.fake.FakeApiBackend
import lk.mageride.shared.testing.fake.InMemorySecureStore
import lk.mageride.shared.testing.fake.mageRideApi
import kotlin.test.AfterTest
import kotlin.test.BeforeTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/**
 * SCR-PA-003 — the phone half, the code half, and what the screen says when the server says no.
 *
 * Everything about tokens is C014's and is tested there. What is C077's, and what these assert, is
 * the state machine on top of it: which half the CTA submits, when Resend is refused **locally**
 * (US-1.10's cooldown, so a tap does not spend one of D-32's five), how a dead attempt gets the
 * passenger back to the number field, and — the one with real consequences — where they land the
 * moment they are signed in.
 */
class LoginViewModelTest {

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
    fun the_cta_submits_the_phone_half_only_once_the_number_is_complete() = runBlocking {
        val model = viewModel()

        model.onPhoneChanged("077123")
        assertFalse(model.state.value.canSubmit)

        // Typed with the trunk zero, which is how a passenger reads it off their own handset.
        model.onPhoneChanged("0771234567")
        assertTrue(model.state.value.canSubmit)
        assertEquals("771234567", model.state.value.phone, "normalised on every keystroke")
    }

    @Test
    fun requesting_a_code_moves_to_the_otp_half_and_starts_the_cooldown() = runBlocking {
        val model = signedOutWithNumber()

        model.submit()
        val state = model.state.await { it.phase == LoginPhase.OTP && !it.busy }

        assertTrue(backend.called("requestOtp"))
        assertEquals("", state.otp, "the boxes start empty")
    }

    @Test
    fun resend_is_refused_locally_inside_the_cooldown() = runBlocking {
        // US-1.10 is a 60-second wait and D-32 caps requests at five an hour, so a tap inside the
        // cooldown does not merely fail — it SPENDS one of the five. Gating the button is what
        // keeps a passenger who taps four times from locking themselves out for an hour.
        //
        // The cooldown is pinned rather than synthesised (Δ C079). `AuthSessionManager` computes
        // `resendAllowedAt = Clock.System.now() + cooldownSeconds` off the REAL clock, and the
        // fixture generator answers 15 for any field whose name contains "second" — so on a loaded
        // build host a fifteen-second stall between the request and this assertion made the
        // countdown legitimately expire and failed a test that was asserting the right thing.
        backend.returns(
            "requestOtp",
            RequestOtpResponse(
                authId = AUTH_ID,
                attemptsRemaining = 3,
                cooldownSeconds = LONG_COOLDOWN_SECONDS,
                isBlocked = false,
            ),
        )
        val model = signedOutWithNumber()
        model.submit()
        model.state.await { it.phase == LoginPhase.OTP && !it.busy }

        model.resend()

        assertFalse(model.state.value.canResend, "the countdown is still running")
        assertFalse(backend.called("resendOtp"), "nothing reached the server")
    }

    @Test
    fun the_cta_waits_for_all_six_digits() = runBlocking {
        val model = signedOutWithNumber()
        model.submit()
        model.state.await { it.phase == LoginPhase.OTP && !it.busy }

        model.onOtpChanged("4829")
        assertFalse(model.state.value.canSubmit)

        model.onOtpChanged("482913")
        assertTrue(model.state.value.canSubmit)
    }

    @Test
    fun a_new_passenger_lands_on_profile_setup_and_an_existing_one_on_the_map() = runBlocking {
        // The destination is computed from the profile the SERVER holds, not from `isNewUser`: a
        // passenger who installed, signed in and killed the app before Profile Setup is not a new
        // user and still has no name.
        backend.returns("verifyOtp", verified(isNewUser = true))
        backend.returns("getMyProfile", profile(firstName = null))
        preferences.locationRationaleAcknowledged = true

        val model = signedOutWithNumber()
        model.submit()
        model.state.await { it.phase == LoginPhase.OTP && !it.busy }
        model.onOtpChanged("482913")
        model.submit()

        assertEquals(PassengerDestination.PROFILE_SETUP, model.destination.await { it != null })
    }

    @Test
    fun a_returning_passenger_with_a_name_goes_straight_to_the_map() = runBlocking {
        backend.returns("verifyOtp", verified(isNewUser = false))
        backend.returns("getMyProfile", profile(firstName = "Ramith de Silva"))
        preferences.locationRationaleAcknowledged = true

        val model = signedOutWithNumber()
        model.submit()
        model.state.await { it.phase == LoginPhase.OTP && !it.busy }
        model.onOtpChanged("482913")
        model.submit()

        assertEquals(PassengerDestination.LIVE_MAP, model.destination.await { it != null })
    }

    @Test
    fun the_language_chosen_before_sign_in_is_pushed_on_the_first_authenticated_pass() = runBlocking {
        // SCR-PA-002 runs signed out, so `PUT /v1/me/prefs/language` cannot be called there. This
        // is the first moment it can be, and the flag is what remembers that it is owed.
        backend.returns("verifyOtp", verified(isNewUser = false))
        backend.returns("getMyProfile", profile(firstName = "Ramith de Silva"))

        val model = signedOutWithNumber()
        model.submit()
        model.state.await { it.phase == LoginPhase.OTP && !it.busy }
        model.onOtpChanged("482913")
        model.submit()
        model.destination.await { it != null }

        assertTrue(backend.called("setLanguagePreference"))
        assertFalse(preferences.languagePendingSync, "and it is no longer owed")
    }

    @Test
    fun a_dead_attempt_returns_to_the_number_rather_than_offering_a_seventh_box() = runBlocking {
        // `423 otp-locked` takes C014 back to `SignedOut` — that `authId` can never succeed again.
        backend.fails("verifyOtp", HttpStatusCode.Locked, "otp-locked")
        val model = signedOutWithNumber()
        model.submit()
        model.state.await { it.phase == LoginPhase.OTP && !it.busy }
        model.onOtpChanged("123456")

        model.submit()
        model.state.await { it.error != null }

        assertEquals(LoginPhase.PHONE, model.state.value.phase)
        assertEquals(R.string.error_otp_locked, model.state.value.error)
    }

    @Test
    fun a_rate_limited_request_says_so_rather_than_something_went_wrong() = runBlocking {
        // D-32's 5/h cap, surfaced. "Something went wrong" would send a passenger straight back to
        // the button that is refusing them.
        backend.fails("requestOtp", HttpStatusCode.TooManyRequests, "otp-rate-limited")
        val model = signedOutWithNumber()

        model.submit()
        model.state.await { it.error != null }

        assertEquals(LoginPhase.PHONE, model.state.value.phase, "no code went out")
        assertEquals(R.string.error_otp_rate_limited, model.state.value.error)
    }

    @Test
    fun back_from_the_code_half_returns_to_the_number_without_leaving_the_screen() = runBlocking {
        val model = signedOutWithNumber()
        model.submit()
        model.state.await { it.phase == LoginPhase.OTP && !it.busy }

        model.editPhoneNumber()

        assertEquals(LoginPhase.PHONE, model.state.value.phase)
        assertEquals("771234567", model.state.value.phone, "the number is kept — it is usually one digit wrong")
        assertEquals("", model.state.value.otp)
    }

    private fun signedOutWithNumber(): LoginViewModel = viewModel().apply { onPhoneChanged("0771234567") }

    private fun viewModel(): LoginViewModel {
        val api = backend.mageRideApi()
        val config = AuthConfig(app = AppSurface.PASSENGER)
        val sessions = AuthSessionManager(
            api = { api.iam },
            store = AuthSessionStore(InMemorySecureStore(), config),
            config = config,
        )
        // Owned, because the resend countdown is a `while (true) { delay(1.seconds) }` on
        // `viewModelScope` and nothing else can end one — see `MainDispatcher.own`.
        return main.own(
            LoginViewModel(
                sessions = sessions,
                onboarding = OnboardingRepository(content = api.content, iam = api.iam, preferences = preferences),
                profiles = PassengerProfileRepository(iam = api.iam),
                preferences = preferences,
                pushTokens = PushTokenProvider(),
            ),
        )
    }

    private fun verified(isNewUser: Boolean) = VerifyOtpResponse(
        accessToken = "access-token",
        refreshToken = "refresh-token",
        expiresIn = EXPIRES_IN_SECONDS,
        user = profile(firstName = if (isNewUser) null else "Ramith de Silva"),
        isNewUser = isNewUser,
    )

    private fun profile(firstName: String?) = UserProfile(
        userId = "01JPAX00000000000000000000",
        phone = "+94771234567",
        firstName = firstName,
        role = Role.PASSENGER,
    )

    private companion object {
        const val EXPIRES_IN_SECONDS = 1800

        /** Longer than any plausible build-host stall, so the assertion cannot race a real clock. */
        const val LONG_COOLDOWN_SECONDS = 3600

        const val AUTH_ID = "01JAUTH0000000000000000001"
    }
}
