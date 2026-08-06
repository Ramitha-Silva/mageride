package lk.mageride.passenger.shell

import androidx.compose.material3.AlertDialog
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.res.stringResource
import lk.mageride.passenger.R
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
        title = { Text(stringResource(R.string.update_required_title)) },
        text = {
            Text(
                text = stringResource(R.string.update_mandatory_message),
                style = MaterialTheme.typography.bodyMedium,
            )
        },
        confirmButton = {
            TextButton(onClick = { onUpdate(signal.updateUrl) }) {
                Text(stringResource(R.string.update_action_now))
            }
        },
    )
}
