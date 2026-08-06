package lk.mageride.passenger.shell

import android.app.Activity
import android.content.Context
import android.content.ContextWrapper
import android.content.SharedPreferences
import android.content.res.Configuration
import androidx.core.content.edit
import lk.mageride.shared.data.models.Language
import java.util.Locale

/**
 * The handful of answers the app needs **before** it has a session.
 *
 * SCR-PA-002 asks for a language on a handset that has never signed in, and SCR-PA-005 asks for
 * location permission. Neither can be written to `iam.users` at the moment it is given, so they
 * are held here and pushed to the server on the first authenticated call — which is C077's job,
 * not the shell's.
 *
 * **The shell owns this store because the shell reads it from `attachBaseContext`**, before a
 * composition exists and while Koin's graph is reachable only through the global context. It is an
 * interface because a view-model test has no `SharedPreferences` — a local unit test's stub
 * answers a default for every member, which would make a language choice look like it saved and
 * silently do nothing.
 */
internal interface AppPreferences {

    /** The chosen UI language (D-26, AL-26). `null` until SCR-PA-002 has been answered. */
    var language: Language?

    /** Whether the local choice still has to be pushed to `iam.users` after sign-in. */
    var languagePendingSync: Boolean

    /** Whether SCR-PA-005 has been shown and dismissed. Grants themselves are asked of the OS. */
    var locationRationaleAcknowledged: Boolean

    /**
     * SCR-PA-015a's *"remembers last choice"* — the wire value of the last [CallType] used, or
     * `null` before the first call.
     *
     * Held here rather than in the ride's state because it outlives every ride: a passenger who
     * always calls normally should not be asked again on their next trip. Δ C079/C080.
     */
    var lastCallType: String?

    /**
     * Whether the *"your number is visible to the other party"* tooltip has been shown (US-26.5).
     *
     * Once, on the **first** call. AL-48 withdrew number masking, so this is the PDPA transparency
     * that replaced it — and a disclosure shown on every call is a disclosure nobody reads.
     */
    var callNumberNoticeShown: Boolean

    /**
     * SCR-PA-027's *"Default payment"* — the wire value of the chosen `PaymentMethod`, or `null`
     * before the passenger has chosen one (US-22.4, AL-14).
     *
     * Here rather than only on `iam.users` because the column cannot hold every rail this app
     * offers: `DefaultPaymentMethod` is still `[cash, lankaqr, onepay]` and AL-57 replaced `onepay`
     * with the **wallet**, which has no value in that enum. See `PaymentPreference`. Δ C083.
     */
    var defaultPaymentMethod: String?

    /** Whether SCR-PA-002 has been answered — "first-launch only" (D2' §A). */
    val firstRunComplete: Boolean get() = language != null
}

/** [AppPreferences] over a private `SharedPreferences` file. */
internal class AndroidAppPreferences(context: Context) : AppPreferences {

    private val store: SharedPreferences =
        context.applicationContext.getSharedPreferences(FILE, Context.MODE_PRIVATE)

    override var language: Language?
        get() = store.getString(KEY_LANGUAGE, null)?.let(Language::fromWire)
        set(value) = store.edit { putString(KEY_LANGUAGE, value?.wire) }

    override var languagePendingSync: Boolean
        get() = store.getBoolean(KEY_PENDING_SYNC, false)
        set(value) = store.edit { putBoolean(KEY_PENDING_SYNC, value) }

    override var locationRationaleAcknowledged: Boolean
        get() = store.getBoolean(KEY_LOCATION_RATIONALE, false)
        set(value) = store.edit { putBoolean(KEY_LOCATION_RATIONALE, value) }

    override var lastCallType: String?
        get() = store.getString(KEY_LAST_CALL_TYPE, null)
        set(value) = store.edit { putString(KEY_LAST_CALL_TYPE, value) }

    override var callNumberNoticeShown: Boolean
        get() = store.getBoolean(KEY_CALL_NUMBER_NOTICE, false)
        set(value) = store.edit { putBoolean(KEY_CALL_NUMBER_NOTICE, value) }

    override var defaultPaymentMethod: String?
        get() = store.getString(KEY_DEFAULT_PAYMENT, null)
        set(value) = store.edit { putString(KEY_DEFAULT_PAYMENT, value) }

    private companion object {
        const val KEY_LAST_CALL_TYPE = "last_call_type"
        const val KEY_CALL_NUMBER_NOTICE = "call_number_notice_shown"
        const val KEY_DEFAULT_PAYMENT = "default_payment_method"

        // Not the C018 database: `mobile_db_schema.md` §0.4 keeps that file encrypted and opening
        // it is `suspend`, and `attachBaseContext` cannot wait for a Keystore round trip to know
        // which locale to inflate resources in. Nothing here is a secret.
        const val FILE = "passenger_prefs"
        const val KEY_LANGUAGE = "language"
        const val KEY_PENDING_SYNC = "language_pending_sync"
        const val KEY_LOCATION_RATIONALE = "location_rationale_acknowledged"
    }
}

/**
 * Applies the language SCR-PA-002 chose to the resources the app inflates.
 *
 * D-26 makes the language a *user* preference, not the handset's: a passenger on a phone set to
 * English who picks සිංහල must get a Sinhala app, and AL-26 makes Sinhala the **default** rather
 * than a translation of English. Android's per-app locale API (`LocaleManager`) is API 33+, and
 * the URD NFR-22 floor is API 26 — so the base context is wrapped instead, which works on every
 * level this app supports and needs no `appcompat`.
 *
 * The wrap is applied in `MainActivity.attachBaseContext`, which is the only place early enough:
 * by `onCreate` the `Resources` a composable will read have already been resolved.
 */
internal object PassengerLocale {

    /**
     * [base] with the stored language applied, or [base] itself when SCR-PA-002 has not been
     * answered (the handset's own locale is the right answer until the passenger says otherwise).
     */
    fun wrap(base: Context): Context {
        val language = AndroidAppPreferences(base).language ?: return base
        val locale = Locale.forLanguageTag(language.wire)

        // `Locale.setDefault` covers what does not go through `Resources` — number and date
        // formatting in particular, which a fare and a scheduled pickup both depend on.
        Locale.setDefault(locale)

        val configuration = Configuration(base.resources.configuration).apply {
            setLocale(locale)
        }
        return ContextWrapper(base.createConfigurationContext(configuration))
    }
}

/**
 * The `Activity` hosting this context, or `null`.
 *
 * Compose hands a screen a `Context`, and a language change has to reach the one thing that can
 * re-inflate resources — `Activity.recreate()`. Walking the `ContextWrapper` chain is the only way
 * to it from inside a composable.
 */
internal tailrec fun Context.findActivity(): Activity? = when (this) {
    is Activity -> this
    is ContextWrapper -> baseContext.findActivity()
    else -> null
}
