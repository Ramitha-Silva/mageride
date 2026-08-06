package lk.mageride.passenger.onboarding

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import lk.mageride.passenger.R
import lk.mageride.passenger.nav.PassengerRoute
import lk.mageride.passenger.ui.theme.ControlTokens
import lk.mageride.passenger.ui.theme.MageRideTheme
import org.koin.androidx.compose.koinViewModel

/**
 * SCR-PA-001 — the splash.
 *
 * The wireframe: the white app mark on a full-bleed `primary` screen, the wordmark under it, and
 * an indeterminate loader. **Loading is its only state** — every branch it can take is a
 * navigation, and there is nothing here for a passenger to do.
 *
 * @param onResolved Where the router decided to go. The shell replaces the whole back stack, so
 *   Back from the next screen leaves the app rather than returning to a spinner.
 */
@Composable
internal fun SplashScreen(onResolved: (PassengerRoute) -> Unit, model: SplashViewModel = koinViewModel()) {
    val route by model.route.collectAsStateWithLifecycle()

    LaunchedEffect(route) {
        route?.let(onResolved)
    }

    Box(
        modifier = Modifier
            .fillMaxSize()
            .background(MaterialTheme.colorScheme.primary),
        contentAlignment = Alignment.Center,
    ) {
        Column(
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.md),
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
                Text(
                    // The app mark, not copy — a single Latin glyph in every locale, which is why
                    // it is not in `strings.xml`. The same mark `ic_launcher_foreground` draws.
                    text = APP_MARK,
                    style = MaterialTheme.typography.displaySmall,
                    color = MaterialTheme.colorScheme.primary,
                    textAlign = TextAlign.Center,
                )
            }
            Text(
                text = stringResource(R.string.app_name),
                style = MaterialTheme.typography.headlineMedium,
                color = MaterialTheme.colorScheme.onPrimary,
            )
            CircularProgressIndicator(
                modifier = Modifier.size(ControlTokens.SplashSpinner),
                color = MaterialTheme.colorScheme.onPrimary,
                trackColor = MaterialTheme.colorScheme.primaryContainer,
                strokeWidth = 3.dp,
            )
        }
    }
}

private const val APP_MARK = "M"
