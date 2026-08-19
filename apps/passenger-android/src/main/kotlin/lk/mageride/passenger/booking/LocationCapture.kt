package lk.mageride.passenger.booking

import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.rememberScrollState
import androidx.compose.material3.FilterChip
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import lk.mageride.passenger.R
import lk.mageride.passenger.ui.theme.MageRideTheme
import lk.mageride.shared.data.models.Place

/**
 * The Search / Map / Paste link / Request control, shared by SCR-PA-010b and SCR-PA-012.
 *
 * One composable rather than three copies, because the *set of methods* is a rule and not a layout:
 * the fence says a package **pickup** offers three and a **drop-off** offers four, and expressing
 * that as `methods = …` at two call sites is what makes it checkable. See [PackageEnd] for why the
 * two ends differ.
 *
 * @param methods Which of the four this field offers, in the wireframe's order.
 */
@Composable
internal fun LocationMethodRow(
    methods: List<PickupMethod>,
    selected: PickupMethod,
    onSelect: (PickupMethod) -> Unit,
    modifier: Modifier = Modifier,
) {
    Row(
        modifier = modifier
            .fillMaxWidth()
            .horizontalScroll(rememberScrollState()),
        horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
    ) {
        methods.forEach { method ->
            FilterChip(
                selected = method == selected,
                onClick = { onSelect(method) },
                label = { Text(stringResource(method.label())) },
            )
        }
    }
}

/** The wireframe's own labels for the four. */
internal fun PickupMethod.label(): Int = when (this) {
    PickupMethod.SEARCH -> R.string.capture_search
    PickupMethod.MAP -> R.string.capture_map
    PickupMethod.PASTE_LINK -> R.string.capture_paste
    PickupMethod.REQUEST -> R.string.capture_request
}

/**
 * What a captured place reads as: its **name**, or the coordinates it was pinned at.
 *
 * A place with no address is not a place with no answer — a pin dropped on an unnamed lane is
 * exactly what the Map method is for, and *"Pinned at 6.92710, 79.86120"* is a passenger telling a
 * driver where to come. Every screen that shows a captured place goes through here, so SCR-PA-009's
 * summary and SCR-PA-010b's row cannot drift apart.
 */
@Composable
internal fun placeLabel(place: Place): String = place.address?.takeIf(String::isNotBlank)
    ?: stringResource(R.string.capture_pinned, place.lat, place.lng)

/** What a captured place reads as under its control, or the prompt when there is none yet. */
@Composable
internal fun CapturedPlace(place: Place?, emptyLabel: String, modifier: Modifier = Modifier) {
    Text(
        text = place?.let { placeLabel(it) } ?: emptyLabel,
        modifier = modifier.fillMaxWidth(),
        style = MaterialTheme.typography.bodyMedium,
        color = if (place == null) {
            MaterialTheme.colorScheme.onSurfaceVariant
        } else {
            MaterialTheme.colorScheme.onSurface
        },
    )
}

/** SCR-PA-012's pickup: three methods. There is nobody standing there to ask (see [PackageEnd]). */
internal val PACKAGE_PICKUP_METHODS = listOf(PickupMethod.SEARCH, PickupMethod.MAP, PickupMethod.PASTE_LINK)

/** SCR-PA-012's drop-off, and SCR-PA-010b's pickup: all four. */
internal val ALL_METHODS = PickupMethod.entries
