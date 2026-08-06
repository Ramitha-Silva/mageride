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
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
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
import lk.mageride.passenger.ui.component.InlineError
import lk.mageride.passenger.ui.component.LabelledTextField
import lk.mageride.passenger.ui.component.MageRideCta
import lk.mageride.passenger.ui.component.PhoneNumberField
import lk.mageride.passenger.ui.component.SectionLabel
import lk.mageride.passenger.ui.theme.ControlTokens
import lk.mageride.passenger.ui.theme.MageRideTheme
import lk.mageride.shared.data.models.ride.LocationRequestState
import org.koin.androidx.compose.koinViewModel

/**
 * SCR-PA-010b — booking for somebody else (US-8.16–8.19, P-01…P-03).
 *
 * The wireframe: `‹ Book for someone else`, the rider's name and mobile with a Contacts affordance,
 * the four **pickup methods**, and — when Request is chosen — *"Waiting for rider to share
 * location… 5:00"* over the CTA.
 *
 * **The unregistered case is a first-class state, not an error** (P-03, US-8.19). A number that
 * belongs to nobody cannot be sent an FCM, so the screen says *"Not a MageRide user — enter pickup
 * manually"* and the Request method removes itself. The booking still works; it just captures the
 * pickup a different way.
 *
 * @param onSearch The Search method — SCR-PA-008 with the result coming back here. The **Map**
 *   method is a modal on this screen: the wireframe assigns no SCR-PA id to a map picker, so there
 *   is no route to navigate to. See [MapPickSheet].
 */
@Composable
@Suppress("LongMethod") // The wireframe's form: name, phone, four methods, countdown, CTA.
internal fun ProxyRiderScreen(
    onBack: () -> Unit,
    onSearch: () -> Unit,
    onDone: () -> Unit,
    model: ProxyRiderViewModel = koinViewModel(),
) {
    val state by model.state.collectAsStateWithLifecycle()
    var pasteOpen by remember { mutableStateOf(false) }
    var mapOpen by remember { mutableStateOf(false) }

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
                text = stringResource(R.string.proxy_title),
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
            LabelledTextField(
                label = stringResource(R.string.proxy_rider_name),
                value = state.riderName,
                onValueChange = model::onNameChanged,
                placeholder = stringResource(R.string.proxy_rider_name_hint),
                keyboardType = KeyboardType.Text,
                imeAction = ImeAction.Next,
            )

            SectionLabel(text = stringResource(R.string.proxy_rider_phone))
            PhoneNumberField(
                value = state.riderPhone,
                onValueChange = model::onPhoneChanged,
                countryCode = PhoneNumber.COUNTRY_CODE,
                placeholder = PhoneNumber.PLACEHOLDER,
            )

            // P-03 / US-8.19. Not an error colour: the booking is fine, one method is not.
            if (state.riderRegistered == false) {
                Text(
                    text = stringResource(R.string.proxy_not_registered),
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }

            SectionLabel(text = stringResource(R.string.proxy_pickup_method))
            LocationMethodRow(
                // The Request method disappears entirely for an unregistered rider rather than
                // being drawn disabled — a control that cannot work is not a control.
                methods = if (state.riderRegistered == false) PACKAGE_PICKUP_METHODS else ALL_METHODS,
                selected = state.method,
                onSelect = { method ->
                    model.setMethod(method)
                    when (method) {
                        PickupMethod.SEARCH -> onSearch()
                        PickupMethod.MAP -> mapOpen = true
                        PickupMethod.PASTE_LINK -> pasteOpen = true
                        PickupMethod.REQUEST -> Unit
                    }
                },
            )

            CapturedPlace(place = state.pickup, emptyLabel = stringResource(R.string.proxy_no_pickup))

            if (state.method == PickupMethod.REQUEST) {
                RequestRow(state = state, onRequest = model::requestRiderLocation)
            }

            state.error?.let { InlineError(message = stringResource(it)) }

            MageRideCta(
                label = stringResource(R.string.proxy_continue),
                onClick = onDone,
                enabled = state.isComplete,
                modifier = Modifier.padding(bottom = MageRideTheme.spacing.md),
            )
        }
    }

    if (pasteOpen) {
        PasteLinkSheet(
            label = stringResource(R.string.paste_label_pickup),
            onUse = { place ->
                model.setPickup(place)
                pasteOpen = false
            },
            onPickOnMap = {
                pasteOpen = false
                mapOpen = true
            },
            onDismiss = { pasteOpen = false },
        )
    }

    if (mapOpen) {
        MapPickSheet(
            label = stringResource(R.string.proxy_pickup_method),
            around = state.pickup?.point,
            onUse = { place ->
                model.setPickup(place)
                mapOpen = false
            },
            onDismiss = { mapOpen = false },
        )
    }
}

/**
 * The P-02 round trip, as the wireframe draws it.
 *
 * Pending is a countdown rather than an indefinite spinner because the window is real and finite:
 * five minutes, enforced by a durable Quartz timer server-side. A booker watching `0:41` knows
 * whether to wait or to switch methods; a booker watching a spinner does not.
 */
@Composable
private fun RequestRow(state: ProxyRiderState, onRequest: () -> Unit) {
    Column(verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs)) {
        when (state.requestState) {
            LocationRequestState.Pending -> Row(
                modifier = Modifier.fillMaxWidth(),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
            ) {
                CircularProgressIndicator(modifier = Modifier.size(ControlTokens.ChipIcon))
                Text(
                    text = stringResource(R.string.proxy_waiting, state.countdown),
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }

            LocationRequestState.Confirmed -> Text(
                text = stringResource(R.string.proxy_confirmed),
                style = MaterialTheme.typography.bodyMedium,
                color = MageRideTheme.status.success,
            )

            // Declined and Expired read the same to a booker — the rider did not share — and both
            // land on the same instruction. Naming which one it was would tell the booker their
            // rider refused, which is not theirs to know (P-02).
            LocationRequestState.Declined, LocationRequestState.Expired -> Text(
                text = stringResource(R.string.proxy_no_answer),
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )

            LocationRequestState.RiderNotRegistered, null -> Unit
        }

        if (state.requestState != LocationRequestState.Confirmed) {
            MageRideCta(
                label = stringResource(R.string.proxy_request_location),
                onClick = onRequest,
                enabled = state.canRequestLocation,
            )
        }
    }
}
