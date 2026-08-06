package lk.mageride.passenger.settings

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Person
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import lk.mageride.passenger.R
import lk.mageride.passenger.ui.theme.ControlTokens
import lk.mageride.passenger.ui.theme.MageRideTheme
import org.koin.compose.koinInject

/**
 * SCR-PA-033's identity block — the `primaryContainer` panel at the top of the drawer.
 *
 * The wireframe draws an avatar disc, the passenger's name and `PAX-90431 · +94 77 123 4567`. The
 * shell owns the drawer and its rows and left this as a slot with a brand-only default, because a
 * name and a phone number come from `GET /v1/users/me` and that is a screen's data layer;
 * `PassengerShell` passes this composable in and changes nothing else.
 *
 * **The id is the account's ULID.** No contract carries a human-readable passenger number — see
 * `SettingsScreen`'s profile card, which prints the same pair and argues the same point.
 *
 * **It renders brand-only until there is a profile, and never renders a placeholder name.** A
 * greyed-out *"Your name"* in the shape of the real thing is how a half-loaded drawer ships looking
 * finished; the shell's original default made the same call.
 */
@Composable
internal fun PassengerDrawerHeader() {
    val identity = koinInject<PassengerIdentity>()
    val profile by identity.profile.collectAsStateWithLifecycle()

    // Only when nothing has been read at all — SCR-PA-027 and SCR-PA-027b both hand their profile
    // over, so a passenger who has opened either has already filled this in.
    LaunchedEffect(identity) { identity.refresh() }

    Column(
        modifier = Modifier
            .fillMaxWidth()
            .background(MaterialTheme.colorScheme.primaryContainer)
            .padding(horizontal = MageRideTheme.spacing.md, vertical = MageRideTheme.spacing.md),
        verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
    ) {
        Box(
            modifier = Modifier
                .size(ControlTokens.Avatar)
                .background(MaterialTheme.colorScheme.background, CircleShape),
            contentAlignment = Alignment.Center,
        ) {
            Icon(
                imageVector = Icons.Filled.Person,
                contentDescription = null,
                tint = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.size(ControlTokens.RowIcon),
            )
        }

        Text(
            text = profile?.firstName?.takeIf(String::isNotBlank) ?: stringResource(R.string.app_name),
            style = MaterialTheme.typography.titleMedium,
            color = MaterialTheme.colorScheme.onPrimaryContainer,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )

        profile?.let {
            Text(
                text = stringResource(R.string.settings_identity, it.userId, it.phone),
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onPrimaryContainer,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
        }
    }
}
