package lk.mageride.driver.onboarding

import io.ktor.http.HttpStatusCode
import kotlinx.coroutines.runBlocking
import lk.mageride.driver.push.PushTokenProvider
import lk.mageride.shared.data.models.AppSurface
import lk.mageride.shared.data.models.Language
import lk.mageride.shared.data.models.Role
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
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * SCR-DA-003 — the phone half, the code half, and what the screen says when the server says no.
 *
 * Everything about tokens is C014's and is tested there. What is C068's, and what these assert,
 * is the state machine on top of it: which half the CTA submits, when Resend is refused locally
 * (D-32), how a dead attempt gets the driver back to the number field, and — the one with real
 * consequences — where a driver lands the moment they are signed in.
 */
class LoginViewModelTest {

    private val main = MainDispatcher()
    private val backend = FakeApiBackend()
    private val preferences = FakeOnboardingPreferences(
        language = Language.SI,
        operatingCityCode = "colombo",
        preferencesPendingSync = true,
    )

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

        // Typed with the trunk zero, which is how a driver reads it off their own handset.
        model.onPhoneChanged("0771234567")
        assertEquals("771234567", model.state.value.phone, "normalised on every keystroke")
        assertTrue(model.state.value.canSubmit)
    }

    @Test
    fun requesting_a_code_moves_to_the_otp_half_and_starts_the_resend_cooldown() = runBlocking {
        val model = signedOutWithNumber()

        model.submit()
        // The countdown is its own coroutine, so it can land a tick after the request does.
        val state = model.state.await { it.phase == LoginPhase.OTP && !it.busy && it.resendInSeconds > 0 }

        assertTrue(backend.called("requestOtp"))
        // D-32's 60-second bucket. Resend is refused locally inside it, because a call the server
        // was never going to honour still spends one of the five attempts an hour.
        assertFalse(state.canResend, "the cooldown is running")
    }

    @Test
    fun the_cta_will_not_verify_a_partial_code() = runBlocking {
        val model = signedOutWithNumber()
        model.submit()
        model.state.await { it.phase == LoginPhase.OTP && !it.busy }

        model.onOtpChanged("123")
        assertFalse(model.state.value.canSubmit)

        model.onOtpChanged("123456")
        assertTrue(model.state.value.canSubmit)
    }

    @Test
    fun a_verified_new_driver_lands_on_profile_setup_and_the_first_run_choices_reach_iam() = runBlocking {
        backend.returns("verifyOtp", verified(isNewUser = true))
        backend.returns("getMyProfile", profile(firstName = null))
        val model = signedOutWithNumber()

        model.submit()
        model.state.await { it.phase == LoginPhase.OTP && !it.busy }
        model.onOtpChanged("123456")
        model.submit()
        model.destination.await { it != null }

        // AL-27's Change 6/22 order: identity before Home, no vehicle anywhere in it.
        assertEquals(OnboardingDestination.PROFILE_SETUP, model.destination.value)
        // SCR-DA-002 ran signed out, so this is the first moment `iam.users` can be written
        // (D-26 language, AL-27 operating city).
        assertTrue(backend.called("setLanguagePreference"))
        assertTrue(backend.called("setOperatingCity"))
        assertFalse(preferences.preferencesPendingSync, "and they are not sent twice")
    }

    @Test
    fun a_returning_driver_with_a_profile_and_no_vehicle_lands_on_home() = runBlocking {
        preferences.permissionsAcknowledged = true
        backend.returns("verifyOtp", verified(isNewUser = false))
        backend.returns("getMyProfile", profile(firstName = "K. Fernando"))
        val model = signedOutWithNumber()

        model.submit()
        model.state.await { it.phase == LoginPhase.OTP && !it.busy }
        model.onOtpChanged("123456")
        model.submit()
        model.destination.await { it != null }

        // Nothing here asks about a vehicle. That is the fence, not an omission (AL-27).
        assertEquals(OnboardingDestination.HOME, model.destination.value)
    }

    @Test
    fun the_destination_comes_from_the_stored_profile_rather_than_from_is_new_user() = runBlocking {
        // A driver who installed, signed in and killed the app before Profile Setup is not a new
        // user on the next verify — and still has no profile. Trusting `isNewUser` would send
        // them to Home with no name and no licence on file.
        backend.returns("verifyOtp", verified(isNewUser = false))
        backend.returns("getMyProfile", profile(firstName = null))
        val model = signedOutWithNumber()

        model.submit()
        model.state.await { it.phase == LoginPhase.OTP && !it.busy }
        model.onOtpChanged("123456")
        model.submit()
        model.destination.await { it != null }

        assertEquals(OnboardingDestination.PROFILE_SETUP, model.destination.value)
    }

    @Test
    fun a_wrong_code_keeps_the_driver_on_the_otp_half_with_the_code_cleared() = runBlocking {
        backend.fails("verifyOtp", HttpStatusCode.Unauthorized, "invalid-otp")
        val model = signedOutWithNumber()
        model.submit()
        model.state.await { it.phase == LoginPhase.OTP && !it.busy }
        model.onOtpChanged("123456")

        model.submit()
        model.state.await { it.error != null }

        assertEquals(LoginPhase.OTP, model.state.value.phase, "the attempt is still alive")
        assertEquals("", model.state.value.otp)
        // D-26: copy is resolved from the kebab `code`, never from the server's English `detail`.
        assertEquals(lk.mageride.driver.R.string.error_otp_invalid, model.state.value.error)
        assertNull(model.destination.value)
    }

    @Test
    fun a_locked_attempt_sends_the_driver_back_to_the_number() = runBlocking {
        // `423 otp-locked` spent the attempt budget: that `authId` can never succeed, so C014
        // returns to SignedOut and the screen has to start a new attempt rather than offer a
        // seventh digit box.
        backend.fails("verifyOtp", HttpStatusCode.Locked, "otp-locked")
        val model = signedOutWithNumber()
        model.submit()
        model.state.await { it.phase == LoginPhase.OTP && !it.busy }
        model.onOtpChanged("123456")

        model.submit()
        model.state.await { it.error != null }

        assertEquals(LoginPhase.PHONE, model.state.value.phase)
        assertEquals(lk.mageride.driver.R.string.error_otp_locked, model.state.value.error)
    }

    @Test
    fun a_rate_limited_request_says_so_rather_than_something_went_wrong() = runBlocking {
        backend.fails("requestOtp", HttpStatusCode.TooManyRequests, "otp-rate-limited")
        val model = signedOutWithNumber()

        model.submit()
        model.state.await { it.error != null }

        assertEquals(LoginPhase.PHONE, model.state.value.phase, "no code went out")
        assertEquals(lk.mageride.driver.R.string.error_otp_rate_limited, model.state.value.error)
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
        val config = AuthConfig(app = AppSurface.DRIVER)
        val sessions = AuthSessionManager(
            api = { api.iam },
            store = AuthSessionStore(InMemorySecureStore(), config),
            config = config,
        )
        return LoginViewModel(
            sessions = sessions,
            onboarding = OnboardingRepository(content = api.content, iam = api.iam, preferences = preferences),
            profiles = DriverProfileRepository(registry = api.registry, iam = api.iam),
            preferences = preferences,
            pushTokens = PushTokenProvider(),
        )
    }

    private fun verified(isNewUser: Boolean) = VerifyOtpResponse(
        accessToken = "access-token",
        refreshToken = "refresh-token",
        expiresIn = EXPIRES_IN_SECONDS,
        user = profile(firstName = if (isNewUser) null else "K. Fernando"),
        isNewUser = isNewUser,
    )

    private fun profile(firstName: String?) = UserProfile(
        userId = "01JDRIVER00000000000000000",
        phone = "+94771234567",
        firstName = firstName,
        role = Role.DRIVER,
    )

    private companion object {
        const val EXPIRES_IN_SECONDS = 1800
    }
}
