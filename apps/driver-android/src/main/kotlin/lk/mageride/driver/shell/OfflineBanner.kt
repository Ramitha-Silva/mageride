package lk.mageride.driver.shell

import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.expandVertically
import androidx.compose.animation.shrinkVertically
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.CloudOff
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import lk.mageride.driver.R
import lk.mageride.driver.ui.theme.MageRideTheme

/**
 * D2' §SCR-DA-032 — *"Top banner 'Connection lost — showing last known' (US-15.6); current screen
 * preserved (not full takeover)."*
 *
 * A `Surface` strip above the content rather than a `Snackbar`: a snackbar times out, and the
 * condition it describes does not. It sits inside the Scaffold's content column so the bottom bar
 * and any bottom sheet stay exactly where they were — the requirement is that the screen is
 * *preserved*, and a banner that pushed the layout around would fail that as surely as a dialog.
 *
 * §SCR-PA-032 adds "auto-clears on reconnect < 5 s" — that is [visible] going false, which is what
 * the monitor emits the moment a validated network is back. No separate timer.
 */
@Composable
internal fun OfflineBanner(visible: Boolean, modifier: Modifier = Modifier) {
    AnimatedVisibility(
        visible = visible,
        enter = expandVertically(),
        exit = shrinkVertically(),
        modifier = modifier,
    ) {
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .background(MageRideTheme.status.warning)
                .padding(horizontal = MageRideTheme.spacing.sm, vertical = MageRideTheme.spacing.xs),
            horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Icon(
                imageVector = Icons.Filled.CloudOff,
                contentDescription = null,
                tint = MageRideTheme.status.onStatus,
                modifier = Modifier.size(18.dp),
            )
            Text(
                text = stringResource(R.string.offline_banner_message),
                style = MaterialTheme.typography.labelMedium,
                color = MageRideTheme.status.onStatus,
            )
        }
    }
}
