package lk.mageride.passenger.ride

import android.content.Intent
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.KeyboardArrowRight
import androidx.compose.material.icons.filled.Call
import androidx.compose.material.icons.filled.Wifi
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.core.net.toUri
import lk.mageride.passenger.R
import lk.mageride.passenger.ui.theme.ControlTokens
import lk.mageride.passenger.ui.theme.MageRideTheme
import lk.mageride.shared.data.models.CallType

/**
 * SCR-PA-015a — *"How do you want to call?"*.
 *
 * **There is no masked option and no masking copy anywhere in this file.** AL-48 withdrew the
 * requirement outright: no proxy-DID product exists with +94 numbers, so *"Normal call"* is a
 * **direct cellular dial of the driver's real number**, revealed post-accept in
 * `RideDetail.counterpartyPhone`. The one privacy claim that survives is on the VoIP row, and it is
 * true — an in-app call involves no number at all.
 *
 * US-26.5's first-call tooltip is the transparency that replaced the masking: shown **once**, and
 * only before a direct dial, because warning about number visibility before a VoIP call would be
 * warning about something that is not happening.
 *
 * @param driverPhone The real number. `null` before acceptance, which greys the direct-dial row.
 * @param onFreeCall Opens SCR-PA-028's WebRTC session. VoIP failure prompts a direct dial (US-26.4).
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun CallChooserSheet(
    driverName: String?,
    driverPhone: String?,
    choice: CallChoice,
    onFreeCall: () -> Unit,
    onDialled: (CallType) -> Unit,
    onDismiss: () -> Unit,
) {
    val context = LocalContext.current
    val remembered = choice.remembered

    ModalBottomSheet(onDismissRequest = onDismiss, sheetState = rememberModalBottomSheetState()) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .padding(horizontal = MageRideTheme.spacing.md)
                .padding(bottom = MageRideTheme.spacing.lg),
            verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
        ) {
            Text(
                text = driverName ?: stringResource(R.string.call_driver),
                style = MaterialTheme.typography.titleMedium,
                color = MaterialTheme.colorScheme.onSurface,
            )
            Text(
                text = stringResource(R.string.call_how),
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )

            CallRow(
                icon = Icons.Filled.Wifi,
                title = stringResource(R.string.call_free),
                // The only honest "number hidden" on this screen: a VoIP call carries no number.
                caption = stringResource(R.string.call_free_caption),
                highlighted = remembered == CallType.FREE_VOIP,
                enabled = true,
                onClick = {
                    choice.remember(CallType.FREE_VOIP)
                    onDialled(CallType.FREE_VOIP)
                    onFreeCall()
                },
            )

            CallRow(
                icon = Icons.Filled.Call,
                title = stringResource(R.string.call_normal),
                caption = stringResource(R.string.call_normal_caption),
                highlighted = remembered == CallType.DIRECT_DIAL,
                enabled = driverPhone != null,
                onClick = {
                    val number = driverPhone ?: return@CallRow
                    choice.remember(CallType.DIRECT_DIAL)
                    onDialled(CallType.DIRECT_DIAL)
                    // ACTION_DIAL, not ACTION_CALL: the latter needs the CALL_PHONE permission and
                    // places the call without the passenger seeing the number. The dialler is one
                    // extra tap and is the transparency US-26.5 asks for, made concrete.
                    context.startActivity(Intent(Intent.ACTION_DIAL, "tel:$number".toUri()))
                    onDismiss()
                },
            )

            // US-26.5, once. AL-48 removed masking, so this is the disclosure that replaced it.
            if (driverPhone != null && choice.owesNumberNotice(CallType.DIRECT_DIAL)) {
                Text(
                    text = stringResource(R.string.call_number_notice),
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }

            TextButton(onClick = onDismiss, modifier = Modifier.fillMaxWidth()) {
                Text(stringResource(R.string.action_cancel))
            }
        }
    }
}

@Composable
private fun CallRow(
    icon: androidx.compose.ui.graphics.vector.ImageVector,
    title: String,
    caption: String,
    highlighted: Boolean,
    enabled: Boolean,
    onClick: () -> Unit,
) {
    val tint = if (enabled) MaterialTheme.colorScheme.onSurface else MaterialTheme.colorScheme.outline

    Row(
        modifier = Modifier
            .fillMaxWidth()
            .clickable(enabled = enabled, onClick = onClick)
            .padding(vertical = MageRideTheme.spacing.sm),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.sm),
    ) {
        Icon(
            imageVector = icon,
            contentDescription = null,
            modifier = Modifier.size(ControlTokens.RowIcon),
            tint = if (highlighted) MaterialTheme.colorScheme.primary else tint,
        )
        Column(modifier = Modifier.weight(1f)) {
            Text(text = title, style = MaterialTheme.typography.bodyLarge, color = tint)
            Text(
                text = caption,
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
        Icon(
            imageVector = Icons.AutoMirrored.Filled.KeyboardArrowRight,
            contentDescription = null,
            modifier = Modifier.size(ControlTokens.RowIcon),
            tint = tint,
        )
    }
}
