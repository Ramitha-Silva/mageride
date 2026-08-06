package lk.mageride.passenger.shell

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
import lk.mageride.passenger.R
import lk.mageride.passenger.ui.theme.ControlTokens
import lk.mageride.passenger.ui.theme.MageRideTheme

/**
 * SCR-PA-032 — *"📡 Connection lost — showing last known positions"*.
 *
 * A strip above the content rather than a `Snackbar`: a snackbar times out, and the condition it
 * describes does not. It sits inside the Scaffold's content column so the bottom bar and the map's
 * bottom sheet stay exactly where they were — the requirement is that the screen is *preserved*,
 * and a banner that pushed the layout around would fail that as surely as a dialog would.
 *
 * *"Auto-clears on reconnect < 5 s"* is [visible] going false, which is what the monitor emits the
 * moment a validated network is back. No separate timer. The live plane's own recovery is on the
 * same budget and is `PassengerLiveMap`'s.
 *
 * **The colour is M3's `errorContainer` pair, and that is a deliberate small deviation.** The
 * wireframe's `.banner.err` is `#FBE2E2` on `#8A1B1B`, and D2' §0.2 defines `error` but **no
 * container role for it** — so rather than transcribe two hexes the token table does not contain,
 * this uses M3's baseline error container, which resolves within a shade of the wireframe's value
 * in light and tracks the appearance in dark. The alternative was a solid §0.2 red bar, which is
 * louder than a banner whose own requirement is "not a full takeover".
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
                .background(MaterialTheme.colorScheme.errorContainer)
                .padding(horizontal = MageRideTheme.spacing.sm, vertical = MageRideTheme.spacing.xs),
            horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Icon(
                imageVector = Icons.Filled.CloudOff,
                contentDescription = null,
                tint = MaterialTheme.colorScheme.onErrorContainer,
                modifier = Modifier.size(ControlTokens.BannerIcon),
            )
            Text(
                text = stringResource(R.string.offline_banner_message),
                style = MaterialTheme.typography.labelMedium,
                color = MaterialTheme.colorScheme.onErrorContainer,
            )
        }
    }
}
