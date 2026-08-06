package lk.mageride.passenger.onboarding

import androidx.annotation.StringRes
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.CreditCard
import androidx.compose.material.icons.outlined.DirectionsBus
import androidx.compose.material.icons.outlined.Map
import androidx.compose.ui.graphics.vector.ImageVector
import lk.mageride.passenger.R
import lk.mageride.shared.data.models.Language
import lk.mageride.shared.data.models.content.OnboardingSlide

/**
 * One slide of SCR-PA-002's carousel, ready to draw.
 *
 * Deliberately not `OnboardingSlide`: that is content-svc's wire shape, carrying a
 * [lk.mageride.shared.data.models.content.TrilingualText] per field and an illustration
 * *reference*. This is what the screen needs after a language has been chosen — one string per
 * field, and an icon it can actually render.
 *
 * @property title The headline.
 * @property body One sentence under it.
 * @property caption The illustration panel's own label.
 * @property icon Stands in for the illustration — see [FeatureSlides].
 */
internal data class FeatureSlide(val title: String, val body: String, val caption: String, val icon: ImageVector)

/** The same three slides before a language is known — the offline fallback's shape. */
internal data class LocalSlide(
    @param:StringRes val title: Int,
    @param:StringRes val body: Int,
    @param:StringRes val caption: Int,
    val icon: ImageVector,
)

/**
 * US-1.2's **three-slide tutorial**, and where its copy comes from.
 *
 * **The slides are content-svc's** — `GET /v1/content/onboarding/passenger` (AL-28, BR-25.1),
 * public and unauthenticated because it is drawn on a screen that runs before sign-in. All three
 * languages arrive in one answer precisely because the language picker is on this same screen, so
 * switching to සිංහල re-renders from the response rather than re-fetching.
 *
 * **[Fallback] is what the screen draws when that call does not land**, and it is not a
 * placeholder: first launch is exactly when a passenger is most likely to be on a bad connection,
 * and a blank carousel above the language boxes would look broken. The copy is trilingual in
 * `strings.xml` like everything else, so the fallback obeys the same rule the server's copy does.
 *
 * **The illustration is an icon, and that is a dependency wall.** `OnboardingSlide.illustrationRef`
 * is *"an app-bundled asset key, or an absolute https URL"* — content-svc serves the reference and
 * never image bytes — and this module ships no image loader, so a URL has nothing to render it.
 * The per-slot icon below is the honest stand-in. See the C077 handoff.
 */
internal object FeatureSlides {

    /** How many slides SCR-PA-002 shows, whatever content-svc returns. */
    const val COUNT = 3

    /**
     * The bundled copy, in slot order.
     *
     * The three things a passenger has to understand before they pick a language: the map is live,
     * public transport is on it beside the hires, and paying is not a card-only affair.
     */
    val Fallback: List<LocalSlide> = listOf(
        LocalSlide(
            title = R.string.onboarding_slide_map_title,
            body = R.string.onboarding_slide_map_body,
            caption = R.string.onboarding_slide_map_caption,
            icon = Icons.Outlined.Map,
        ),
        LocalSlide(
            title = R.string.onboarding_slide_transit_title,
            body = R.string.onboarding_slide_transit_body,
            caption = R.string.onboarding_slide_transit_caption,
            icon = Icons.Outlined.DirectionsBus,
        ),
        LocalSlide(
            title = R.string.onboarding_slide_pay_title,
            body = R.string.onboarding_slide_pay_body,
            caption = R.string.onboarding_slide_pay_caption,
            icon = Icons.Outlined.CreditCard,
        ),
    )

    /**
     * The icon for a slide, by its 1-based [OnboardingSlide.slot].
     *
     * Falls back to the map for a slot content-svc invents beyond the three: a slide with no icon
     * would draw an empty panel, and a fourth slide is a content change rather than an error.
     */
    fun iconForSlot(slot: Int): ImageVector = Fallback.getOrNull(slot - 1)?.icon ?: Fallback.first().icon

    /** One server slide, resolved into [language]. */
    fun resolve(slide: OnboardingSlide, language: Language): FeatureSlide = FeatureSlide(
        title = slide.title[language],
        body = slide.body[language],
        // content-svc sends no caption; the reference is what it does send, and naming the asset
        // under the panel is more useful to whoever wires the real illustrations than a blank.
        caption = slide.illustrationRef,
        icon = iconForSlot(slide.slot),
    )
}
