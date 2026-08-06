package lk.mageride.passenger.subscription

import lk.mageride.shared.data.models.AppSurface
import lk.mageride.shared.data.models.Role
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.iam.RequestOtpResponse
import lk.mageride.shared.data.models.iam.UserProfile
import lk.mageride.shared.data.models.iam.VerifyOtpResponse
import lk.mageride.shared.domain.auth.AuthConfig
import lk.mageride.shared.domain.auth.AuthSessionManager
import lk.mageride.shared.domain.auth.AuthSessionStore
import lk.mageride.shared.testing.fake.FakeApiBackend
import lk.mageride.shared.testing.fake.InMemorySecureStore
import lk.mageride.shared.testing.fake.mageRideApi

/**
 * A signed-in [AuthSessionManager], for the three view models that need a passenger id.
 *
 * **Signed in through the real OTP flow rather than by writing a token into a store.**
 * `SessionState.SignedIn` is minted in exactly one place (`verifyOtp`) and `AuthSessionManager`
 * deliberately gives no other door — a helper that reached around it would be testing a state the
 * app cannot actually be in. Two canned responses over `FakeApiBackend` is the whole cost.
 *
 * C077's own tests build a signed-**out** manager for the same reason in reverse: they are testing
 * the flow itself. This is the other side, and it lives beside the cluster that needs it rather
 * than in the shared harness, because it is two API stubs and a phone number.
 */
internal fun signedInSession(userId: Ulid = PASSENGER_ID): AuthSessionManager {
    val backend = FakeApiBackend()
    backend.returns(
        "requestOtp",
        RequestOtpResponse(authId = AUTH_ID, attemptsRemaining = 3, cooldownSeconds = 60, isBlocked = false),
    )
    backend.returns(
        "verifyOtp",
        VerifyOtpResponse(
            accessToken = "access-token",
            refreshToken = "refresh-token",
            expiresIn = EXPIRES_IN_SECONDS,
            user = UserProfile(userId = userId, phone = PHONE, firstName = "A. Perera", role = Role.PASSENGER),
            isNewUser = false,
        ),
    )

    val config = AuthConfig(app = AppSurface.PASSENGER)
    return AuthSessionManager(
        api = { backend.mageRideApi().iam },
        store = AuthSessionStore(InMemorySecureStore(), config),
        config = config,
    )
}

/** Drives [manager] through the OTP flow. Call from inside a `runBlocking`. */
internal suspend fun AuthSessionManager.signIn() {
    requestOtp(PHONE)
    verifyOtp(OTP)
}

/** The passenger every subscription in these tests belongs to. */
internal const val PASSENGER_ID: Ulid = "01JPAX00000000000000000000"

private const val PHONE = "+94771234567"
private const val OTP = "482913"
private const val AUTH_ID = "01JAUTH0000000000000000001"
private const val EXPIRES_IN_SECONDS = 1800
