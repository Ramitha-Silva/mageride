package lk.mageride.passenger.ride

import android.content.ActivityNotFoundException
import android.content.Intent
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.AccountBalance
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.PhotoCamera
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.core.net.toUri
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import lk.mageride.passenger.R
import lk.mageride.passenger.ui.MoneyFormat
import lk.mageride.passenger.ui.component.InlineError
import lk.mageride.passenger.ui.component.MageRideCta
import lk.mageride.passenger.ui.theme.ControlTokens
import lk.mageride.passenger.ui.theme.MageRideTheme
import lk.mageride.shared.data.models.fare.PaymentMethod

/**
 * SCR-PA-017 — paying, and waiting for the driver to say it arrived.
 *
 * **This app renders no MageRide QR** (AL-22). The passenger *scans the driver's* printed or
 * on-screen LankaQR, or opens their own bank app through a LankaQR deep link (AL-15). Both move
 * money bank to bank, which is exactly why **no callback ever reaches fare-svc** and why settlement
 * is AL-47's attestation: *"I've paid"* → the driver confirms → `DriverConfirmedQR`, terminal.
 *
 * **Unconfirmed past five minutes offers help rather than a longer spinner.** The driver is
 * re-pushed at +5 min; if nothing comes back the route is Support → the Finance dispute queue, and
 * there is no money for the platform to reverse either way.
 *
 * @param onSupport The *"Get help"* link — SCR-PA-030.
 */
@Composable
@Suppress("LongMethod") // The wireframe's states: scan, deep link, claim, wait, confirmed.
internal fun PayFareScreen(
    onBack: () -> Unit,
    onSupport: () -> Unit,
    onSettled: () -> Unit,
    model: PayFareViewModel,
) {
    val state by model.state.collectAsStateWithLifecycle()
    val context = LocalContext.current

    androidx.compose.runtime.LaunchedEffect(state.confirmed) { if (state.confirmed) onSettled() }

    Column(modifier = Modifier.fillMaxSize()) {
        Row(modifier = Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            IconButton(onClick = onBack) {
                Icon(
                    imageVector = Icons.AutoMirrored.Filled.ArrowBack,
                    contentDescription = stringResource(R.string.action_back),
                    tint = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
            Text(
                text = stringResource(R.string.pay_title),
                style = MaterialTheme.typography.titleLarge,
                color = MaterialTheme.colorScheme.onSurface,
            )
        }

        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(MageRideTheme.spacing.md),
            verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.sm),
            horizontalAlignment = Alignment.CenterHorizontally,
        ) {
            Text(
                text = stringResource(R.string.pay_amount_due),
                style = MaterialTheme.typography.labelMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            Text(
                // One number. There is no surcharge line on this screen because no surviving rail
                // has one (AL-57) — see `PaymentRails`.
                text = state.amountMinor?.let(MoneyFormat::rupees) ?: MoneyFormat.EMPTY,
                style = MaterialTheme.typography.displaySmall,
                color = MaterialTheme.colorScheme.onSurface,
            )

            when {
                state.confirmed -> Confirmed()

                state.claimed -> Waiting(state = state, onSupport = onSupport)

                state.method == PaymentMethod.SCAN_DRIVER_QR -> DriverQrActions(
                    state = state,
                    onScan = model::openScanner,
                    onDeepLink = {
                        // AL-15 — the "Pay" deep link opens the passenger's bank app pre-filled.
                        // A handset with no compatible app falls back to the camera, which is the
                        // scannable path the same decision made the fallback rather than the
                        // default.
                        runCatching {
                            context.startActivity(Intent(Intent.ACTION_VIEW, LANKAQR_SCHEME.toUri()))
                        }.onFailure { failure ->
                            if (failure is ActivityNotFoundException) model.openScanner()
                        }
                    },
                    onClaim = { model.claimPaid() },
                )

                else -> Row(
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
                ) {
                    if (state.busy) CircularProgressIndicator(modifier = Modifier.size(ControlTokens.ChipIcon))
                    Text(
                        text = stringResource(R.string.pay_settling),
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }

            state.error?.let { InlineError(message = stringResource(it)) }

            if (!state.confirmed) {
                OutlinedButton(onClick = model::switchToCash, modifier = Modifier.fillMaxWidth()) {
                    // US-8.15 — a rail that will not settle becomes cash, without losing history.
                    Text(stringResource(R.string.pay_switch_to_cash))
                }
            }
        }
    }

    if (state.scanning) {
        DriverQrScanner(onScanned = model::onQrScanned, onDismiss = model::closeScanner)
    }
}

/** The two ways to pay a driver's own QR, plus AL-47's claim. */
@Composable
private fun DriverQrActions(state: PayFareState, onScan: () -> Unit, onDeepLink: () -> Unit, onClaim: () -> Unit) {
    Text(
        text = stringResource(R.string.pay_scan_explainer),
        style = MaterialTheme.typography.bodyMedium,
        color = MaterialTheme.colorScheme.onSurfaceVariant,
        textAlign = TextAlign.Center,
    )

    MageRideCta(
        label = stringResource(R.string.pay_scan),
        onClick = onScan,
        leading = {
            Icon(
                imageVector = Icons.Filled.PhotoCamera,
                contentDescription = null,
                modifier = Modifier.size(ControlTokens.RowIcon),
            )
        },
    )

    OutlinedButton(onClick = onDeepLink, modifier = Modifier.fillMaxWidth()) {
        Icon(
            imageVector = Icons.Filled.AccountBalance,
            contentDescription = null,
            modifier = Modifier.size(ControlTokens.ChipIcon),
        )
        Text(
            text = stringResource(R.string.pay_bank_app),
            modifier = Modifier.padding(start = MageRideTheme.spacing.xxs),
        )
    }

    // AL-47's claim. Offered whether or not the scan went through this app, because the passenger
    // may well have paid from their bank app instead — the platform saw neither.
    OutlinedButton(onClick = onClaim, modifier = Modifier.fillMaxWidth(), enabled = !state.busy) {
        Text(stringResource(R.string.pay_ive_paid))
    }
}

/** *"Waiting for driver to confirm…"*, and Support once the nudge window has passed. */
@Composable
private fun Waiting(state: PayFareState, onSupport: () -> Unit) {
    Row(
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
    ) {
        CircularProgressIndicator(modifier = Modifier.size(ControlTokens.ChipIcon))
        Text(
            text = stringResource(R.string.pay_waiting, state.waiting),
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }

    if (state.offerSupport) {
        TextButton(onClick = onSupport) { Text(stringResource(R.string.pay_get_help)) }
    }
}

/** `Confirmed ✓` — `DriverConfirmedQR`, which is terminal and releases the earning (R-05). */
@Composable
private fun Confirmed() {
    Row(
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
    ) {
        Icon(
            imageVector = Icons.Filled.CheckCircle,
            contentDescription = null,
            modifier = Modifier.size(ControlTokens.RowIcon),
            tint = MageRideTheme.status.success,
        )
        Text(
            text = stringResource(R.string.pay_confirmed),
            style = MaterialTheme.typography.titleMedium,
            color = MageRideTheme.status.success,
        )
    }
}

/**
 * AL-15's deep link.
 *
 * The LankaQR scheme with no payload: the passenger's bank app opens and they complete the payment
 * there against the driver's QR. This app has no merchant reference to pre-fill — the driver's is
 * on their own payout profile, not in this client — which is exactly the AL-59 arrangement.
 */
private const val LANKAQR_SCHEME = "lankaqr://pay"
