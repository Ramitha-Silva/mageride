package lk.mageride.driver.ui.component

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
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import lk.mageride.driver.ui.theme.CtaTokens
import lk.mageride.driver.ui.theme.MageRideTheme

/**
 * D2' §0.2's **CTA token** — the one primary button every screen group uses.
 *
 * > *"CTA token (replaces NY `PrimaryButton`): height `56dp`, radius `sm 8`, `primary` bg,
 * > `onPrimary` label `titleMedium`, optional 20dp leading/trailing icon, ripple/`.buttonStyle`
 * > press state, inline lottie/`ProgressView` loader."*
 *
 * Screens call this rather than `Button`: a wireframe's full-width orange bar is this composable
 * in all five of C068's screens, four of C069's and the rest, and one definition is what keeps
 * them the same height.
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

/** The tonal variant §0.2's `.cta.tonal` uses for a secondary action on the same screen. */
@Composable
internal fun MageRideCtaTonal(
    label: String,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
) {
    Button(
        onClick = onClick,
        modifier = modifier
            .fillMaxWidth()
            .height(CtaTokens.Height),
        enabled = enabled,
        shape = RoundedCornerShape(CtaTokens.Radius),
        colors = ButtonDefaults.buttonColors(
            containerColor = MaterialTheme.colorScheme.primaryContainer,
            contentColor = MaterialTheme.colorScheme.onPrimaryContainer,
            disabledContainerColor = MaterialTheme.colorScheme.surfaceVariant,
            disabledContentColor = MaterialTheme.colorScheme.outlineVariant,
        ),
    ) {
        Text(text = label, style = MaterialTheme.typography.titleMedium)
    }
}
