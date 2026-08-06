package lk.mageride.passenger

import android.app.Application
import android.app.NotificationChannel
import android.app.NotificationManager
import androidx.core.content.getSystemService
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import lk.mageride.passenger.di.passengerAppModule
import lk.mageride.passenger.push.PushChannels
import lk.mageride.shared.di.initKoin
import lk.mageride.shared.platform.PlatformAttestationProvider
import org.koin.android.ext.android.get
import org.koin.android.ext.koin.androidContext
import org.koin.android.ext.koin.androidLogger
import org.koin.core.logger.Level

/**
 * Process entry point: starts Koin, declares the notification channels, and warms up attestation.
 *
 * Deliberately small. Anything that can wait for a screen waits for a screen — a passenger on a
 * five-year-old handset feels every millisecond of `Application.onCreate`, and the three things
 * below are here because they genuinely cannot be anywhere else. **The database is not opened
 * here**: `PassengerDatabase` defers it so the Keystore round trip is paid by the first screen
 * that reads a table rather than by every cold start.
 *
 * **The live socket is not opened here either.** It is `PassengerShell`'s, because a connection
 * dialled before there is a session has no token to present.
 */
internal class PassengerApplication : Application() {

    /**
     * Start-up work that outlives any Activity.
     *
     * Not `GlobalScope` and not a service scope: the warm-up must survive the first Activity being
     * recreated (which happens on the very first launch if the passenger rotates the phone) and
     * must die with the process, which is exactly what an Application-held scope does.
     */
    private val startUpScope = CoroutineScope(SupervisorJob() + Dispatchers.Default)

    override fun onCreate() {
        super.onCreate()

        initKoin(
            appModules = listOf(passengerAppModule()),
            appDeclaration = {
                androidContext(this@PassengerApplication)
                // ERROR in release: Koin's INFO logging names every definition it resolves,
                // which on this graph is a line per API on every cold start.
                androidLogger(if (BuildConfig.DEBUG) Level.INFO else Level.ERROR)
            },
        )

        registerNotificationChannels()

        // C014's handoff: "call its `warmUp()` at start-up; without the warm-up the first
        // sensitive mutation of the session pays the whole Play Integrity preparation cost, and
        // the first sensitive mutation is `POST /v1/auth/otp/request`" — i.e. the OTP request on
        // SCR-PA-003, the first thing a new passenger does.
        startUpScope.launch { get<PlatformAttestationProvider>().warmUp() }
    }

    /**
     * The two channels the app publishes on, created once per install.
     *
     * Creating them here rather than at first use is what makes them appear in Android's own
     * notification settings before the first push arrives — a passenger who has silenced the app
     * cannot un-silence a channel that does not exist yet.
     */
    private fun registerNotificationChannels() {
        val manager = getSystemService<NotificationManager>() ?: return

        manager.createNotificationChannel(
            NotificationChannel(
                PushChannels.RIDES,
                getString(R.string.push_channel_rides_name),
                // A driver arriving and P-02's 300-second location request are both things the
                // passenger is actively waiting on. IMPORTANCE_HIGH is what makes them heads-up
                // notifications; anything lower and the request expires unseen.
                NotificationManager.IMPORTANCE_HIGH,
            ).apply { description = getString(R.string.push_channel_rides_description) },
        )

        manager.createNotificationChannel(
            NotificationChannel(
                PushChannels.GENERAL,
                getString(R.string.push_channel_general_name),
                NotificationManager.IMPORTANCE_DEFAULT,
            ).apply { description = getString(R.string.push_channel_general_description) },
        )
    }
}
