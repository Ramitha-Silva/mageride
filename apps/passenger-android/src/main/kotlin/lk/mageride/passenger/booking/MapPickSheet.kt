package lk.mageride.passenger.booking

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.LocationOn
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import lk.mageride.passenger.R
import lk.mageride.passenger.map.MageRideMap
import lk.mageride.passenger.map.MapCamera
import lk.mageride.passenger.ui.Coordinates
import lk.mageride.passenger.ui.component.MageRideCta
import lk.mageride.passenger.ui.theme.ControlTokens
import lk.mageride.passenger.ui.theme.MageRideTheme
import lk.mageride.shared.data.models.GeoPoint
import lk.mageride.shared.data.models.Place

/**
 * The **Map** capture method — drop a pin by moving the map under it.
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
 * @param around Where to open. The passenger's fix, or whatever the field already holds.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun MapPickSheet(label: String, around: GeoPoint?, onUse: (Place) -> Unit, onDismiss: () -> Unit) {
    var centre by remember { mutableStateOf(around) }

    ModalBottomSheet(onDismissRequest = onDismiss, sheetState = rememberModalBottomSheetState()) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .padding(horizontal = MageRideTheme.spacing.md)
                .padding(bottom = MageRideTheme.spacing.lg),
            verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.sm),
        ) {
            Text(
                text = label,
                style = MaterialTheme.typography.titleMedium,
                color = MaterialTheme.colorScheme.onSurface,
            )

            Box(
                modifier = Modifier
                    .fillMaxWidth()
                    .height(ControlTokens.SheetMap),
                contentAlignment = Alignment.Center,
            ) {
                MageRideMap(
                    camera = around?.let { MapCamera(it.lat, it.lng) } ?: MapCamera.Default,
                    onCameraIdle = { centre = it },
                )
                // The fixed marker. Drawn in Compose over the map rather than as a map annotation,
                // which is what makes it stay exactly at the centre through every gesture.
                Icon(
                    imageVector = Icons.Filled.LocationOn,
                    contentDescription = null,
                    modifier = Modifier.size(ControlTokens.ListRowIcon),
                    tint = MaterialTheme.colorScheme.primary,
                )
            }

            Text(
                text = centre?.let(Coordinates::format) ?: stringResource(R.string.map_pick_move),
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )

            MageRideCta(
                label = stringResource(R.string.paste_use),
                onClick = { centre?.let { onUse(Place(lat = it.lat, lng = it.lng)) } },
                enabled = centre != null,
            )
        }
    }
}
