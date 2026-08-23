package lk.mageride.driver

import android.app.Application
import android.app.NotificationChannel
import android.app.NotificationManager
import androidx.core.content.getSystemService
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import lk.mageride.driver.di.driverAppModule
import lk.mageride.driver.location.PositionForegroundService
import lk.mageride.driver.push.PushChannels
import lk.mageride.shared.di.initKoin
import lk.mageride.shared.platform.PlatformAttestationProvider
import org.koin.android.ext.android.get
import org.koin.android.ext.koin.androidContext
import org.koin.android.ext.koin.androidLogger
import org.koin.core.logger.Level

/**
 * Process entry point: starts Koin, declares the notification channels, and warms up attestation.
 *
 * Deliberately small. Anything that can wait for a screen waits for a screen — a driver on a
 * five-year-old handset feels every millisecond of `Application.onCreate`, and the two things
 * below are here because they genuinely cannot be anywhere else.
 */
internal class DriverApplication : Application() {

    /**
     * Start-up work that outlives any Activity.
     *
     * Not `GlobalScope` and not a service scope: the warm-up must survive the first Activity being
     * recreated (which happens on the very first launch if the driver rotates the phone) and must
     * die with the process, which is exactly what an Application-held scope does.
     */
    private val startUpScope = CoroutineScope(SupervisorJob() + Dispatchers.Default)

    override fun onCreate() {
        super.onCreate()

        initKoin(
            appModules = listOf(driverAppModule()),
            appDeclaration = {
                androidContext(this@DriverApplication)
                // ERROR in release: Koin's INFO logging names every definition it resolves,
                // which on this graph is a line per API on every cold start.
                androidLogger(if (BuildConfig.DEBUG) Level.INFO else Level.ERROR)
            },
        )

        registerNotificationChannels()

        // C014's handoff: "call its `warmUp()` at start-up; without the warm-up the first
        // sensitive mutation of the session pays the whole Play Integrity preparation cost, and
        // the first sensitive mutation is `POST /v1/auth/otp/request`" — i.e. the OTP request on
        // the login screen, the first thing a new driver does.
        startUpScope.launch { get<PlatformAttestationProvider>().warmUp() }
    }

    /**
     * The three channels the app publishes on, created once per install.
     *
     * Creating them here rather than at first use is what makes them appear in Android's own
     * notification settings before the first push arrives — a driver who has silenced the app
     * cannot un-silence a channel that does not exist yet.
     */
    private fun registerNotificationChannels() {
        val manager = getSystemService<NotificationManager>() ?: return

        manager.createNotificationChannel(
            NotificationChannel(
                PushChannels.RIDE_OFFERS,
                getString(R.string.push_channel_rides_name),
                // E-01's offer has a 15-second life. IMPORTANCE_HIGH is what makes it a heads-up
                // notification; anything lower and the driver sees it after it has expired.
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

        manager.createNotificationChannel(
            NotificationChannel(
                PositionForegroundService.CHANNEL_ID,
                getString(R.string.location_channel_name),
                // LOW: the foreground-service notification is a legal and UX requirement, not an
                // alert. IMPORTANCE_DEFAULT would buzz the handset every time a driver goes online.
                NotificationManager.IMPORTANCE_LOW,
            ).apply { description = getString(R.string.location_channel_description) },
        )
    }
}
