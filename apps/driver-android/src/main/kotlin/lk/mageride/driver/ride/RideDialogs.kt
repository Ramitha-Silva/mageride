package lk.mageride.driver.ride

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import lk.mageride.driver.R
import lk.mageride.driver.ui.component.MageRideCta
import lk.mageride.driver.ui.component.MageRideCtaTonal
import lk.mageride.driver.ui.component.OtpEntry
import lk.mageride.driver.ui.theme.MageRideTheme
import lk.mageride.shared.data.models.CallType

/**
 * SCR-DA-015's three sheets, hosted together because at most one is ever up.
 *
 * The OTP entry (P-07), AL-48's call-type chooser and AL-47's QR attestation are three different
 * conversations with three different consequences, and putting them in one file keeps
 * [ActiveRideScreen] the wireframe's layout tree rather than the wireframe plus its modals.
 */
@Composable
internal fun RideDialogs(state: ActiveRideState, viewModel: ActiveRideViewModel) {
    when (state.sheet) {
        RideSheet.START_OTP -> StartOtpDialog(
            onVerify = viewModel::startRide,
            onDismiss = { viewModel.open(sheet = null) },
        )

        RideSheet.CALL_TYPE -> CallTypeSheet(
            onChoose = viewModel::call,
            onDismiss = { viewModel.open(sheet = null) },
        )

        RideSheet.QR_CONFIRM -> QrConfirmSheet(
            onAnswer = viewModel::confirmQrPayment,
            onDismiss = { viewModel.open(sheet = null) },
        )

        null -> Unit
    }
}

/**
 * The rider's four-digit start OTP (P-07).
 *
 * **Five attempts and no more**: after that the endpoint answers `423 otp-locked` and the handoff
 * needs support intervention. The count is the server's — nothing here tracks it, because a client
 * that counted would be a second answer to a question `rides` already has.
 */
@Composable
private fun StartOtpDialog(onVerify: (String) -> Unit, onDismiss: () -> Unit) {
    var otp by remember { mutableStateOf("") }

    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(text = stringResource(R.string.ride_otp_title)) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.sm)) {
                Text(
                    text = stringResource(R.string.ride_otp_body),
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                OtpEntry(value = otp, onValueChange = { otp = it }, length = OTP_LENGTH)
            }
        },
        confirmButton = {
            TextButton(onClick = { onVerify(otp) }, enabled = otp.length == OTP_LENGTH) {
                Text(text = stringResource(R.string.ride_otp_verify))
            }
        },
        dismissButton = {
            TextButton(onClick = onDismiss) { Text(text = stringResource(R.string.action_dismiss)) }
        },
    )
}

/**
 * **AL-48's call-type chooser** — and there are two options, not three.
 *
 * *"Free call"* is the in-app WebRTC session; *"Normal call"* is a **direct cellular dial** of the
 * counterparty's real number, which the ride carries from `Accepted` onward. The masked-number
 * bridge and D-25's masked-SMS relay were both withdrawn, so a VoIP failure falls back to the same
 * direct dial rather than to a relay (US-26.4, BR-30.3).
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun CallTypeSheet(onChoose: (CallType) -> Unit, onDismiss: () -> Unit) {
    ModalBottomSheet(onDismissRequest = onDismiss, sheetState = rememberModalBottomSheetState()) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .padding(MageRideTheme.spacing.md),
            verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.sm),
        ) {
            Text(
                text = stringResource(R.string.ride_call_title),
                style = MaterialTheme.typography.titleMedium,
                color = MaterialTheme.colorScheme.onSurface,
            )
            MageRideCta(
                label = stringResource(R.string.ride_call_free),
                onClick = { onChoose(CallType.FREE_VOIP) },
            )
            MageRideCtaTonal(
                label = stringResource(R.string.ride_call_normal),
                onClick = { onChoose(CallType.DIRECT_DIAL) },
            )
        }
    }
}

/**
 * **AL-47 · *"QR payment received?"*** (US-26.1).
 *
 * The driver's answer **is** the settlement. *"Yes"* posts `POST /v1/fare/pay/driver-qr/confirm`
 * and the payment reaches `DriverConfirmedQR`, which is terminal, settles like cash and releases
 * the earning (R-05). *"Not received"* opens a Support → Finance ticket and moves no money —
 * MageRide took no commission on this path and holds none of it, so there is nothing to reverse.
 *
 * It is not dismissible by accident: the sheet is the last step of the ride, and a driver who
 * swiped it away would have a completed trip with no settled payment and no way back to it.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun QrConfirmSheet(onAnswer: (Boolean) -> Unit, onDismiss: () -> Unit) {
    ModalBottomSheet(onDismissRequest = onDismiss, sheetState = rememberModalBottomSheetState()) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .padding(MageRideTheme.spacing.md),
            verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.sm),
        ) {
            Text(
                text = stringResource(R.string.ride_qr_title),
                style = MaterialTheme.typography.titleMedium,
                color = MaterialTheme.colorScheme.onSurface,
            )
            Text(
                text = stringResource(R.string.ride_qr_body),
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            MageRideCta(label = stringResource(R.string.ride_qr_received), onClick = { onAnswer(true) })
            MageRideCtaTonal(label = stringResource(R.string.ride_qr_not_received), onClick = { onAnswer(false) })
        }
    }
}

/** Four digits (P-07). The package handoffs use the same length on the same entry. */
private const val OTP_LENGTH = 4
