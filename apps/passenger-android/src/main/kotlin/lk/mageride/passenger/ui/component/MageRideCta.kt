package lk.mageride.passenger.ui.component

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import lk.mageride.passenger.ui.theme.CtaTokens
import lk.mageride.passenger.ui.theme.MageRideTheme

/**
 * D2' §0.2's **CTA token** — the one primary button every screen group uses.
 *
 * > *"CTA token (replaces NY `PrimaryButton`): height `56dp`, radius `sm 8`, `primary` bg,
 * > `onPrimary` label `titleMedium`, optional 20dp leading/trailing icon, ripple/`.buttonStyle`
 * > press state, inline lottie/`ProgressView` loader."*
 *
 * The shell declared the token and deliberately shipped no composable for it; this is the one, and
 * C078–C084 use it rather than a `Button`. Every full-width orange bar in
 * `passenger_android.html` is this.
 *
 * [loading] is not the same as `!enabled`. A CTA mid-request stays visually primary and swaps its
 * label for the inline loader — greying it out reads as "you cannot do this", which is wrong when
 * the answer is "you already did".
 *
 * @param label The button text. Always from a string resource — never a literal.
 * @param leading Optional 20 dp icon before the label.
 * @param trailing Optional 20 dp icon after it.
 */
// Seven parameters is the shape of a Compose slot API, not a design smell: `modifier`, `enabled`
// and the two content slots are conventions every M3 component carries, and folding them into a
// holder type would make this the one button in the app that does not read like `Button`.
@Suppress("LongParameterList")
@Composable
internal fun MageRideCta(
    label: String,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
    loading: Boolean = false,
    leading: (@Composable () -> Unit)? = null,
    trailing: (@Composable () -> Unit)? = null,
) {
    Button(
        onClick = onClick,
        modifier = modifier
            .fillMaxWidth()
            .height(CtaTokens.Height),
        // A CTA that is loading must not fire twice; it stays enabled-looking and unclickable.
        enabled = enabled && !loading,
        shape = RoundedCornerShape(CtaTokens.Radius),
        colors = ButtonDefaults.buttonColors(
            containerColor = MaterialTheme.colorScheme.primary,
            contentColor = MaterialTheme.colorScheme.onPrimary,
            // `enabled = false` while loading would otherwise grey the bar out — see the KDoc.
            disabledContainerColor = if (loading) {
                MaterialTheme.colorScheme.primary
            } else {
                MaterialTheme.colorScheme.surfaceVariant
            },
            disabledContentColor = if (loading) {
                MaterialTheme.colorScheme.onPrimary
            } else {
                MaterialTheme.colorScheme.outlineVariant
            },
        ),
    ) {
        if (loading) {
            CircularProgressIndicator(
                modifier = Modifier.size(CtaTokens.IconSize),
                color = MaterialTheme.colorScheme.onPrimary,
                strokeWidth = 2.dp,
            )
        } else {
            Row(
                horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                leading?.invoke()
                Text(text = label, style = MaterialTheme.typography.titleMedium)
                trailing?.invoke()
            }
        }
    }
}

/**
 * The wireframe's `textlink` — a bare primary-coloured action.
 *
 * SCR-PA-002's *"Skip"*, SCR-PA-003's *"Resend (54s)"* and SCR-PA-005's *"Not now"* are all this.
 * A `TextButton` rather than clickable `Text` so it carries M3's own touch target and ripple; a
 * 13 px link with a 20 px hit box is the control everyone misses on a moving bus.
 */
@Composable
internal fun MageRideTextLink(
    label: String,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
) {
    TextButton(onClick = onClick, modifier = modifier, enabled = enabled) {
        Text(
            text = label,
            style = MaterialTheme.typography.bodyMedium,
            color = if (enabled) {
                MaterialTheme.colorScheme.primary
            } else {
                MaterialTheme.colorScheme.outlineVariant
            },
        )
    }
}
