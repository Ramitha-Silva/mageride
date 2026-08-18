package lk.mageride.passenger.map

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.MyLocation
import androidx.compose.material.icons.filled.Remove
import androidx.compose.material3.FloatingActionButton
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.SmallFloatingActionButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.res.stringResource
import lk.mageride.passenger.R
import lk.mageride.passenger.ui.theme.MageRideTheme
import org.maplibre.android.camera.CameraUpdateFactory
import org.maplibre.android.maps.MapLibreMap

/**
 * The controls drawn *on* a [MageRideMap] — §0.3's recentre disc, and the zoom pair above it.
 *
 * Here rather than inside `MageRideMap` because they are the one part of that widget a screen
 * genuinely configures: the map itself is the same §0.3 layer stack everywhere (MAP-03), while
 * whether a passenger may move the camera is a per-screen question. SCR-PA-023's trip line is an
 * illustration of a journey already taken and wants none of this; SCR-PA-010 and SCR-PA-026 are
 * maps a passenger works with and want all of it.
 *
 * **Both are optional and both are drawn in one stack**, so a map with zoom but no recentre does
 * not leave a hole where the disc would have been.
 *
 * @param zoom Called with the number of zoom levels to move — `+1` from `＋`, `-1` from `−`.
 *   `null` hides the pair.
 * @param onRecentre The recentre disc. `null` hides it. The camera move belongs to the caller: it
 *   is the one holding the [MapLibreMap] and the position to move to.
 */
@Composable
internal fun MapControls(
    modifier: Modifier = Modifier,
    zoom: ((Double) -> Unit)? = null,
    onRecentre: (() -> Unit)? = null,
) {
    Column(
        modifier = modifier,
        verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
        horizontalAlignment = Alignment.End,
    ) {
        if (zoom != null) {
            ZoomButton(
                icon = Icons.Filled.Add,
                description = stringResource(R.string.map_zoom_in),
                onClick = { zoom(ZOOM_STEP) },
            )
            ZoomButton(
                icon = Icons.Filled.Remove,
                description = stringResource(R.string.map_zoom_out),
                onClick = { zoom(-ZOOM_STEP) },
            )
        }

        if (onRecentre != null) {
            FloatingActionButton(
                onClick = onRecentre,
                containerColor = MaterialTheme.colorScheme.surface,
                contentColor = MaterialTheme.colorScheme.onSurface,
            ) {
                Icon(
                    imageVector = Icons.Filled.MyLocation,
                    contentDescription = stringResource(R.string.map_recentre),
                )
            }
        }
    }
}

/** One `＋` / `−` disc. Smaller than the recentre FAB, which is the primary action of the pair. */
@Composable
private fun ZoomButton(icon: ImageVector, description: String, onClick: () -> Unit) {
    SmallFloatingActionButton(
        onClick = onClick,
        containerColor = MaterialTheme.colorScheme.surface,
        contentColor = MaterialTheme.colorScheme.onSurface,
    ) {
        Icon(imageVector = icon, contentDescription = description)
    }
}

/**
 * Steps the camera by [delta] zoom levels, animated and clamped by MapLibre to the style's range.
 *
 * One level per tap is the platform convention and it is what makes the button predictable — a
 * larger step reads as a jump, a smaller one as a broken button.
 */
internal fun MapLibreMap.zoomBy(delta: Double) {
    animateCamera(CameraUpdateFactory.zoomBy(delta))
}

/** How far one `＋` or `−` moves the camera. */
private const val ZOOM_STEP = 1.0
