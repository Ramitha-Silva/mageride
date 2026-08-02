package lk.mageride.driver.push

import android.app.PendingIntent
import android.content.Intent
import android.net.Uri
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import com.google.firebase.messaging.FirebaseMessagingService
import com.google.firebase.messaging.RemoteMessage
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch
import lk.mageride.driver.MainActivity
import lk.mageride.driver.R
import lk.mageride.shared.data.api.comms.NotificationApi
import lk.mageride.shared.data.models.ClientPlatform
import lk.mageride.shared.data.models.comms.RegisterPushTokenRequest
import lk.mageride.shared.domain.auth.AuthSessionManager
import org.koin.android.ext.android.inject

/**
 * The FCM receiver — E-01's ride offers and every other push notification-svc sends.
 *
 * **Data messages, not notification messages.** notification-svc sends a `data` dictionary
 * (`kind`, `deeplink`, `notificationId`, per-kind extras) rather than a `notification` block, which
 * is what puts this callback in charge of what the driver sees. That matters for exactly one
 * reason: a `notification` message is posted by the system and never reaches the app while it is
 * backgrounded, and a 15-second offer that the app learns about only when the driver next opens it
 * has already expired.
 *
 * A backgrounded app has no socket (`signalr-hub.md` §6), so this path is not a nicety — it is the
 * only delivery there is.
 */
internal class DriverMessagingService : FirebaseMessagingService() {

    private val router: PushRouter by inject()
    private val sessions: AuthSessionManager by inject()
    private val notifications: NotificationApi by inject()

    // FirebaseMessagingService's callbacks run on a background thread with a ~20 s budget and no
    // lifecycle of their own. A service-owned scope cancelled in onDestroy is what keeps a token
    // registration from outliving the process that started it.
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    /**
     * A new FCM registration token — first launch, app data cleared, or Google rotating it.
     *
     * `onRegistered`, not `onNewToken`: firebase-messaging 25 deprecated the latter in favour of
     * the `onRegistered`/`onUnregistered` pair, and an override of a deprecated member is a
     * warning this build treats as noise it cannot afford.
     *
     * Registered against the *device*, which is why [AuthSessionManager.deviceId] is the id sent
     * rather than a fresh UUID: `iam.devices` is keyed on it (AL-08), and a second identifier
     * would make a re-registration look like a new handset and revoke the session.
     *
     * Nothing is sent while signed out. The token is not lost — Firebase replays the current one
     * on the next start, and the login flow registers it once there is a session to attach it to.
     */
    override fun onRegistered(token: String) {
        scope.launch {
            runCatching {
                notifications.registerPushToken(
                    RegisterPushTokenRequest(
                        token = token,
                        platform = ClientPlatform.ANDROID,
                        deviceId = sessions.deviceId(),
                    ),
                )
            }
        }
    }

    override fun onMessageReceived(message: RemoteMessage) {
        val push = PushMessage.from(message.data)

        // Two things happen, in this order and independently: the destination is offered to the
        // shell (which acts on it only if the app is in the foreground), and a notification is
        // posted (which is what reaches a driver who is not looking at the screen).
        router.offer(push)
        post(push, message)
    }

    /**
     * Posts the tray notification.
     *
     * The tap target is [MainActivity] carrying the deep link as its intent data, so the cold-start
     * and warm-start paths are the same code — `MainActivity.routeFromIntent` reads it either way.
     */
    private fun post(push: PushMessage, message: RemoteMessage) {
        val title = message.notification?.title ?: push.data[DATA_TITLE] ?: getString(R.string.app_name)
        val body = message.notification?.body ?: push.data[DATA_BODY].orEmpty()

        val intent = Intent(this, MainActivity::class.java).apply {
            flags = Intent.FLAG_ACTIVITY_SINGLE_TOP or Intent.FLAG_ACTIVITY_CLEAR_TOP
            push.deeplink?.let { data = Uri.parse(it) }
        }

        val pending = PendingIntent.getActivity(
            this,
            // The notification id doubles as the request code so two live notifications do not
            // collapse into one PendingIntent pointing at whichever arrived first.
            push.notificationId?.hashCode() ?: 0,
            intent,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )

        val notification = NotificationCompat.Builder(this, PushChannels.channelFor(push.kind))
            .setSmallIcon(android.R.drawable.ic_menu_directions)
            .setContentTitle(title)
            .setContentText(body)
            .setAutoCancel(true)
            .setContentIntent(pending)
            .setPriority(
                if (push.kind == PushMessage.KIND_RIDE_OFFER) {
                    NotificationCompat.PRIORITY_HIGH
                } else {
                    NotificationCompat.PRIORITY_DEFAULT
                },
            )
            .build()

        val manager = NotificationManagerCompat.from(this)
        if (manager.areNotificationsEnabled()) {
            // POST_NOTIFICATIONS is a runtime permission from API 33 and SCR-DA-007 asks for it.
            // A driver who refused it still gets the offer through the socket when the app is
            // open; posting without the permission would throw here and kill the callback.
            manager.notify(push.notificationId?.hashCode() ?: body.hashCode(), notification)
        }
    }

    override fun onDestroy() {
        scope.cancel()
        super.onDestroy()
    }

    private companion object {
        const val DATA_TITLE = "title"
        const val DATA_BODY = "body"
    }
}
