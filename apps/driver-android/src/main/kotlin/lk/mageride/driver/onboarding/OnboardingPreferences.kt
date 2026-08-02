package lk.mageride.driver.onboarding

import android.content.Context
import android.content.SharedPreferences
import androidx.core.content.edit
import lk.mageride.shared.data.models.Language

/**
 * The three first-run answers the app needs **before** it has a session.
 *
 * SCR-DA-002 asks for a language and an operating city on a handset that has never signed in
 * (`GET /v1/config/cities` is the one public route in the flow), and SCR-DA-007 asks for
 * permissions. None of the three can be written to `iam.users` at the moment they are given, so
 * they are held here and pushed to the server on the first authenticated call — see
 * [OnboardingRepository.syncPreferences].
 *
 * An interface with an Android implementation because the values are also read from
 * `Activity.attachBaseContext`, before Koin's graph is reachable, and because a view-model test
 * has no `SharedPreferences` (a local unit test's is a stub whose every member returns a default).
 */
internal interface OnboardingPreferences {

    /** The chosen UI language. `null` until SCR-DA-002 has been answered. */
    var language: Language?

    /** The chosen `config.operating_cities` code (US-1.3a). `null` until SCR-DA-002. */
    var operatingCityCode: String?

    /** Whether the local choices still have to be pushed to `iam.users` after sign-in. */
    var preferencesPendingSync: Boolean

    /** Whether SCR-DA-007 has been shown and dismissed. Grants themselves are asked of the OS. */
    var permissionsAcknowledged: Boolean

    /** Whether SCR-DA-002 has been answered — "first run only" (D2' §B). */
    val firstRunComplete: Boolean get() = language != null && operatingCityCode != null
}

/** [OnboardingPreferences] over a private `SharedPreferences` file. */
internal class AndroidOnboardingPreferences(context: Context) : OnboardingPreferences {

    private val store: SharedPreferences =
        context.applicationContext.getSharedPreferences(FILE, Context.MODE_PRIVATE)

    override var language: Language?
        get() = store.getString(KEY_LANGUAGE, null)?.let(Language::fromWire)
        set(value) = store.edit { putString(KEY_LANGUAGE, value?.wire) }

    override var operatingCityCode: String?
        get() = store.getString(KEY_CITY, null)
        set(value) = store.edit { putString(KEY_CITY, value) }

    override var preferencesPendingSync: Boolean
        get() = store.getBoolean(KEY_PENDING_SYNC, false)
        set(value) = store.edit { putBoolean(KEY_PENDING_SYNC, value) }

    override var permissionsAcknowledged: Boolean
        get() = store.getBoolean(KEY_PERMISSIONS, false)
        set(value) = store.edit { putBoolean(KEY_PERMISSIONS, value) }

    private companion object {
        // Not the C018 database: `mobile_db_schema.md` §0.4 keeps that file encrypted and opening
        // it is `suspend`, and `attachBaseContext` cannot wait for a Keystore round trip to know
        // which locale to inflate resources in. Nothing here is a secret.
        const val FILE = "driver_onboarding"
        const val KEY_LANGUAGE = "language"
        const val KEY_CITY = "operating_city_code"
        const val KEY_PENDING_SYNC = "preferences_pending_sync"
        const val KEY_PERMISSIONS = "permissions_acknowledged"
    }
}
