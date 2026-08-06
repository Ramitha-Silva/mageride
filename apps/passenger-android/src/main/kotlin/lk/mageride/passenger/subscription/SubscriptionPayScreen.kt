package lk.mageride.passenger.subscription

import android.content.ActivityNotFoundException
import android.content.Intent
import android.graphics.BitmapFactory
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.AttachFile
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.RadioButton
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.core.net.toUri
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import lk.mageride.passenger.R
import lk.mageride.passenger.ui.DateFormat
import lk.mageride.passenger.ui.MoneyFormat
import lk.mageride.passenger.ui.component.InlineError
import lk.mageride.passenger.ui.component.MageRideCta
import lk.mageride.passenger.ui.component.SectionLabel
import lk.mageride.passenger.ui.theme.ControlTokens
import lk.mageride.passenger.ui.theme.MageRideTheme
import lk.mageride.shared.data.models.subscription.PayTo
import lk.mageride.shared.data.models.subscription.SubscriptionPayMethod
import lk.mageride.shared.domain.fare.FarePaymentAction
import lk.mageride.shared.domain.subscription.ModeBPaymentStep

/**
 * SCR-PA-025a — "Pay subscription".
 *
 * The wireframe: a filled header carrying the vehicle, the period and *"to ABC Fleet (Pvt) Ltd"*
 * with the amount on the right; **Choose payment mode**; a card per rail; and a
 * **Confirm & pay Rs 6,000** CTA.
 *
 * **Three deliberate departures from that drawing, all of them AL-49/AL-59's.**
 *
 * 1. **No OnePay row, and no `+5 %` anywhere.** A subscription is paid to the *fleet owner*, and
 *    OnePay has one merchant account per merchant — the money would land in MageRide's. See
 *    [SubscriptionRails]. **Cash** takes the vacant row, which is what D2' §16e and US-23.6 ask
 *    for. The wireframe needs a micro-change-set; recorded in the C082 handoff.
 * 2. **The owner's details appear after Confirm, not before.** `payTo` is minted by
 *    `POST …/pay` and served only from a verified payout profile, so the chooser cannot print an
 *    account number it has not been given.
 * 3. **The attach row is on the transfer card, and stays live afterwards.** A passenger who
 *    already has the slip attaches it first and one tap does both; one who needs the account
 *    number first gets it from stage two and attaches then.
 */
@Composable
@Suppress("LongMethod") // The wireframe's layout tree: header, chooser, four rails, CTA.
internal fun SubscriptionPayScreen(onBack: () -> Unit, onDone: () -> Unit, model: SubscriptionPayViewModel) {
    val state by model.state.collectAsStateWithLifecycle()
    val qrImage by model.qrImage.collectAsStateWithLifecycle()
    val context = LocalContext.current
    val scope = rememberCoroutineScope()

    // `GetContent` rather than `PickVisualMedia`: the photo picker is API 33+ with a Play Services
    // back-port, and this app's floor is Android 8.0 (NFR-22). A transfer slip is also routinely a
    // PDF from a banking app rather than an image in the gallery.
    val picker = rememberLauncherForActivityResult(ActivityResultContracts.GetContent()) { uri ->
        if (uri == null) return@rememberLauncherForActivityResult
        scope.launch {
            val bytes = withContext(Dispatchers.IO) {
                runCatching { context.contentResolver.openInputStream(uri)?.use { it.readBytes() } }.getOrNull()
            }
            if (bytes != null) model.attachSlip(uri.lastPathSegment ?: SLIP_NAME, bytes)
        }
    }

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
                text = stringResource(R.string.subscription_pay_title),
                style = MaterialTheme.typography.titleLarge,
                color = MaterialTheme.colorScheme.onSurface,
            )
        }

        Column(
            modifier = Modifier
                .fillMaxSize()
                .verticalScroll(rememberScrollState())
                .padding(MageRideTheme.spacing.md),
            verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.sm),
        ) {
            SummaryCard(state)

            if (state.payment == null) {
                SectionLabel(text = stringResource(R.string.subscription_pay_choose))

                SubscriptionRails.METHODS.forEach { method ->
                    RailCard(
                        method = method,
                        selected = method == state.method,
                        slipName = state.slipName,
                        onSelect = { model.choose(method) },
                        onAttach = { picker.launch(PICKER_FILTER) },
                    )
                }
            } else {
                PaymentStep(
                    state = state,
                    qrImage = qrImage,
                    onOpenBankApp = { url -> context.openBankApp(url) },
                    onAttach = { picker.launch(PICKER_FILTER) },
                )
            }

            state.error?.let { InlineError(message = stringResource(it)) }

            when {
                state.settled -> MageRideCta(label = stringResource(R.string.action_done), onClick = onDone)

                state.payment == null -> MageRideCta(
                    label = state.amount
                        ?.let { stringResource(R.string.subscription_pay_confirm, MoneyFormat.rupees(it)) }
                        ?: stringResource(R.string.subscription_pay_confirm_unknown),
                    onClick = model::confirm,
                    enabled = state.canConfirm,
                    loading = state.submitting,
                )

                // Initiated and not settled: cash waiting on the owner, or a transfer whose slip
                // has not been attached yet. Neither has a button that finishes it from here.
                else -> MageRideCta(
                    label = stringResource(R.string.action_done),
                    onClick = onDone,
                    enabled = !state.submitting,
                )
            }
        }
    }
}

/** The wireframe's filled header: vehicle, period, payee, amount. */
@Composable
private fun SummaryCard(state: SubscriptionPayState) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .background(MaterialTheme.colorScheme.surfaceVariant, RoundedCornerShape(MageRideTheme.radius.md))
            .padding(MageRideTheme.spacing.sm),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Column(modifier = Modifier.weight(1f)) {
            Text(
                text = state.subscription?.vehicleId.orEmpty(),
                style = MaterialTheme.typography.titleSmall,
                color = MaterialTheme.colorScheme.onSurface,
            )
            Text(
                text = listOfNotNull(
                    state.payment?.periodMonth?.let(DateFormat::monthYear),
                    state.subscription?.nextDue
                        ?.let { stringResource(R.string.subscriptions_next_due, DateFormat.dayMonth(it)) },
                    // The payee, straight from `payTo` — the only name this screen ever shows,
                    // and only once a verified profile has supplied one (AL-49).
                    state.payment?.payTo?.accountHolderName
                        ?.let { stringResource(R.string.subscription_pay_to, it) },
                ).joinToString(SEPARATOR),
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
        Text(
            text = state.amount?.let(MoneyFormat::rupees) ?: MoneyFormat.EMPTY,
            style = MaterialTheme.typography.headlineSmall,
            color = MaterialTheme.colorScheme.onSurface,
        )
    }
}

/** One rail. No surcharge line on any of them — see [SubscriptionRails]. */
@Composable
private fun RailCard(
    method: SubscriptionPayMethod,
    selected: Boolean,
    slipName: String?,
    onSelect: () -> Unit,
    onAttach: () -> Unit,
) {
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .border(
                width = if (selected) ControlTokens.BorderSelected else ControlTokens.Border,
                color = if (selected) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.outline,
                shape = RoundedCornerShape(MageRideTheme.radius.md),
            )
            .clickable(onClick = onSelect)
            .padding(MageRideTheme.spacing.sm),
        verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
    ) {
        Row(modifier = Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            RadioButton(selected = selected, onClick = onSelect)
            Column(modifier = Modifier.weight(1f)) {
                Text(
                    text = stringResource(SubscriptionRails.label(method)),
                    style = MaterialTheme.typography.bodyLarge,
                    color = MaterialTheme.colorScheme.onSurface,
                )
                Text(
                    text = stringResource(SubscriptionRails.caption(method)),
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }

        // US-23.4 — the transfer rail cannot be confirmed without a screenshot of the slip.
        if (method == SubscriptionPayMethod.ONLINE_TRANSFER && selected) {
            AttachSlipButton(slipName = slipName, onAttach = onAttach)
        }
    }
}

/** The wireframe's `📎 Attach transfer screenshot`, and what it says once one is attached. */
@Composable
private fun AttachSlipButton(slipName: String?, onAttach: () -> Unit) {
    OutlinedButton(onClick = onAttach, modifier = Modifier.fillMaxWidth()) {
        Icon(
            imageVector = Icons.Filled.AttachFile,
            contentDescription = null,
            modifier = Modifier.size(ControlTokens.RowIcon),
        )
        Text(
            text = slipName?.let { stringResource(R.string.subscription_slip_attached, it) }
                ?: stringResource(R.string.subscription_attach_slip),
            modifier = Modifier.padding(start = MageRideTheme.spacing.xxs),
        )
    }
}

/**
 * Stage two — what BR-23.10 says to do with the rail that was chosen.
 *
 * The branch is `:shared`'s `ModeBPaymentStep` and not a second `when` over the method: which
 * hand-off a rail resolves to depends on what the *server* sent back (`redirectUrl`, `qrPayload`,
 * `payTo`), and that decision lives in `ModeBPaymentRules` where the driver-side fare screen's
 * equivalent also lives.
 */
@Composable
private fun PaymentStep(
    state: SubscriptionPayState,
    qrImage: ByteArray?,
    onOpenBankApp: (String) -> Unit,
    onAttach: () -> Unit,
) {
    Column(
        modifier = Modifier.fillMaxWidth(),
        verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
    ) {
        when (val step = state.step) {
            is ModeBPaymentStep.GatewayHandoff -> when (val action = step.action) {
                is FarePaymentAction.OpenBankApp -> OutlinedButton(
                    onClick = { onOpenBankApp(action.url) },
                    modifier = Modifier.fillMaxWidth(),
                ) {
                    Text(stringResource(R.string.subscription_open_bank_app))
                }

                // AL-15's fallback: no bank app took the link, so the payload is what is left.
                // It is the OWNER's LankaQR string and is shown as text rather than re-encoded —
                // this app renders no QR of its own (AL-22).
                is FarePaymentAction.ShowLankaQrFallback -> StepNote(
                    title = stringResource(R.string.subscription_lankaqr_payload),
                    body = action.payload,
                )

                else -> StepNote(
                    title = stringResource(R.string.subscription_unavailable),
                    body = stringResource(R.string.subscription_unavailable_body),
                )
            }

            is ModeBPaymentStep.ShowOwnerLankaQr -> OwnerQr(qrImage)

            is ModeBPaymentStep.TransferAndUploadSlip -> {
                BankDetails(step.payTo)
                if (state.awaitingSlip) {
                    AttachSlipButton(slipName = state.slipName, onAttach = onAttach)
                } else {
                    StepNote(
                        title = stringResource(R.string.subscription_transfer_pending_title),
                        body = stringResource(R.string.subscription_pending_body),
                    )
                }
            }

            // US-23.6 — only the owner can mark cash received, in the web portal. Nothing on this
            // handset finishes it, and saying so beats a spinner that never resolves.
            ModeBPaymentStep.HandToCollector -> StepNote(
                title = stringResource(R.string.subscription_cash_title),
                body = stringResource(R.string.subscription_cash_body),
            )

            ModeBPaymentStep.Unavailable, null -> StepNote(
                title = stringResource(R.string.subscription_unavailable),
                body = stringResource(R.string.subscription_unavailable_body),
            )
        }
    }
}

/** AL-49's verified bank block, exactly as `payTo` gave it. */
@Composable
private fun BankDetails(payTo: PayTo) {
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .background(MaterialTheme.colorScheme.surfaceVariant, RoundedCornerShape(MageRideTheme.radius.md))
            .padding(MageRideTheme.spacing.sm),
        verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xxs),
    ) {
        SectionLabel(text = stringResource(R.string.subscription_transfer_to))
        DetailRow(stringResource(R.string.subscription_bank), payTo.bank)
        DetailRow(stringResource(R.string.subscription_branch), payTo.branch)
        DetailRow(stringResource(R.string.subscription_account_no), payTo.accountNo)
        DetailRow(stringResource(R.string.subscription_account_holder), payTo.accountHolderName)
    }
}

/** One `label · value` line of the bank block. Absent when the profile carried no value. */
@Composable
private fun DetailRow(label: String, value: String?) {
    if (value.isNullOrBlank()) return
    Row(modifier = Modifier.fillMaxWidth()) {
        Text(
            text = label,
            modifier = Modifier.weight(1f),
            style = MaterialTheme.typography.labelSmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
        Text(
            text = value,
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurface,
        )
    }
}

/**
 * The owner's own bank-app LankaQR, for the passenger to scan with theirs.
 *
 * Decoded here rather than in the view model so no Android graphics type reaches a class that has
 * to run on this Linux build host. `BitmapFactory` answers `null` for anything it cannot decode —
 * including in a local unit test, where the framework class is a stub — and the caption below is
 * what that produces rather than a crash.
 */
@Composable
private fun OwnerQr(bytes: ByteArray?) {
    val bitmap = remember(bytes) {
        bytes?.let { runCatching { BitmapFactory.decodeByteArray(it, 0, it.size) }.getOrNull() }
    }

    Column(
        modifier = Modifier.fillMaxWidth(),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
    ) {
        if (bitmap != null) {
            Image(
                bitmap = bitmap.asImageBitmap(),
                contentDescription = stringResource(R.string.subscription_owner_qr),
                modifier = Modifier.size(ControlTokens.Viewfinder),
            )
        }
        Text(
            text = stringResource(
                if (bitmap != null) R.string.subscription_owner_qr_hint else R.string.subscription_owner_qr_missing,
            ),
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }
}

/** A titled note — the cash, pending and unavailable states. */
@Composable
private fun StepNote(title: String, body: String) {
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .background(MaterialTheme.colorScheme.surfaceVariant, RoundedCornerShape(MageRideTheme.radius.md))
            .padding(MageRideTheme.spacing.sm),
        verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xxs),
    ) {
        Text(
            text = title,
            style = MaterialTheme.typography.titleSmall,
            color = MaterialTheme.colorScheme.onSurface,
        )
        Text(
            text = body,
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }
}

/**
 * Opens the LankaQR deep link in the passenger's own bank app (AL-15).
 *
 * A handset with no compatible app is not an error worth a dialog: the transfer details and the
 * owner's QR are both still on this screen, and the passenger picks another rail.
 */
private fun android.content.Context.openBankApp(url: String) {
    runCatching { startActivity(Intent(Intent.ACTION_VIEW, url.toUri())) }
        .onFailure { if (it !is ActivityNotFoundException) throw it }
}

/** What the slip picker asks the system for. */
private const val PICKER_FILTER = "*/*"

/** The name a slip is sent under when the provider exposed none. */
private const val SLIP_NAME = "transfer-slip"

/** The wireframe's `·` between the period, the due date and the payee. Punctuation, not copy. */
private const val SEPARATOR = " · "
