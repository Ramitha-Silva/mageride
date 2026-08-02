package lk.mageride.driver.shell

import androidx.compose.material3.AlertDialog
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.res.stringResource
import lk.mageride.driver.R
import lk.mageride.shared.data.api.UpgradeRequiredSignal

/**
 * D-31's minimum-version gate, as the app sees it.
 *
 * The gateway runs the check at the edge on **every** route, so any of the 176 operations can
 * answer `426`. C013 therefore raises it once on `MageRideApiSignals.upgradeRequired` instead of
 * making 176 call sites handle it, and this is the single subscriber — see that class's KDoc.
 *
 * **`isMandatory` decides whether there is a way out.** A mandatory gate is not dismissible and
 * has no "Later": every subsequent call would answer 426 anyway, so a dismissible wall would put
 * the driver in an app where nothing works and nothing explains why. An optional one is a nudge
 * and must be dismissible, or a driver mid-shift is blocked by a release that did not require it.
 *
 * @param signal The 426 payload, or `null` when no gate is in force.
 * @param onUpdate Opens [UpgradeRequiredSignal.updateUrl] in the store.
 * @param onDismiss Only reachable when the gate is optional.
 */
@Composable
internal fun UpdateGate(signal: UpgradeRequiredSignal?, onUpdate: (String?) -> Unit, onDismiss: () -> Unit) {
    if (signal == null) return

    AlertDialog(
        // A mandatory gate ignores the scrim tap and the back button; `onDismissRequest` is the
        // only hook either of those has, so swallowing it here is what makes it non-dismissible.
        onDismissRequest = { if (!signal.isMandatory) onDismiss() },
        title = { Text(stringResource(R.string.update_required_title)) },
        text = {
            Text(
                text = if (signal.isMandatory) {
                    stringResource(R.string.update_mandatory_message)
                } else {
                    stringResource(R.string.update_optional_message)
                },
                style = MaterialTheme.typography.bodyMedium,
            )
        },
        confirmButton = {
            TextButton(onClick = { onUpdate(signal.updateUrl) }) {
                Text(stringResource(R.string.update_action_now))
            }
        },
        dismissButton = {
            if (!signal.isMandatory) {
                TextButton(onClick = onDismiss) {
                    Text(stringResource(R.string.update_action_later))
                }
            }
        },
    )
}
