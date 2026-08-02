package lk.mageride.driver.onboarding

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.res.stringResource
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import lk.mageride.driver.R
import lk.mageride.driver.ui.theme.ControlTokens
import lk.mageride.driver.ui.theme.MageRideTheme
import org.koin.androidx.compose.koinViewModel

/**
 * **SCR-DA-001 · splash** — the app mark on the brand orange, and the boot decision behind it.
 *
 * Full-bleed `primary` with a white rounded mark, the product name and a spinner, exactly as the
 * wireframe draws it. Nothing is tappable: this screen exists for as long as
 * [SplashViewModel] needs and not one frame longer, and it is popped off the back stack the
 * moment it answers — pressing Back from Login must leave the app, not return here.
 */
@Composable
internal fun SplashScreen(onResolved: (OnboardingDestination) -> Unit, modifier: Modifier = Modifier) {
    val viewModel: SplashViewModel = koinViewModel()
    val destination by viewModel.destination.collectAsStateWithLifecycle()

    LaunchedEffect(destination) {
        destination?.let(onResolved)
    }

    Box(
        modifier = modifier
            .fillMaxSize()
            .background(MaterialTheme.colorScheme.primary),
        contentAlignment = Alignment.Center,
    ) {
        Column(
            verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.md, Alignment.CenterVertically),
            horizontalAlignment = Alignment.CenterHorizontally,
        ) {
            Box(
                modifier = Modifier
                    .size(ControlTokens.SplashMark)
                    .background(
                        color = MaterialTheme.colorScheme.onPrimary,
                        shape = RoundedCornerShape(ControlTokens.SplashMarkRadius),
                    ),
                contentAlignment = Alignment.Center,
            ) {
                // The launcher's own vector, tinted back to the brand orange. A `Text("M")` would
                // be a hard-coded user-visible string in a trilingual app and a second copy of a
                // mark that already exists as a drawable.
                Icon(
                    painter = painterResource(R.drawable.ic_launcher_foreground),
                    contentDescription = null,
                    modifier = Modifier.size(ControlTokens.SplashMark),
                    tint = MaterialTheme.colorScheme.primary,
                )
            }

            // `app_name`, not a second copy of it: the wireframe's "MageRide Driver" under the
            // mark is the launcher label, and the two must not be able to disagree.
            Text(
                text = stringResource(R.string.app_name),
                style = MaterialTheme.typography.titleLarge,
                color = MaterialTheme.colorScheme.onPrimary,
            )

            CircularProgressIndicator(
                modifier = Modifier.size(ControlTokens.SplashSpinner),
                color = MaterialTheme.colorScheme.onPrimary,
                trackColor = MaterialTheme.colorScheme.primaryContainer,
            )
        }
    }
}
