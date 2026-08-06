package lk.mageride.passenger.home

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.LocationOn
import androidx.compose.material.icons.filled.PushPin
import androidx.compose.material.icons.filled.Star
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import lk.mageride.passenger.R
import lk.mageride.passenger.ui.component.LabelledTextField
import lk.mageride.passenger.ui.component.SectionLabel
import lk.mageride.passenger.ui.theme.ControlTokens
import lk.mageride.passenger.ui.theme.MageRideTheme
import lk.mageride.shared.data.models.GeoPoint
import lk.mageride.shared.data.models.query.GeocodedPlace
import lk.mageride.shared.data.models.query.GeocodedPlaceSource
import org.koin.androidx.compose.koinViewModel

/**
 * SCR-PA-008 — the destination field.
 *
 * The wireframe: a `‹ Set route` bar, the green *"Current location"* pickup row over the red drop
 * field, a Predictions list, and *"📌 Select on map"* / *"＋ Add address"* at the foot.
 *
 * **GEO ONLY (AL-17).** Typing `138` searches for a *place* called 138 and returns places; no route
 * row ever appears in this list, and selecting a prediction always yields a coordinate. D2'
 * §SCR-PA-008 still describes the opposite — see [SearchLocationViewModel] for the conflict and
 * the C078 handoff for what it costs US-7.9.
 *
 * @param around Where the passenger is, so the geocoder biases results toward them.
 * @param onPlaceChosen Selecting a prediction. C079's SCR-PA-009 is where this leads.
 * @param onPickOnMap The wireframe's *"Select on map"* — also the way out when the geocoder is down.
 * @param onAddAddress *"＋ Add address"* — C083's SCR-PA-026.
 */
@Composable
internal fun SearchLocationScreen(
    around: GeoPoint?,
    onBack: () -> Unit,
    onPlaceChosen: (GeocodedPlace) -> Unit,
    onPickOnMap: () -> Unit,
    onAddAddress: () -> Unit,
    model: SearchLocationViewModel = koinViewModel(),
) {
    val state by model.state.collectAsStateWithLifecycle()

    LaunchedEffect(around) {
        around?.let(model::biasAround)
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
                text = stringResource(R.string.search_title),
                style = MaterialTheme.typography.titleLarge,
                color = MaterialTheme.colorScheme.onSurface,
            )
        }

        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(horizontal = MageRideTheme.spacing.md),
            verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
        ) {
            // The pickup row. Fixed to the current location on this screen: SCR-PA-009 is where a
            // pickup becomes editable, and offering two editable fields here would be two ways to
            // do the same thing one screen apart.
            PickupRow()

            LabelledTextField(
                label = stringResource(R.string.search_drop_label),
                value = state.query,
                onValueChange = model::onQueryChanged,
                // The hint says what this field takes, which is the fence made visible: a place or
                // an address, never a route number (AL-17).
                placeholder = stringResource(R.string.search_drop_placeholder),
                keyboardType = KeyboardType.Text,
                imeAction = ImeAction.Search,
                isError = state.geocoderDown,
            )

            when {
                state.searching -> Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(MageRideTheme.spacing.sm),
                    horizontalArrangement = Arrangement.Center,
                ) {
                    CircularProgressIndicator(modifier = Modifier.size(ControlTokens.RowIcon))
                }

                state.geocoderDown -> Text(
                    text = stringResource(R.string.search_geocoder_down),
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.error,
                )

                else -> SectionLabel(
                    text = if (state.showingDefaults) {
                        stringResource(R.string.search_saved)
                    } else {
                        stringResource(R.string.search_predictions)
                    },
                )
            }

            LazyColumn(modifier = Modifier.weight(1f)) {
                items(state.predictions) { place ->
                    PredictionRow(
                        place = place,
                        onClick = {
                            // §2.2's `place_recents`, which is what fills SCR-PA-010's "Recent"
                            // list. Local only — nothing about this is sent anywhere.
                            model.onPlaceChosen(place)
                            onPlaceChosen(place)
                        },
                    )
                }
            }

            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(bottom = MageRideTheme.spacing.md),
                horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
            ) {
                OutlinedButton(onClick = onPickOnMap, modifier = Modifier.weight(1f)) {
                    Icon(
                        imageVector = Icons.Filled.PushPin,
                        contentDescription = null,
                        modifier = Modifier.size(ControlTokens.ChipIcon),
                    )
                    Text(
                        text = stringResource(R.string.search_select_on_map),
                        modifier = Modifier.padding(start = MageRideTheme.spacing.xxs),
                    )
                }
                OutlinedButton(onClick = onAddAddress, modifier = Modifier.weight(1f)) {
                    Icon(
                        imageVector = Icons.Filled.Add,
                        contentDescription = null,
                        modifier = Modifier.size(ControlTokens.ChipIcon),
                    )
                    Text(
                        text = stringResource(R.string.search_add_address),
                        modifier = Modifier.padding(start = MageRideTheme.spacing.xxs),
                    )
                }
            }
        }
    }
}

/** The wireframe's green `cdot` row — where the passenger is now. */
@Composable
private fun PickupRow() {
    Row(
        modifier = Modifier.fillMaxWidth(),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
    ) {
        Box(
            modifier = Modifier
                .size(MageRideTheme.spacing.sm)
                .background(MageRideTheme.status.success, CircleShape),
        )
        Text(
            text = stringResource(R.string.search_current_location),
            style = MaterialTheme.typography.bodyLarge,
            color = MaterialTheme.colorScheme.onSurface,
        )
    }
}

/**
 * One prediction.
 *
 * The leading glyph is the row's **source** — ★ for one of the passenger's own saved or recent
 * places, 📍 for a geocoded one. That is `GeocodedPlace.source`, which is why the search is one
 * call rather than three lists merged on the device.
 */
@Composable
private fun PredictionRow(place: GeocodedPlace, onClick: () -> Unit) {
    val saved = place.source == GeocodedPlaceSource.SAVED || place.source == GeocodedPlaceSource.RECENT

    Row(
        modifier = Modifier
            .fillMaxWidth()
            .clickable(onClick = onClick)
            .padding(vertical = MageRideTheme.spacing.xs),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.sm),
    ) {
        Icon(
            imageVector = if (saved) Icons.Filled.Star else Icons.Filled.LocationOn,
            contentDescription = null,
            modifier = Modifier.size(ControlTokens.RowIcon),
            tint = MaterialTheme.colorScheme.onSurfaceVariant,
        )
        Column(modifier = Modifier.weight(1f)) {
            Text(
                text = place.displayName,
                style = MaterialTheme.typography.bodyLarge,
                color = MaterialTheme.colorScheme.onSurface,
            )
            val detail = listOfNotNull(place.line1, place.city).joinToString(SEPARATOR)
            if (detail.isNotBlank()) {
                Text(
                    text = detail,
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }
    }
}

/** The wireframe's `·` between an address line and its city. Punctuation, not copy. */
private const val SEPARATOR = " · "
