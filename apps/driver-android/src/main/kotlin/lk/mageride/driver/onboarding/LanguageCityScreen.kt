package lk.mageride.driver.onboarding

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.pager.HorizontalPager
import androidx.compose.foundation.pager.rememberPagerState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.LocationOn
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import lk.mageride.driver.R
import lk.mageride.driver.ui.component.IllustrationPanel
import lk.mageride.driver.ui.component.MageRideCta
import lk.mageride.driver.ui.component.PagerDots
import lk.mageride.driver.ui.component.SectionLabel
import lk.mageride.driver.ui.component.SelectionBox
import lk.mageride.driver.ui.theme.MageRideTheme
import lk.mageride.shared.data.models.Language
import org.koin.androidx.compose.koinViewModel

/**
 * **SCR-DA-002 · language / city** — first run only.
 *
 * Top to bottom, exactly as the wireframe draws it: a "Welcome" app bar, the AL-28 three-slide
 * carousel with its paging dots, the AL-26 vertical language boxes (**Sinhala first and
 * selected**), the AL-27 operating-city radio list loaded from `config.operating_cities`, and the
 * Continue CTA pinned under a spacer.
 *
 * Choosing a language re-inflates the app's resources, so Continue may recreate the Activity; the
 * splash then routes on to Login, which is where this screen was going anyway.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun LanguageCityScreen(onContinue: () -> Unit, modifier: Modifier = Modifier) {
    val viewModel: LanguageCityViewModel = koinViewModel()
    val state by viewModel.state.collectAsStateWithLifecycle()
    val context = LocalContext.current

    Scaffold(
        modifier = modifier.fillMaxSize(),
        topBar = { TopAppBar(title = { Text(text = stringResource(R.string.onboarding_welcome_title)) }) },
    ) { insets ->
        Column(
            modifier = Modifier
                .padding(insets)
                .fillMaxSize()
                .verticalScroll(rememberScrollState())
                .padding(MageRideTheme.spacing.md),
            verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.sm),
        ) {
            FeatureCarousel()

            SectionLabel(text = stringResource(R.string.onboarding_language_label))
            state.languages.forEach { language ->
                SelectionBox(
                    label = language.endonym,
                    secondary = language.englishName,
                    selected = language == state.language,
                    onSelect = { viewModel.selectLanguage(language) },
                )
            }

            SectionLabel(text = stringResource(R.string.onboarding_city_label))
            CityPicker(state = state, onSelect = viewModel::selectCity, onRetry = viewModel::loadCities)

            MageRideCta(
                label = stringResource(R.string.action_continue),
                enabled = state.canContinue,
                onClick = {
                    // Navigate FIRST, then rebuild — in that order, always.
                    //
                    // Resources are resolved once per configuration, so a language chosen after
                    // that has no effect until the Activity is rebuilt. But `recreate()` does NOT
                    // send the app back through the splash: `rememberNavController` saves its back
                    // stack through `rememberSaveable` and restores it, so the app returns to
                    // whatever was on top — which, if we recreated without moving first, was this
                    // screen. Continue rebuilt SCR-DA-002 and went nowhere. Moving to Login first
                    // is what puts Login in the state that gets restored.
                    val languageChanged = viewModel.confirm()
                    onContinue()
                    // No Activity means a `@Preview`; the navigation above is then all there is.
                    if (languageChanged) context.findActivity()?.recreate()
                },
                modifier = Modifier.padding(top = MageRideTheme.spacing.xs),
            )
        }
    }
}

/**
 * AL-28's carousel — three client-paged slides, swipeable, with dots.
 *
 * `HorizontalPager` is D2' §B's own Android delta for this screen. Client-paged and
 * **presentation only**: BR-25.1 is explicit that nothing here gates anything, so a driver who
 * never swipes still reaches the selectors below.
 */
@Composable
private fun FeatureCarousel(modifier: Modifier = Modifier) {
    val slides = FeatureSlides.All
    val pager = rememberPagerState(pageCount = slides::size)

    Column(
        modifier = modifier.fillMaxWidth(),
        verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        HorizontalPager(state = pager, modifier = Modifier.fillMaxWidth()) { page ->
            val slide = slides[page]
            Column(
                modifier = Modifier.fillMaxWidth(),
                verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
                horizontalAlignment = Alignment.CenterHorizontally,
            ) {
                IllustrationPanel(icon = slide.icon, caption = stringResource(slide.caption))
                Text(
                    text = stringResource(slide.title),
                    style = MaterialTheme.typography.titleLarge,
                    color = MaterialTheme.colorScheme.onSurface,
                    textAlign = TextAlign.Center,
                )
                Text(
                    text = stringResource(slide.body),
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    textAlign = TextAlign.Center,
                )
            }
        }
        PagerDots(count = slides.size, current = pager.currentPage)
    }
}

/**
 * The city radio list, and the two states a network-loaded list has that a hard-coded one does
 * not.
 *
 * A failure is offered as Retry rather than swallowed into an empty list: an empty city picker and
 * an unreachable gateway look identical to a driver, and only one of them is worth waiting out.
 */
@Composable
private fun CityPicker(
    state: LanguageCityState,
    onSelect: (String) -> Unit,
    onRetry: () -> Unit,
    modifier: Modifier = Modifier,
) {
    Column(
        modifier = modifier.fillMaxWidth(),
        verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
    ) {
        when {
            state.loadingCities -> Box(
                modifier = Modifier.fillMaxWidth(),
                contentAlignment = Alignment.Center,
            ) {
                CircularProgressIndicator()
            }

            state.citiesFailed -> {
                Text(
                    text = stringResource(R.string.onboarding_city_load_failed),
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.error,
                )
                TextButton(onClick = onRetry) { Text(text = stringResource(R.string.action_retry)) }
            }

            else -> state.cities.forEach { city ->
                SelectionBox(
                    label = city.name(state.language),
                    selected = city.code == state.cityCode,
                    onSelect = { onSelect(city.code) },
                    leading = Icons.Outlined.LocationOn,
                )
            }
        }
    }
}

/**
 * The language's own name in its own script — `සිංහල`, `தமிழ்`, `English`.
 *
 * **Deliberately not a string resource.** An endonym is the same string in all three locales, so
 * the three `strings.xml` files would carry three identical values — which `StringResourceTest`
 * correctly rejects as "a key copied into si and ta and never translated". It is the same
 * argument `OperatingCity`'s own KDoc makes about city names: a proper noun is **data**, not
 * copy. Whoever is looking for their language is looking for the script they read.
 *
 * `internal` since C074: SCR-DA-029's language row offers the same three choices, and a second
 * copy of the table is how `සිංහල` ends up spelled two ways.
 */
internal val Language.endonym: String
    get() = when (this) {
        Language.SI -> "සිංහල"
        Language.TA -> "தமிழ்"
        Language.EN -> "English"
    }

/**
 * The English gloss beside it, so a driver who reads neither other script can still navigate.
 *
 * `null` for English, where the gloss would repeat the endonym — which is exactly what the
 * wireframe draws: `සිංහල Sinhala`, `தமிழ் Tamil`, and a bare `English`.
 */
internal val Language.englishName: String?
    get() = when (this) {
        Language.SI -> "Sinhala"
        Language.TA -> "Tamil"
        Language.EN -> null
    }
