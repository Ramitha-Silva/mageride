package lk.mageride.driver.ui.component

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.Person
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import coil3.compose.AsyncImage
import lk.mageride.driver.R
import lk.mageride.driver.home.DashboardLabels
import lk.mageride.driver.ui.Symbols
import lk.mageride.driver.ui.theme.ControlTokens
import lk.mageride.driver.ui.theme.MageRideTheme
import java.util.Locale

/**
 * The avatar, the name and level, and the vehicle and rating line.
 *
 * **One composable for two screens, deliberately.** The wireframes draw the same block at the top
 * of the menu drawer and the top of the profile screen, and before this they were two independent
 * pieces of layout that had already drifted: the menu showed the app's name where the driver's
 * should be, and the profile showed the driver's platform id — a value SCR-DA-023 exists to let
 * them read, but not one that answers "who am I and what am I driving".
 *
 * @param trailing The row's own affordance — the profile screen's ✎, nothing on the menu.
 */
@Composable
internal fun DriverHeader(
    state: DriverHeaderState,
    modifier: Modifier = Modifier,
    trailing: @Composable (() -> Unit)? = null,
) {
    Row(
        modifier = modifier.fillMaxWidth(),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
    ) {
        Surface(
            modifier = Modifier.size(ControlTokens.AvatarSmall),
            shape = CircleShape,
            color = MaterialTheme.colorScheme.primaryContainer,
        ) {
            Box(contentAlignment = Alignment.Center) {
                // The glyph is drawn first and the photograph over it, so it is also what shows
                // while the load is in flight and what is left if it fails. `SubcomposeAsyncImage`
                // would give explicit loading and error slots; this needs neither, because the
                // right thing to draw in both states is the thing already underneath.
                Icon(
                    imageVector = Icons.Outlined.Person,
                    contentDescription = null,
                    modifier = Modifier.size(ControlTokens.RowIcon),
                    tint = MaterialTheme.colorScheme.onPrimaryContainer,
                )

                if (state.photoUrl != null) {
                    AsyncImage(
                        model = state.photoUrl,
                        // Decorative: the driver's name is on the very next line, and a screen
                        // reader announcing "photo of Nimal" before reading "Nimal" is noise.
                        contentDescription = null,
                        modifier = Modifier.fillMaxSize(),
                        // The stored photograph is whatever shape the handset camera took; the
                        // wireframe's avatar is a circle. Crop rather than fit, or a portrait is
                        // letterboxed into a disc with the driver's face in a band across it.
                        contentScale = ContentScale.Crop,
                    )
                }
            }
        }

        Column(modifier = Modifier.weight(1f)) {
            Row(
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xxs),
            ) {
                Text(
                    text = state.name ?: stringResource(R.string.profile_unnamed),
                    style = MaterialTheme.typography.titleMedium,
                    color = MaterialTheme.colorScheme.onSurface,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.weight(1f, fill = false),
                )

                // Only when the level read has answered. `L1` is a real level and also what a
                // defaulted `Int` would print, so a placeholder here would tell a Level 3 driver
                // they were Level 1 for as long as the request was in flight.
                state.level?.let { level ->
                    // `DashboardLabels.level`, not a second copy: "L3" is a Driver Level
                    // identifier and not a sentence, and the dashboard already owns the one
                    // definition of how it is spelled.
                    SolidBadge(label = DashboardLabels.level(level), accent = MaterialTheme.colorScheme.primary)
                }
            }

            Text(
                text = secondLine(state),
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
        }

        trailing?.invoke()
    }
}

/**
 * Who the driver is, as both SCR-DA-029 and SCR-DA-036 show it (Δ MCS-24).
 *
 * @property name The driver's own name. `null` renders the "not set yet" copy rather than a blank.
 * @property level Driver Level 1–3 (D5' §4.2). `null` while the read is in flight.
 * @property registration The plate of the vehicle this handset is live for (D-03), or `null` when
 *   nothing is eligible — a driver who has not onboarded one, or whose only vehicle is suspended.
 * @property rating The star average out of 5.
 *
 *   **Always `null` today, and that is a spec gap rather than an omission.** No app-facing read
 *   carries a driver's own average: `GET /v1/users/me` has no such field, `GET /v1/drivers/{id}/level`
 *   answers points and not stars, `trips.ratings` has no aggregate read, and the only place a
 *   driver `rating` appears anywhere on the surface is `RideDetail.driver.rating` — the number a
 *   *passenger* is shown about the driver of *their* ride. C070's menu header and C074's profile
 *   card each reached that conclusion independently and each drew an em dash. This carries the
 *   parameter so that the day a read exists, one type gains a value and both screens show it.
 * @property photoUrl The driver's own photograph, absolute and ready to load (Δ MCS-25).
 *
 *   **This is registry-svc's, not `GET /v1/users/me`'s.** It used to be a `hasPhoto` flag fed from
 *   `UserProfile.photoUrl`, and that field is null for every driver who onboarded in this app:
 *   Profile Setup writes `registry.driver_profiles` and never touches `iam.users` (D3'
 *   §`getDriverProfile`). The photograph AL-27 *requires* is the one registry-svc stored, and it
 *   now arrives as a signed link a loader can follow with no credential of its own.
 *
 *   `null` draws the glyph, which is the state before the read answers and for a driver whose photo
 *   PDPA erasure has cleared.
 */
internal data class DriverHeaderState(
    val name: String? = null,
    val level: Int? = null,
    val registration: String? = null,
    val rating: Double? = null,
    val photoUrl: String? = null,
)

/**
 * The plate and the rating, joined by the separator the rest of this app uses.
 *
 * A driver with no live vehicle gets copy saying so rather than an empty line: "no vehicle yet" is
 * the state that sends them to SCR-DA-026a, and a blank row would read as a value still loading.
 */
@Composable
private fun secondLine(state: DriverHeaderState): String {
    val vehicle = state.registration ?: stringResource(R.string.driver_header_no_vehicle)
    val rating = state.rating ?: return vehicle

    return "$vehicle ${Symbols.DOT} ${Symbols.STAR_FILLED} ${stars(rating)}"
}

/** One decimal place, `4.8`. A rating is a number, not copy — the same rule as `L3`. */
private fun stars(rating: Double): String = String.format(Locale.ROOT, "%.1f", rating)
