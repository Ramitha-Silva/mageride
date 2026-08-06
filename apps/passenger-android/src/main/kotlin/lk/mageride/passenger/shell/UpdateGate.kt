package lk.mageride.passenger.shell

import androidx.compose.foundation.layout.size
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Upgrade
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import lk.mageride.passenger.R
import lk.mageride.passenger.ui.component.MageRideCta
import lk.mageride.passenger.ui.theme.ControlTokens
import lk.mageride.shared.data.api.UpgradeRequiredSignal

/**
 * SCR-PA-031's **mandatory** half — D-31's minimum-version gate as a wall.
 *
 * The gateway runs the check at the edge on **every** route, so any of the 176 operations can
 * answer `426`. C013 therefore raises it once on `MageRideApiSignals.upgradeRequired` instead of
 * making 176 call sites handle it, and the shell is the single subscriber — see that class's KDoc.
 *
 * **The two cases are two different controls here, and the wireframe is explicit about it**:
 * *"mandatory = non-dismissible dialog → Store; soft = dismissible banner/snackbar"*. So this
 * composable draws nothing at all for a soft signal — `PassengerShell` shows that one as a
 * snackbar with an `Update` action, which is dismissible by construction and does not block a
 * passenger mid-ride over a release that did not require it. (The driver app answers both with one
 * dialog; D2' §SCR-DA-035 does not draw the split.)
 *
 * A mandatory gate is not dismissible and has no "Later": every subsequent call would answer `426`
 * anyway, so a dismissible wall would put the passenger in an app where nothing works and nothing
 * explains why.
 *
 * The layout is the wireframe's `.dialog .box`: the `⬆️` mark, *"Update required"*, the sentence
 * under it, and a **full-width** `Update now` bar — which is the §0.2 CTA token rather than M3's
 * default text button, because that is what the frame draws.
 *
 * @param signal The 426 payload, or `null` when no gate is in force.
 * @param onUpdate Opens [UpgradeRequiredSignal.updateUrl] in the store.
 */
@Composable
internal fun UpdateGate(signal: UpgradeRequiredSignal?, onUpdate: (String?) -> Unit) {
    if (signal == null || !signal.isMandatory) return

    AlertDialog(
        // A mandatory gate ignores the scrim tap and the back button; `onDismissRequest` is the
        // only hook either of those has, so swallowing it here is what makes it non-dismissible.
        onDismissRequest = { },
        icon = {
            Icon(
                imageVector = Icons.Filled.Upgrade,
                contentDescription = null,
                modifier = Modifier.size(ControlTokens.DialogIcon),
            )
        },
        title = { Text(text = stringResource(R.string.update_required_title), textAlign = TextAlign.Center) },
        text = {
            Text(
                text = stringResource(R.string.update_mandatory_message),
                style = MaterialTheme.typography.bodyMedium,
                textAlign = TextAlign.Center,
            )
        },
        confirmButton = {
            MageRideCta(
                label = stringResource(R.string.update_action_now),
                onClick = { onUpdate(signal.updateUrl) },
            )
        },
    )
}
