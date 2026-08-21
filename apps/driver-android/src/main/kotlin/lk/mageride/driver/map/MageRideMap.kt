package lk.mageride.driver.map

import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.MyLocation
import androidx.compose.material3.FloatingActionButton
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.compose.LocalLifecycleOwner
import lk.mageride.driver.R
import lk.mageride.driver.di.DriverEnvironment
import lk.mageride.driver.ui.theme.MageRideTheme
import org.koin.compose.koinInject
import org.maplibre.android.MapLibre
import org.maplibre.android.camera.CameraUpdateFactory
import org.maplibre.android.geometry.LatLng
import org.maplibre.android.maps.MapLibreMap
import org.maplibre.android.maps.MapView
import org.maplibre.android.maps.Style

/**
 * The map host every screen with a map puts inside itself.
 *
 * D2' §0.1 and §0.3: MapLibre GL Native over self-served PMTiles, light and dark styles, no Google
 * Maps. This composable owns the `MapView`'s lifecycle — which is the whole reason it exists,
 * because `MapView` predates Compose and needs all nine of its lifecycle callbacks forwarded or it
 * leaks its GL surface on the first rotation.
 *
 * **[onMapReady] is the seam for the screen groups.** C070's dashboard and C077's passenger map
 * add their own sources and layers to the style they are handed; this file installs none, because
 * a shell that decided what a marker looked like would be writing the screens.
 * [VehicleLayers] carries the D2' §0.3 primitives they share.
 *
 * @param camera Where to start. A screen that tracks the driver moves the camera itself afterwards.
 * @param onRecentre The §0.3 recentre FAB ("both apps"). `null` hides it — a static map that
 *   cannot be panned has nothing to recentre.
 * @param controlsBottomInset Lifts the recentre FAB off the map's bottom edge. A map TALLER than
 *   the screen it sits on parks its only control below the fold, which is a control the driver
 *   cannot reach without first scrolling past the thing it acts on; SCR-DA-010's dashboard passes
 *   its own overflow here so the FAB stays on the first screenful. `0.dp` — a map that fits —
 *   leaves it where §0.3 draws it.
 */
@Composable
internal fun MageRideMap(
    modifier: Modifier = Modifier,
    camera: MapCamera = MapCamera.Default,
    darkTheme: Boolean = isSystemInDarkTheme(),
    onRecentre: (() -> Unit)? = null,
    controlsBottomInset: Dp = 0.dp,
    onMapReady: (MapLibreMap, Style) -> Unit = { _, _ -> },
) {
    val context = LocalContext.current
    val environment = koinInject<DriverEnvironment>()
    val description = stringResource(R.string.map_content_description)

    // MapLibre.getInstance loads the native library and sets up the file source. Idempotent, and
    // called here rather than in Application.onCreate so a driver who never opens a map never
    // pays for the .so.
    remember { MapLibre.getInstance(context) }

    val mapView = remember {
        MapView(context).apply {
            getMapAsync { map ->
                map.setStyle(
                    Style.Builder().fromJson(MapStyles.forTheme(context, environment.pmTilesUrl, darkTheme)),
                ) { style ->
                    map.cameraPosition = org.maplibre.android.camera.CameraPosition.Builder()
                        .target(LatLng(camera.lat, camera.lng))
                        .zoom(camera.zoom)
                        .build()
                    onMapReady(map, style)
                }
            }
        }
    }

    MapViewLifecycle(mapView)

    Box(modifier = modifier) {
        AndroidView(
            factory = { mapView },
            modifier = Modifier
                .fillMaxSize()
                .semantics { contentDescription = description },
        )

        if (onRecentre != null) {
            FloatingActionButton(
                onClick = onRecentre,
                modifier = Modifier
                    .align(Alignment.BottomEnd)
                    .padding(end = MageRideTheme.spacing.sm, bottom = MageRideTheme.spacing.sm + controlsBottomInset),
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

/** Moves the camera to [target], animated. The recentre FAB's action, and a tracking screen's tick. */
internal fun MapLibreMap.centreOn(target: LatLng, zoom: Double = MapCamera.DEFAULT_ZOOM) {
    animateCamera(CameraUpdateFactory.newLatLngZoom(target, zoom))
}

/**
 * Forwards the Android lifecycle into [MapView].
 *
 * Every callback, not a subset: `onStart`/`onStop` bind and release the GL context, `onPause`
 * stops the render loop, and `onDestroy` frees the native map. Missing `onDestroy` alone leaks a
 * few megabytes of native memory per rotation, which on a five-year-old handset is a crash within
 * a shift.
 */
@Composable
private fun MapViewLifecycle(mapView: MapView) {
    val lifecycleOwner = LocalLifecycleOwner.current

    DisposableEffect(lifecycleOwner, mapView) {
        mapView.onCreate(null)

        val observer = LifecycleEventObserver { _, event ->
            when (event) {
                Lifecycle.Event.ON_START -> mapView.onStart()
                Lifecycle.Event.ON_RESUME -> mapView.onResume()
                Lifecycle.Event.ON_PAUSE -> mapView.onPause()
                Lifecycle.Event.ON_STOP -> mapView.onStop()
                Lifecycle.Event.ON_DESTROY -> mapView.onDestroy()
                else -> Unit
            }
        }

        lifecycleOwner.lifecycle.addObserver(observer)
        onDispose {
            lifecycleOwner.lifecycle.removeObserver(observer)
            mapView.onDestroy()
        }
    }
}
