package lk.mageride.driver.onboarding

import androidx.annotation.StringRes
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.AccountBalanceWallet
import androidx.compose.material.icons.outlined.Bolt
import androidx.compose.material.icons.outlined.Campaign
import androidx.compose.ui.graphics.vector.ImageVector
import lk.mageride.driver.R

/**
 * One slide of SCR-DA-002's feature carousel.
 *
 * @property title The slide's headline.
 * @property body One sentence under it.
 * @property caption The illustration panel's own label.
 * @property icon Stands in for the illustration — see [FeatureSlides].
 */
internal data class FeatureSlide(
    @param:StringRes val title: Int,
    @param:StringRes val body: Int,
    @param:StringRes val caption: Int,
    val icon: ImageVector,
)

/**
 * AL-28's **3-slide feature infographic**, mirroring the passenger app's SCR-PA-002.
 *
 * BR-25.1: *"a 3-slide feature-infographic carousel (content-svc strings, Si/Ta/En) above the
 * language & city selectors. Presentation only; no gating."* The four features it has to land
 * before a driver picks a language are the ones the wireframe's own slide-1 copy names —
 * onboarding, 15-second dispatch, Directional Travel, and the in-app wallet and daily fee.
 *
 * ### Why the strings are resources rather than content-svc's
 *
 * AL-28 says content-svc serves them, and `backend/contracts/content.yaml` declares no route that
 * does: it carries `GET /v1/config/cities`, the mTLS notification templates and the in-app
 * broadcasts, and nothing else. Shipping the copy as trilingual resources satisfies the *rule*
 * that matters — every string exists in Si, Ta and En, and `StringResourceTest` enforces it — and
 * leaves one list to replace when a route lands. Raised as a micro-change-set in the C068 handoff.
 */
internal object FeatureSlides {

    /** The three slides, in order. The dots and the pager both count from this list. */
    val All: List<FeatureSlide> = listOf(
        FeatureSlide(
            title = R.string.onboarding_slide_earn_title,
            body = R.string.onboarding_slide_earn_body,
            caption = R.string.onboarding_slide_earn_caption,
            icon = Icons.Outlined.Campaign,
        ),
        FeatureSlide(
            title = R.string.onboarding_slide_dispatch_title,
            body = R.string.onboarding_slide_dispatch_body,
            caption = R.string.onboarding_slide_dispatch_caption,
            icon = Icons.Outlined.Bolt,
        ),
        FeatureSlide(
            title = R.string.onboarding_slide_wallet_title,
            body = R.string.onboarding_slide_wallet_body,
            caption = R.string.onboarding_slide_wallet_caption,
            icon = Icons.Outlined.AccountBalanceWallet,
        ),
    )
}
