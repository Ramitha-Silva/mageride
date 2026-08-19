package lk.mageride.passenger.booking

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.LocationOn
import androidx.compose.material.icons.filled.Star
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
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
import lk.mageride.passenger.map.MageRideMap
import lk.mageride.passenger.map.MapCamera
import lk.mageride.passenger.ui.Coordinates
import lk.mageride.passenger.ui.component.LabelledTextField
import lk.mageride.passenger.ui.component.MageRideCta
import lk.mageride.passenger.ui.theme.ControlTokens
import lk.mageride.passenger.ui.theme.MageRideTheme
import lk.mageride.shared.data.models.GeoPoint
import lk.mageride.shared.data.models.Place
import lk.mageride.shared.data.models.query.GeocodedPlace
import lk.mageride.shared.data.models.query.GeocodedPlaceSource
import org.koin.androidx.compose.koinViewModel

/**
 * The **Map** capture method — search for the area, then drop a pin by moving the map under it.
 *
 * **This is not a wireframe screen and has no SCR-PA id**, which is itself worth stating: the
 * wireframe offers *"Map pin"* / *"Map"* as a capture method on SCR-PA-010b and SCR-PA-012, and
 * *"📌 Select on map"* on SCR-PA-008, but draws no screen for any of them. A modal is therefore the
 * conservative reading — it is what SCR-PA-012a is for its own method, it needs no back-stack entry
 * of its own, and it leaves the form underneath exactly as the passenger left it. Recorded in the
 * C079 handoff.
 *
 * **The pin is fixed and the map moves.** MapLibre's draggable-annotation API lives in a plugin
 * artifact this module cannot depend on, and the centre-pin pattern is the one every ride app uses
 * anyway — it works with one thumb and needs no precise touch on a small marker.
 *
 * **The search box is what makes the pin usable across a city** (Δ handset report). Panning was the
 * only way to move this map, so a pickup two towns away meant dragging there at whatever zoom the
 * sheet opened on. Typing puts the map on the junction; the pin still decides the metres. See
 * [MapPickViewModel] for what a search result does and does not commit.
 *
 * **The sheet opens fully expanded and its content scrolls**, which is a defect fix rather than a
 * preference. At the partially-expanded default the 320 dp map pushed *"Use this location"* below
 * the fold — and `PanningMapView` (rightly) claims every drag that starts on the map, so the one
 * gesture a passenger would try to reveal the button instead panned the map. The CTA was
 * unreachable without knowing to drag the strip beside the title.
 *
 * @param around Where to open. The passenger's fix, or whatever the field already holds.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun MapPickSheet(
    label: String,
    around: GeoPoint?,
    onUse: (Place) -> Unit,
    onDismiss: () -> Unit,
    model: MapPickViewModel = koinViewModel(),
) {
    val state by model.state.collectAsStateWithLifecycle()

    // One model serves SCR-PA-010b's pickup and both of SCR-PA-012's ends, so each opening starts
    // from the field it was opened for rather than from the last one. The composable is created
    // fresh every time the sheet opens, which is what makes `Unit` the right key.
    LaunchedEffect(Unit) { model.opened(around) }

    ModalBottomSheet(
        onDismissRequest = onDismiss,
        sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true),
    ) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .verticalScroll(rememberScrollState())
                .padding(horizontal = MageRideTheme.spacing.md)
                .padding(bottom = MageRideTheme.spacing.lg),
            verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.sm),
        ) {
            Text(
                text = label,
                style = MaterialTheme.typography.titleMedium,
                color = MaterialTheme.colorScheme.onSurface,
            )

            LabelledTextField(
                label = stringResource(R.string.map_pick_search),
                value = state.query,
                onValueChange = model::onQueryChanged,
                placeholder = stringResource(R.string.search_drop_placeholder),
                keyboardType = KeyboardType.Text,
                imeAction = ImeAction.Search,
            )

            Box(
                modifier = Modifier
                    .fillMaxWidth()
                    .height(ControlTokens.SheetMap),
                contentAlignment = Alignment.Center,
            ) {
                MageRideMap(
                    camera = around?.let { MapCamera(it.lat, it.lng) } ?: MapCamera.Default,
                    // A search result moves the map. `camera` cannot — it is read once, when the
                    // style loads — which is what `focus` was added for.
                    focus = state.focus,
                    onCameraIdle = model::onPinMoved,
                    // A pin is only as accurate as the zoom it was placed at, and the sheet's map
                    // is small. Pinch works too; this is the one-handed way to the same thing.
                    zoomControls = true,
                )
                // The fixed marker. Drawn in Compose over the map rather than as a map annotation,
                // which is what makes it stay exactly at the centre through every gesture.
                Icon(
                    imageVector = Icons.Filled.LocationOn,
                    contentDescription = null,
                    modifier = Modifier.size(ControlTokens.ListRowIcon),
                    tint = MaterialTheme.colorScheme.primary,
                )

                // Over the map rather than above it: the sheet keeps one height whether or not a
                // search is open, so the CTA never moves under the passenger's thumb.
                Predictions(
                    state = state,
                    onChoose = model::onPredictionChosen,
                    modifier = Modifier.align(Alignment.TopCenter),
                )
            }

            Text(
                text = state.chosen?.displayName
                    ?: state.centre?.let(Coordinates::format)
                    ?: stringResource(R.string.map_pick_move),
                style = MaterialTheme.typography.bodyMedium,
                color = if (state.chosen == null && state.centre == null) {
                    MaterialTheme.colorScheme.onSurfaceVariant
                } else {
                    MaterialTheme.colorScheme.onSurface
                },
            )

            MageRideCta(
                label = stringResource(R.string.paste_use),
                onClick = { state.selection?.let(onUse) },
                enabled = state.selection != null,
            )
        }
    }
}

/**
 * The search results, as a card over the top of the map.
 *
 * Nothing at all when there is nothing to say — an empty field, or a query too short to spend a
 * request on — so the map is unobstructed for the gesture the sheet is actually for.
 */
@Composable
private fun Predictions(state: MapPickState, onChoose: (GeocodedPlace) -> Unit, modifier: Modifier = Modifier) {
    if (state.predictions.isEmpty() && !state.searching && !state.geocoderDown) return

    Surface(
        modifier = modifier
            .fillMaxWidth()
            .padding(MageRideTheme.spacing.xs),
        color = MaterialTheme.colorScheme.surface,
        shape = RoundedCornerShape(MageRideTheme.radius.md),
        tonalElevation = MageRideTheme.elevation.level2,
    ) {
        Column(
            modifier = Modifier
                .heightIn(max = ControlTokens.PredictionOverlay)
                .verticalScroll(rememberScrollState())
                .padding(MageRideTheme.spacing.xs),
        ) {
            when {
                state.searching -> Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.Center,
                ) {
                    CircularProgressIndicator(modifier = Modifier.size(ControlTokens.RowIcon))
                }

                // The pin is unaffected by a geocoder that cannot answer, so this says so and
                // leaves the map alone — the same call AL-14 makes about a reverse geocode.
                state.geocoderDown -> Text(
                    text = stringResource(R.string.search_geocoder_down),
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )

                else -> state.predictions.forEach { place ->
                    PredictionRow(place = place, onClick = { onChoose(place) })
                }
            }
        }
    }
}

/** One result: the ★ / 📍 its source earns it, and what it is called. */
@Composable
private fun PredictionRow(place: GeocodedPlace, onClick: () -> Unit) {
    val saved = place.source == GeocodedPlaceSource.SAVED || place.source == GeocodedPlaceSource.RECENT

    Row(
        modifier = Modifier
            .fillMaxWidth()
            .clickable(onClick = onClick)
            .padding(vertical = MageRideTheme.spacing.xs),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
    ) {
        Icon(
            imageVector = if (saved) Icons.Filled.Star else Icons.Filled.LocationOn,
            contentDescription = null,
            modifier = Modifier.size(ControlTokens.RowIcon),
            tint = MaterialTheme.colorScheme.onSurfaceVariant,
        )
        Text(
            text = place.displayName,
            modifier = Modifier.weight(1f),
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurface,
        )
    }
}
