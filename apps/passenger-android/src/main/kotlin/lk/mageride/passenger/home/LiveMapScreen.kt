package lk.mageride.passenger.home

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.History
import androidx.compose.material.icons.filled.Menu
import androidx.compose.material.icons.filled.Search
import androidx.compose.material.icons.filled.Star
import androidx.compose.material.icons.filled.Tune
import androidx.compose.material3.AssistChip
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.res.stringResource
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.repeatOnLifecycle
import lk.mageride.passenger.R
import lk.mageride.passenger.live.LiveStatus
import lk.mageride.passenger.map.MageRideMap
import lk.mageride.passenger.map.MapCamera
import lk.mageride.passenger.shell.LocalDrawerControl
import lk.mageride.passenger.ui.component.SectionLabel
import lk.mageride.passenger.ui.theme.ControlTokens
import lk.mageride.passenger.ui.theme.MageRideTheme
import lk.mageride.shared.data.models.GeoPoint
import lk.mageride.shared.data.models.iam.SavedAddress
import lk.mageride.shared.data.models.query.GeocodedPlace
import org.koin.androidx.compose.koinViewModel

/**
 * SCR-PA-010 — the live map, and the app's home.
 *
 * The wireframe: a `≡ MageRide ⦿` app bar over a full-bleed map with the recentre FAB, and a
 * bottom sheet carrying *"Where to?"*, the ★ Home / ★ Work chips and the recent destinations. The
 * bottom navigation and the offline banner are the shell's (C076) and are drawn around this.
 *
 * **SCR-PA-032 is a state of this screen, not a screen of its own.** When the live plane is not
 * connected the markers dim to last-known (US-15.2), a reconnect chip appears over the map, and the
 * shell's banner says why. Nothing is erased: a passenger who has lost signal still wants to know
 * where the bus was.
 *
 * SCR-PA-006 and SCR-PA-007 are modal sheets over this map and are hosted here for the same reason
 * — both are drawn over it in the wireframe, and neither has a back-stack entry.
 *
 * **The map is PINNED at a fixed height and the sheet below it scrolls.** The two used to be the
 * other way round: the map took `weight(1f)` above a wrap-content sheet, so the sheet's growth
 * came straight out of the map — the first recent destination halved it, and a passenger with a
 * full history got a strip. `ControlTokens.HomeMap` is the height now, and the recents scroll
 * inside [HomeSheet] instead of eating the thing the screen is named after.
 *
 * **The map is deliberately OUTSIDE the scrolling container**, which is not a layout preference.
 * A `MapView` inside a `verticalScroll` takes focus when it or a control on it is touched, and
 * the scroll container then brings it into view — so tapping `＋` while the recents were scrolled
 * up snapped the page back to the top, once per tap. Keeping the scroll to the sheet also leaves
 * every one-finger drag on the map to the map, which is what pan is.
 */
@Composable
@Suppress("LongMethod") // The wireframe's layout tree: app bar, map, sheet, and two modal sheets.
internal fun LiveMapScreen(
    onSearch: () -> Unit,
    onRequestModeBAccess: (String) -> Unit,
    onOpenSavedAddress: (SavedAddress) -> Unit,
    onOpenRecent: (GeocodedPlace) -> Unit,
    onAddAddress: () -> Unit,
    model: LiveMapViewModel = koinViewModel(),
) {
    val state by model.state.collectAsStateWithLifecycle()
    val openDrawer = LocalDrawerControl.current
    var filterOpen by remember { mutableStateOf(false) }

    // The sheet's two lists are written on OTHER screens — recents on SCR-PA-008, saved addresses
    // on SCR-PA-026 — and neither source has a change feed, so both are re-read whenever this
    // screen comes back to the front. This model is scoped to the back-stack entry and survives
    // the trip, so nothing else would ever re-read them. See `LiveMapViewModel.onResumed`.
    val lifecycle = LocalLifecycleOwner.current.lifecycle
    LaunchedEffect(lifecycle) {
        lifecycle.repeatOnLifecycle(Lifecycle.State.RESUMED) { model.onResumed() }
    }

    Column(modifier = Modifier.fillMaxSize()) {
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(horizontal = MageRideTheme.spacing.xs),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            // The wireframe's `≡`. AL-31's no-hamburger rule is the DRIVER dashboard's; SCR-PA-033
            // says the passenger drawer opens "from the ≡ menu / 'Menu' tab", and this is the ≡.
            IconButton(onClick = openDrawer) {
                Icon(
                    imageVector = Icons.Filled.Menu,
                    contentDescription = stringResource(R.string.nav_menu),
                    tint = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
            Text(
                text = stringResource(R.string.app_name),
                style = MaterialTheme.typography.titleLarge,
                color = MaterialTheme.colorScheme.onSurface,
            )
            Box(modifier = Modifier.weight(1f))
            IconButton(onClick = { filterOpen = true }) {
                Icon(
                    imageVector = Icons.Filled.Tune,
                    contentDescription = stringResource(R.string.filter_title),
                    tint = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }

        Box(
            modifier = Modifier
                .fillMaxWidth()
                .height(ControlTokens.HomeMap),
        ) {
            MageRideMap(
                modifier = Modifier.fillMaxSize(),
                vehicles = state.vehicles,
                userPosition = state.fix,
                camera = state.fix?.let { MapCamera(it.lat, it.lng) } ?: MapCamera.Default,
                // §0.3's recentre FAB. Non-null is what draws it; the animation back to the blue
                // dot is `MageRideMap`'s, because only it holds the `MapLibreMap`. This screen has
                // nothing to add — the map does not track the passenger, so there is no follow
                // mode here to re-arm.
                onRecentre = { },
                onVehicleTap = { vehicleId ->
                    when (val tap = model.onMarkerTapped(vehicleId)) {
                        is MarkerTap.RequestModeBAccess -> onRequestModeBAccess(tap.vehicleId)

                        // The popup opens from the view model's own state; nothing to do here.
                        is MarkerTap.ShowPopup, MarkerTap.Ignored -> Unit
                    }
                },
                // Pinch still works — the map owns every gesture on it. These are the
                // one-handed and the accessible way to the same thing.
                zoomControls = true,
                dimmed = state.stale,
            )

            if (state.status == LiveStatus.Connecting) {
                ReconnectChip(modifier = Modifier.align(Alignment.TopCenter))
            }

            val empty = state.emptyReason
            if (empty != EmptyReason.NONE) {
                EmptyNotice(reason = empty, modifier = Modifier.align(Alignment.TopCenter))
            }
        }

        HomeSheet(
            modifier = Modifier.weight(1f),
            shortcuts = state.shortcuts,
            recents = state.recents,
            onSearch = onSearch,
            onShortcut = onOpenSavedAddress,
            onAddAddress = onAddAddress,
            onRecent = onOpenRecent,
        )
    }

    if (filterOpen) {
        ModeFilterSheet(
            filter = state.filter,
            onModeChanged = model::setMode,
            onTypeChanged = model::setType,
            onDismiss = { filterOpen = false },
        )
    }

    state.selected?.let { vehicle ->
        VehiclePopup(
            vehicle = vehicle,
            detail = state.detail,
            around = state.fix?.let { GeoPoint(it.lat, it.lng) },
            onDismiss = model::dismissPopup,
        )
    }
}

/** The wireframe's bottom sheet: the search entry, US-7.13's shortcuts and the Recent rows. */
@Composable
private fun HomeSheet(
    shortcuts: List<SavedAddress>,
    recents: List<GeocodedPlace>,
    onSearch: () -> Unit,
    onShortcut: (SavedAddress) -> Unit,
    onAddAddress: () -> Unit,
    onRecent: (GeocodedPlace) -> Unit,
    modifier: Modifier = Modifier,
) {
    Surface(
        modifier = modifier.fillMaxWidth(),
        color = MaterialTheme.colorScheme.background,
        shape = RoundedCornerShape(
            topStart = MageRideTheme.radius.card,
            topEnd = MageRideTheme.radius.card,
        ),
        tonalElevation = MageRideTheme.elevation.level2,
    ) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .verticalScroll(rememberScrollState())
                .padding(MageRideTheme.spacing.sm),
            verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
            horizontalAlignment = Alignment.CenterHorizontally,
        ) {
            Box(
                modifier = Modifier
                    .width(ControlTokens.SheetHandleWidthLarge)
                    .height(ControlTokens.SheetHandleHeight)
                    .background(
                        MaterialTheme.colorScheme.outline,
                        RoundedCornerShape(MageRideTheme.radius.sm),
                    ),
            )

            // "Where to?" — a button rather than a field. The typing happens on SCR-PA-008, which
            // is a destination with its own back stack; a field here would need two search UIs.
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .height(ControlTokens.SearchBar)
                    .background(
                        MaterialTheme.colorScheme.surfaceVariant,
                        RoundedCornerShape(MageRideTheme.radius.md),
                    )
                    .clickable(onClick = onSearch)
                    .padding(horizontal = MageRideTheme.spacing.sm),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
            ) {
                Icon(
                    imageVector = Icons.Filled.Search,
                    contentDescription = null,
                    modifier = Modifier.size(ControlTokens.RowIcon),
                    tint = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                Text(
                    text = stringResource(R.string.map_where_to),
                    style = MaterialTheme.typography.bodyLarge,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }

            // "★ Home | ★ Work | ＋ Add". The ＋ is always there, including — especially — when
            // there are no shortcuts at all, because it is how a passenger gets their first one.
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .horizontalScroll(rememberScrollState()),
                horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
            ) {
                shortcuts.forEach { address ->
                    AssistChip(
                        onClick = { onShortcut(address) },
                        label = { Text(address.label) },
                        leadingIcon = { ChipIcon(Icons.Filled.Star) },
                    )
                }
                AssistChip(
                    onClick = onAddAddress,
                    label = { Text(stringResource(R.string.map_add_address)) },
                    leadingIcon = { ChipIcon(Icons.Filled.Add) },
                )
            }

            if (recents.isNotEmpty()) {
                SectionLabel(
                    text = stringResource(R.string.map_recent),
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(top = MageRideTheme.spacing.xxs),
                )
                recents.forEach { place ->
                    RecentRow(place = place, onClick = { onRecent(place) })
                }
            }
        }
    }
}

/** A chip's leading glyph, at the one size §0.2 gives an inline icon. */
@Composable
private fun ChipIcon(icon: ImageVector) {
    Icon(imageVector = icon, contentDescription = null, modifier = Modifier.size(ControlTokens.ChipIcon))
}

/** One "🕘 Nugegoda Junction" row — a place this handset has been looking for (§2.2, local only). */
@Composable
private fun RecentRow(place: GeocodedPlace, onClick: () -> Unit) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .clickable(onClick = onClick)
            .padding(vertical = MageRideTheme.spacing.xs),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.sm),
    ) {
        Icon(
            imageVector = Icons.Filled.History,
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
            place.line1?.takeIf { it.isNotBlank() }?.let { line ->
                Text(
                    text = line,
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }
    }
}

/** SCR-PA-032's reconnect indicator — the socket is down and something is being done about it. */
@Composable
private fun ReconnectChip(modifier: Modifier = Modifier) {
    Text(
        text = stringResource(R.string.map_reconnecting),
        modifier = modifier
            .padding(MageRideTheme.spacing.xs)
            .background(
                MaterialTheme.colorScheme.surfaceVariant,
                RoundedCornerShape(MageRideTheme.radius.lg),
            )
            .padding(horizontal = MageRideTheme.spacing.sm, vertical = MageRideTheme.spacing.xxs),
        style = MaterialTheme.typography.labelMedium,
        color = MaterialTheme.colorScheme.onSurfaceVariant,
    )
}

/**
 * US-7.14 — *"an in-app message when no vehicles of my selected type are active in my area,
 * instead of seeing an empty map with no context"*.
 *
 * The context is which kind of empty it is, which is why [EmptyReason] has three values and not a
 * boolean: "you switched everything off" and "there is nothing here" ask for different actions.
 */
@Composable
private fun EmptyNotice(reason: EmptyReason, modifier: Modifier = Modifier) {
    val message = when (reason) {
        EmptyReason.FILTERED_OUT -> R.string.map_empty_filtered
        EmptyReason.NOTHING_NEARBY -> R.string.map_empty_nearby
        EmptyReason.OFFLINE -> R.string.map_empty_offline
        EmptyReason.NONE -> return
    }

    Text(
        text = stringResource(message),
        modifier = modifier
            .padding(MageRideTheme.spacing.sm)
            .background(
                MaterialTheme.colorScheme.surfaceVariant,
                RoundedCornerShape(MageRideTheme.radius.md),
            )
            .padding(horizontal = MageRideTheme.spacing.sm, vertical = MageRideTheme.spacing.xs),
        style = MaterialTheme.typography.bodyMedium,
        color = MaterialTheme.colorScheme.onSurfaceVariant,
    )
}
