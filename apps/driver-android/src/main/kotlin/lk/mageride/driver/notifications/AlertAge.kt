package lk.mageride.driver.notifications

import androidx.annotation.StringRes
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.res.stringResource
import lk.mageride.driver.R
import lk.mageride.driver.ui.component.StatusTone
import lk.mageride.driver.ui.theme.MageRideTheme
import kotlin.time.Clock
import kotlin.time.Duration.Companion.days
import kotlin.time.Duration.Companion.hours
import kotlin.time.Duration.Companion.minutes
import kotlin.time.ExperimentalTime
import kotlin.time.Instant

/**
 * How old an alert is, as SCR-DA-034 prints it — *"2 min ago"*, *"1 h ago"*, *"Yesterday"*.
 *
 * **String resources rather than `DateUtils.getRelativeTimeSpanString`.** The platform helper reads
 * `Locale.getDefault()`, and this app's language is applied per-*context* by
 * `MainActivity.attachBaseContext` through `DriverLocale.wrap` (the per-app `LocaleManager` is API
 * 33+ and the floor here is 26) — so the helper would print English on a Sinhala screen for exactly
 * the users this platform is for.
 *
 * @property label Which resource to render.
 * @property value Its `%1$d` argument, or `null` for the two labels that take none.
 */
internal data class AlertAge(@param:StringRes val label: Int, val value: Int?) {

    internal companion object {

        /**
         * The age of an alert received at [receivedAt], as of [now].
         *
         * Elapsed time rather than a calendar comparison, which is deliberate: *"Yesterday"* here
         * means *"about a day ago"*, and a notification is not a business date. Nothing on this
         * screen is compared against a Colombo day boundary, so D-38's rule does not bite — that one
         * is about `fee_date` and `period_month`, which this is not.
         */
        @OptIn(ExperimentalTime::class)
        fun of(receivedAt: Instant, now: Instant): AlertAge {
            val elapsed = now - receivedAt
            return when {
                elapsed < 1.minutes -> AlertAge(R.string.alert_age_now, null)
                elapsed < 1.hours -> AlertAge(R.string.alert_age_minutes, elapsed.inWholeMinutes.toInt())
                elapsed < 1.days -> AlertAge(R.string.alert_age_hours, elapsed.inWholeHours.toInt())
                elapsed < 2.days -> AlertAge(R.string.alert_age_yesterday, null)
                else -> AlertAge(R.string.alert_age_days, elapsed.inWholeDays.toInt())
            }
        }
    }
}

/** [AlertAge] rendered. Reads the wall clock, so it is a composable rather than a pure function. */
@OptIn(ExperimentalTime::class)
@Composable
internal fun alertAge(receivedAt: Instant): String {
    val age = AlertAge.of(receivedAt, Clock.System.now())
    return age.value?.let { stringResource(age.label, it) } ?: stringResource(age.label)
}

/**
 * The colour of an alert's leading square.
 *
 * Resolved from [StatusTone] rather than from a fourth palette, because D2' §0.2 has exactly these
 * roles and the wireframe's own row tints (`primaryContainer`, `#FBE2E2`, `#E4F5EA`) are the same
 * three ideas — a dispatch row, a money-out row and a money-in row. `StatusPill` maps the identical
 * table; it is private there because a pill is a pill, and this is a square.
 */
@Composable
internal fun alertTone(kind: AlertKind): Color = when (kind.tone) {
    StatusTone.DONE -> MageRideTheme.status.success
    StatusTone.PENDING -> MageRideTheme.status.warning
    StatusTone.INFO -> MaterialTheme.colorScheme.secondary
    StatusTone.NEUTRAL -> MaterialTheme.colorScheme.primary
}
