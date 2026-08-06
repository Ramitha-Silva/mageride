package lk.mageride.passenger.onboarding

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.pager.HorizontalPager
import androidx.compose.foundation.pager.rememberPagerState
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import lk.mageride.passenger.R
import lk.mageride.passenger.shell.findActivity
import lk.mageride.passenger.ui.component.MageRideCta
import lk.mageride.passenger.ui.component.MageRideTextLink
import lk.mageride.passenger.ui.component.PagerDots
import lk.mageride.passenger.ui.component.SectionLabel
import lk.mageride.passenger.ui.component.SelectionBox
import lk.mageride.passenger.ui.theme.MageRideTheme

/**
 * SCR-PA-002 — the three-slide carousel and the language picker.
 *
 * **Get Started is pinned to the bottom, below the language list** — the prompt's fence, US-1.3's
 * own wording and the wireframe all say so, and a `Spacer(weight = 1f)` above it is what makes
 * that true at every supported height rather than only on the one the wireframe was drawn at.
 * D2' §SCR-PA-002's ASCII sketch still draws the CTA *above* the picker and the picker as a
 * `SegmentedButton`; the wireframe supersedes both. Recorded in the C077 handoff.
 *
 * **Skip and Get Started are the same action.** Neither skips the language: Sinhala is already the
 * highlighted default (AL-26) and the view model has already stored whatever is selected, so both
 * doors finish onboarding. The carousel is presentation only (BR-25.1) and gates nothing.
 *
 * @param onContinue Move to SCR-PA-003. The shell replaces the back stack — onboarding is a
 *   one-way door.
 */
@Composable
internal fun OnboardingScreen(onContinue: () -> Unit, model: OnboardingViewModel) {
    val state by model.state.collectAsStateWithLifecycle()
    val context = LocalContext.current
    val slides = rememberSlides(state)
    val pager = rememberPagerState(pageCount = { slides.size })

    val finish: () -> Unit = {
        // A language change only reaches `Resources` through `MainActivity.attachBaseContext`, so
        // the Activity is rebuilt before the next screen is drawn — otherwise a passenger who
        // picked සිංහල meets an English login screen. `recreate()` re-enters the graph at the
        // splash, which routes straight back here and then on, now in the chosen language.
        if (model.finish()) {
            // No Activity means a `@Preview`; continuing is the only sensible thing left.
            context.findActivity()?.recreate() ?: onContinue()
        } else {
            onContinue()
        }
    }

    Column(
        modifier = Modifier
            .fillMaxSize()
            .padding(MageRideTheme.spacing.md),
        verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.sm),
    ) {
        Row(modifier = Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End) {
            MageRideTextLink(label = stringResource(R.string.onboarding_skip), onClick = finish)
        }

        HorizontalPager(state = pager, modifier = Modifier.fillMaxWidth()) { page ->
            SlidePage(slides[page])
        }

        PagerDots(
            count = slides.size,
            current = pager.currentPage,
            modifier = Modifier.fillMaxWidth().padding(vertical = MageRideTheme.spacing.xxs),
        )

        SectionLabel(text = stringResource(R.string.onboarding_language_label), centred = true)

        Column(verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs)) {
            // US-1.3's order — Sinhala, Tamil, English — is `LanguageChoices`, not the enum's.
            LanguageChoices.forEach { language ->
                SelectionBox(
                    label = language.endonym,
                    secondary = language.englishName,
                    selected = state.language == language,
                    onSelect = { model.select(language) },
                )
            }
        }

        // The pin. Everything above scrolls into whatever height is left; the CTA does not move.
        Box(modifier = Modifier.weight(1f))

        MageRideCta(label = stringResource(R.string.onboarding_get_started), onClick = finish)
    }
}

/** One page of the carousel. */
@Composable
private fun SlidePage(slide: FeatureSlide) {
    Column(
        modifier = Modifier.fillMaxWidth(),
        verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        lk.mageride.passenger.ui.component.IllustrationPanel(icon = slide.icon, caption = slide.caption)
        Text(
            text = slide.title,
            style = MaterialTheme.typography.headlineMedium,
            color = MaterialTheme.colorScheme.onSurface,
            textAlign = TextAlign.Center,
        )
        Text(
            text = slide.body,
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            textAlign = TextAlign.Center,
        )
    }
}

/**
 * content-svc's slides in the chosen language, or the bundled ones.
 *
 * Resolved here rather than in the view model because the fallback's copy is a **string
 * resource** — `stringResource` is composable, and a `@StringRes` int that had to be resolved in a
 * view model would need a `Context` there. Switching language re-runs this, which is what makes
 * the carousel change with the picker without a second fetch.
 */
@Composable
private fun rememberSlides(state: OnboardingState): List<FeatureSlide> = if (state.slides.isEmpty()) {
    FeatureSlides.Fallback.map {
        FeatureSlide(
            title = stringResource(it.title),
            body = stringResource(it.body),
            caption = stringResource(it.caption),
            icon = it.icon,
        )
    }
} else {
    state.slides.map { FeatureSlides.resolve(it, state.language) }
}
