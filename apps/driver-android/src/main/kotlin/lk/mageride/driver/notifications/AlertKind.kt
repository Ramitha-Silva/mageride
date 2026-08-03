package lk.mageride.driver.notifications

import androidx.annotation.StringRes
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.ArrowForward
import androidx.compose.material.icons.outlined.CheckCircle
import androidx.compose.material.icons.outlined.DirectionsCar
import androidx.compose.material.icons.outlined.Inventory2
import androidx.compose.material.icons.outlined.Link
import androidx.compose.material.icons.outlined.Notifications
import androidx.compose.material.icons.outlined.Shield
import androidx.compose.material.icons.outlined.TrendingDown
import androidx.compose.ui.graphics.vector.ImageVector
import lk.mageride.driver.R
import lk.mageride.driver.ui.component.StatusTone

/**
 * The alert kinds SCR-DA-034 draws, and what each one looks like.
 *
 * D2' §SCR-DA-034's own list — *"dispatch/fee/registration/sharing/directional"* — with the push
 * types the wireframe names under it: `RIDE_OFFER`, `DIRECTIONAL_EXPIRING`, `LOW_BALANCE`,
 * `TOPUP_CONFIRMED`, `SHARE_REQUEST`, `package_*`, `SOS_*`.
 *
 * **Matched on the wire value, and an unmatched one still draws.** The type is `data.kind`, which is
 * notification-svc's catalogue name and *"grows without a contract change"* — the same reason
 * `NotificationPreferences` is a map rather than an enum. A kind this build has never heard of gets
 * the neutral bell and the push's own title, because a driver being shown nothing is worse than
 * being shown a row with a generic icon.
 *
 * @property tone Which of the four status colours tints the row's leading square.
 * @property icon The wireframe's glyph.
 * @property label What the row says when the push carried no title of its own.
 */
internal enum class AlertKind(val tone: StatusTone, val icon: ImageVector, @param:StringRes val label: Int) {

    /** E-01's fifteen-second offer (`ride_offer`). */
    RideOffer(StatusTone.NEUTRAL, Icons.Outlined.DirectionsCar, R.string.alert_kind_ride_offer),

    /** DT-08's ten-minute warning (`DIRECTIONAL_EXPIRING`, US-10.14). */
    Directional(StatusTone.NEUTRAL, Icons.AutoMirrored.Outlined.ArrowForward, R.string.alert_kind_directional),

    /** US-9.9's `LOW_BALANCE`, once below Rs 200. */
    LowBalance(StatusTone.PENDING, Icons.Outlined.TrendingDown, R.string.alert_kind_low_balance),

    /** `TOPUP_CONFIRMED` / `PAYMENT_CONFIRMED` (US-8.12). */
    MoneyIn(StatusTone.DONE, Icons.Outlined.CheckCircle, R.string.alert_kind_money_in),

    /** A Mode B access request (`SHARE_REQUEST`, US-10.2). */
    Share(StatusTone.INFO, Icons.Outlined.Link, R.string.alert_kind_share),

    /** `package_picked` / `package_delivered` (US-10.12/13). */
    Package(StatusTone.INFO, Icons.Outlined.Inventory2, R.string.alert_kind_package),

    /** `SOS_TRIGGERED` / `SOS_RESOLVED` — the types US-10.7 does not let a driver mute. */
    Safety(StatusTone.PENDING, Icons.Outlined.Shield, R.string.alert_kind_safety),

    /** Everything else notification-svc sends. */
    Other(StatusTone.NEUTRAL, Icons.Outlined.Notifications, R.string.alert_kind_other),
    ;

    internal companion object {

        /**
         * Resolves a stored `type`.
         *
         * Case-insensitive and prefix-matched on the two families the catalogue spells with a
         * suffix (`package_*`, `SOS_*`), which is how D5' §14.4 and the wireframe both write them.
         */
        fun of(type: String): AlertKind {
            val key = type.uppercase()
            return when {
                key == "RIDE_OFFER" -> RideOffer
                key.startsWith("DIRECTIONAL") -> Directional
                key == "LOW_BALANCE" -> LowBalance
                key == "TOPUP_CONFIRMED" || key == "PAYMENT_CONFIRMED" -> MoneyIn
                key.startsWith("SHARE") -> Share
                key.startsWith("PACKAGE") -> Package
                key.startsWith("SOS") -> Safety
                else -> Other
            }
        }
    }
}
