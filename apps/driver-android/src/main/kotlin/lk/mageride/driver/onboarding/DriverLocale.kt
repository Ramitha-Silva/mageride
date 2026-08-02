package lk.mageride.driver.onboarding

import android.app.Activity
import android.content.Context
import android.content.ContextWrapper
import android.content.res.Configuration
import java.util.Locale

/**
 * Applies the language SCR-DA-002 chose to the resources the app inflates.
 *
 * D-26 makes the language a *user* preference, not the handset's: a driver on a phone set to
 * English who picks සිංහල must get a Sinhala app, and AL-26 makes Sinhala the **default** rather
 * than a translation of English. Android's per-app locale API (`LocaleManager`) is API 33+, and
 * the URD NFR-22 floor is API 26 — so the base context is wrapped instead, which works on every
 * level this app supports and needs no `appcompat`.
 *
 * The wrap is applied in `MainActivity.attachBaseContext`, which is the only place early enough:
 * by `onCreate` the `Resources` a composable will read have already been resolved.
 */
internal object DriverLocale {

    /**
     * [base] with the stored language applied, or [base] itself when SCR-DA-002 has not been
     * answered (the handset's own locale is the right answer until the driver says otherwise).
     */
    fun wrap(base: Context): Context {
        val language = AndroidOnboardingPreferences(base).language ?: return base
        val locale = Locale.forLanguageTag(language.wire)

        // `Locale.setDefault` covers what does not go through `Resources` — number and date
        // formatting in particular, which a fare and a fee date both depend on.
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
