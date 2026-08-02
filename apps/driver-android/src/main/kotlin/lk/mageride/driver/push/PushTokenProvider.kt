package lk.mageride.driver.push

import com.google.firebase.messaging.FirebaseMessaging
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlin.coroutines.resume

/**
 * This install's FCM registration token, or `null`.
 *
 * `POST /v1/auth/otp/request` carries it (`RequestOtpRequest.fcmToken`) so the first notification
 * a new driver is sent — the vehicle-approval push, US-2.14 — has somewhere to land before they
 * have opened the app a second time.
 *
 * **Null is a supported answer, not a failure.** There is no `google-services.json` in this repo
 * (C124 owns the Firebase project), so `FirebaseMessaging.getInstance()` throws
 * `IllegalStateException: Default FirebaseApp is not initialized` on every build produced today.
 * Failing a driver's login over a push token that is inert anyway would be the wrong trade, so
 * the failure is swallowed here and the sign-in goes ahead without one.
 */
internal class PushTokenProvider {

    /** Reads the token, waiting for the Play-services task. Answers `null` rather than throwing. */
    @Suppress("TooGenericExceptionCaught")
    suspend fun current(): String? = try {
        suspendCancellableCoroutine { continuation ->
            FirebaseMessaging.getInstance().token
                .addOnSuccessListener { token -> continuation.resume(token) }
                .addOnFailureListener { continuation.resume(null) }
                .addOnCanceledListener { continuation.resume(null) }
        }
    } catch (cause: CancellationException) {
        throw cause
    } catch (_: Throwable) {
        null
    }
}
