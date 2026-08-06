package lk.mageride.passenger.booking

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Info
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.SegmentedButton
import androidx.compose.material3.SegmentedButtonDefaults
import androidx.compose.material3.SingleChoiceSegmentedButtonRow
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import lk.mageride.passenger.R
import lk.mageride.passenger.onboarding.PhoneNumber
import lk.mageride.passenger.ui.MoneyFormat
import lk.mageride.passenger.ui.component.InlineError
import lk.mageride.passenger.ui.component.LabelledTextField
import lk.mageride.passenger.ui.component.MageRideCta
import lk.mageride.passenger.ui.component.PhoneNumberField
import lk.mageride.passenger.ui.component.SectionLabel
import lk.mageride.passenger.ui.theme.ControlTokens
import lk.mageride.passenger.ui.theme.MageRideTheme
import lk.mageride.shared.data.models.PackageSize
import lk.mageride.shared.data.models.ride.RidePaymentMethod
import org.koin.androidx.compose.koinViewModel

/**
 * SCR-PA-012 — sending a parcel (US-20.1/20.2/20.8, P-06).
 *
 * The wireframe, top to bottom: `‹ Send a package`, the **S/M/L** selector with the ⓘ hint that
 * *"updates per pick"*, a description, the recipient's name and number, **Pickup location** with
 * three methods, **Drop-off location** with four, `Payment: COD ▾`, and *"Get estimate & Book"*.
 *
 * **The two ends offer different methods and that is the fence.** Pickup: Search / Map / Paste
 * link. Drop-off: those plus **Request**, which asks the *recipient* to share where the parcel
 * should go. There is nobody at the pickup to ask — the sender is standing there.
 *
 * The **Map** method is a modal on this screen for the reason [MapPickSheet] gives: the wireframe
 * assigns no SCR-PA id to a map picker, so there is no route to navigate to.
 */
@Composable
@Suppress("LongMethod") // The wireframe's form, in the wireframe's order.
internal fun PackageBookingScreen(
    onBack: () -> Unit,
    onSearch: (PackageEnd) -> Unit,
    onBooked: (String) -> Unit,
    model: PackageBookingViewModel = koinViewModel(),
) {
    val state by model.state.collectAsStateWithLifecycle()
    var pasteFor by remember { mutableStateOf<PackageEnd?>(null) }
    var mapFor by remember { mutableStateOf<PackageEnd?>(null) }

    LaunchedEffect(state.booked) {
        // The OTP is shown once and never again (P-07), so the screen surfaces it before the
        // navigation rather than after — see PackageBookingViewModel.
        state.booked?.takeIf { state.pickupOtp == null }?.let {
            onBooked(it)
            model.onBookingConsumed()
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
                text = stringResource(R.string.package_title),
                style = MaterialTheme.typography.titleLarge,
                color = MaterialTheme.colorScheme.onSurface,
            )
        }

        Column(
            modifier = Modifier
                .fillMaxSize()
                .verticalScroll(rememberScrollState())
                .padding(horizontal = MageRideTheme.spacing.md),
            verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.sm),
        ) {
            SectionLabel(text = stringResource(R.string.package_size))
            SizeSelector(size = state.size, onSelect = model::setSize)

            // P-06's hint. Directly below the size box and swapping with it, which is what
            // change619 #2 added and what stops a sender picking S for a fridge.
            Row(
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
            ) {
                Icon(
                    imageVector = Icons.Filled.Info,
                    contentDescription = null,
                    modifier = Modifier.size(ControlTokens.ChipIcon),
                    tint = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                Text(
                    text = stringResource(state.sizeHint),
                    style = MaterialTheme.typography.labelMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }

            LabelledTextField(
                label = stringResource(R.string.package_description),
                value = state.description,
                onValueChange = model::onDescriptionChanged,
                placeholder = stringResource(R.string.package_description_hint),
                keyboardType = KeyboardType.Text,
                imeAction = ImeAction.Next,
            )

            LabelledTextField(
                label = stringResource(R.string.package_recipient_name),
                value = state.recipientName,
                onValueChange = model::onRecipientNameChanged,
                placeholder = stringResource(R.string.package_recipient_name_hint),
                keyboardType = KeyboardType.Text,
                imeAction = ImeAction.Next,
            )

            SectionLabel(text = stringResource(R.string.package_recipient_phone))
            PhoneNumberField(
                value = state.recipientPhone,
                onValueChange = model::onRecipientPhoneChanged,
                countryCode = PhoneNumber.COUNTRY_CODE,
                placeholder = PhoneNumber.PLACEHOLDER,
            )

            EndCapture(
                title = stringResource(R.string.package_pickup),
                end = PackageEnd.PICKUP,
                methods = PACKAGE_PICKUP_METHODS,
                state = state,
                model = model,
                onSearch = onSearch,
                onPickOnMap = { mapFor = it },
                onPaste = { pasteFor = it },
            )

            EndCapture(
                title = stringResource(R.string.package_dropoff),
                end = PackageEnd.DROPOFF,
                methods = ALL_METHODS,
                state = state,
                model = model,
                onSearch = onSearch,
                onPickOnMap = { mapFor = it },
                onPaste = { pasteFor = it },
            )

            SectionLabel(text = stringResource(R.string.package_payment))
            PaymentRow(method = state.paymentMethod, onChange = model::setPaymentMethod)

            state.estimateMinor?.let { amount ->
                Text(
                    text = stringResource(R.string.package_estimate, MoneyFormat.rupees(amount)),
                    style = MaterialTheme.typography.titleMedium,
                    color = MaterialTheme.colorScheme.onSurface,
                )
            }

            // P-07 — shown once, never returned again. The sender gives it to the driver.
            state.pickupOtp?.let { otp ->
                Text(
                    text = stringResource(R.string.package_pickup_otp, otp),
                    style = MaterialTheme.typography.titleMedium,
                    color = MageRideTheme.status.success,
                )
                MageRideCta(
                    label = stringResource(R.string.package_otp_noted),
                    onClick = {
                        state.booked?.let(onBooked)
                        model.onBookingConsumed()
                    },
                )
            }

            state.error?.let { InlineError(message = stringResource(it)) }

            if (state.pickupOtp == null) {
                MageRideCta(
                    label = if (state.estimateMinor == null) {
                        stringResource(R.string.package_get_estimate)
                    } else {
                        stringResource(R.string.package_book)
                    },
                    onClick = { if (state.estimateMinor == null) model.estimate() else model.book() },
                    enabled = state.canEstimate,
                    loading = state.estimating || state.booking,
                    modifier = Modifier.padding(bottom = MageRideTheme.spacing.md),
                )
            }
        }
    }

    pasteFor?.let { end ->
        PasteLinkSheet(
            label = stringResource(
                if (end == PackageEnd.PICKUP) R.string.paste_label_pickup else R.string.paste_label_dropoff,
            ),
            onUse = { place ->
                model.setPlace(end, place)
                pasteFor = null
            },
            onPickOnMap = {
                pasteFor = null
                mapFor = end
            },
            onDismiss = { pasteFor = null },
        )
    }

    mapFor?.let { end ->
        MapPickSheet(
            label = stringResource(
                if (end == PackageEnd.PICKUP) R.string.package_pickup else R.string.package_dropoff,
            ),
            around = (if (end == PackageEnd.PICKUP) state.pickup else state.dropoff)?.point,
            onUse = { place ->
                model.setPlace(end, place)
                mapFor = null
            },
            onDismiss = { mapFor = null },
        )
    }
}

/** P-06's `( S )( M )( L )`. */
@Composable
private fun SizeSelector(size: PackageSize, onSelect: (PackageSize) -> Unit) {
    SingleChoiceSegmentedButtonRow(modifier = Modifier.fillMaxWidth()) {
        PackageSize.entries.forEachIndexed { index, option ->
            SegmentedButton(
                selected = option == size,
                onClick = { onSelect(option) },
                shape = SegmentedButtonDefaults.itemShape(index = index, count = PackageSize.entries.size),
            ) {
                // S / M / L are the same character in all three scripts, so they are the enum's
                // own names rather than three identical `strings.xml` values.
                Text(option.name)
            }
        }
    }
}

/** One end's label, method row and captured value. */
@Composable
private fun EndCapture(
    title: String,
    end: PackageEnd,
    methods: List<PickupMethod>,
    state: PackageBookingState,
    model: PackageBookingViewModel,
    onSearch: (PackageEnd) -> Unit,
    onPickOnMap: (PackageEnd) -> Unit,
    onPaste: (PackageEnd) -> Unit,
) {
    SectionLabel(text = title)
    LocationMethodRow(
        methods = methods,
        selected = if (end == PackageEnd.PICKUP) state.pickupMethod else state.dropoffMethod,
        onSelect = { method ->
            model.setMethod(end, method)
            when (method) {
                PickupMethod.SEARCH -> onSearch(end)

                PickupMethod.MAP -> onPickOnMap(end)

                PickupMethod.PASTE_LINK -> onPaste(end)

                // The recipient is asked over the same P-02 round trip a proxy rider is. C080's
                // SCR-PA-020 owns the tracking that follows; the request itself is SCR-PA-010b's
                // machinery and is reached from there. See the C079 handoff.
                PickupMethod.REQUEST -> Unit
            }
        },
    )
    CapturedPlace(
        place = if (end == PackageEnd.PICKUP) state.pickup else state.dropoff,
        emptyLabel = stringResource(R.string.package_not_set),
    )
}

/** Cash / LankaQR / OnePay / **COD** — the last of which exists only on this screen (US-20.8). */
@Composable
private fun PaymentRow(method: RidePaymentMethod, onChange: (RidePaymentMethod) -> Unit) {
    SingleChoiceSegmentedButtonRow(modifier = Modifier.fillMaxWidth()) {
        RidePaymentMethod.entries.forEachIndexed { index, option ->
            SegmentedButton(
                selected = option == method,
                onClick = { onChange(option) },
                shape = SegmentedButtonDefaults.itemShape(index = index, count = RidePaymentMethod.entries.size),
            ) {
                Text(stringResource(paymentLabel(option)))
            }
        }
    }
}
