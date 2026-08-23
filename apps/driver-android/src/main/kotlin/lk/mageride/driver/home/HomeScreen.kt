package lk.mageride.driver.home

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.EvStation
import androidx.compose.material.icons.outlined.Payments
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.layout.SubcomposeLayout
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.Constraints
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import lk.mageride.driver.R
import lk.mageride.driver.ui.MoneyFormat
import lk.mageride.driver.ui.component.DashboardBanner
import lk.mageride.driver.ui.component.SolidBadge
import lk.mageride.driver.ui.theme.ControlTokens
import lk.mageride.driver.ui.theme.MageRideTheme
import lk.mageride.shared.data.models.ServiceMode
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.subscription.DailyFeeDayStatus
import lk.mageride.shared.domain.wallet.WalletAlert
import org.koin.androidx.compose.koinViewModel

/**
 * **Home** — SCR-DA-010 when the live vehicle is Mode C, SCR-DA-011 when it is Mode A or Mode B.
 *
 * One destination, because D2' makes them one: SCR-DA-012 was merged into SCR-DA-010 and SCR-DA-011
 * *"IS the driver's home dashboard"* for a bus or a private-transport vehicle. What differs between
 * them is the sheet and the header; the map, the banners, the bottom navigation and the offer
 * takeover are shared, and a chooser in front of two destinations would have to keep all four in
 * step.
 *
 * **SCR-DA-014 arrives here as a takeover, not as a route** — a `Dialog` sized to the whole window,
 * with back disabled. That is what `PushRouter` means by routing a `ride_offer` push to Home: the
 * offer is the dashboard's, and fifteen seconds is not long enough to navigate anywhere.
 *
 * @param onOpenRide SCR-DA-015. Reached three ways — winning an offer, tapping the resume banner,
 *   and a cold start that found a ride already in hand.
 * @param onOpenLevel SCR-DA-019, from the `L3` badge (Δ C072). D2' §SCR-DA-019's traceability row
 *   is *"Driver Level badge | SCR-DA-019 + SCR-DA-010 badge"* — the badge is the level screen's
 *   entry point, and it is the only one the wireframes draw.
 * @param onOpenEarnings SCR-DA-020, from the *"Today: 4 trips · Rs 3,180"* line on the standby
 *   sheet (Δ C072) — that figure **is** the earnings dashboard's headline for the Today window.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun HomeScreen(
    onOpenDirectional: () -> Unit,
    onOpenVehicles: () -> Unit,
    onOpenRide: (Ulid) -> Unit,
    onOpenLevel: () -> Unit,
    onOpenEarnings: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val viewModel: HomeViewModel = koinViewModel()
    val offerModel: OfferViewModel = koinViewModel()
    val state by viewModel.state.collectAsStateWithLifecycle()
    val offer by offerModel.state.collectAsStateWithLifecycle()

    Scaffold(
        modifier = modifier.fillMaxSize(),
        topBar = { HomeTopBar(state = state, onOpenLevel = onOpenLevel) },
    ) { insets ->
        BoxWithConstraints(modifier = Modifier.padding(insets).fillMaxSize()) {
            HomeDashboardLayout(
                viewport = maxHeight,
                modifier = Modifier.fillMaxSize().verticalScroll(rememberScrollState()),
                banners = { HomeBanners(state = state) },
                map = { mapHeight, controlsInset ->
                    Box(modifier = Modifier.fillMaxWidth().height(mapHeight)) {
                        DriverHomeMap(
                            position = state.position,
                            vehicleType = state.vehicles.live?.vehicleType,
                            // Everything past the first screenful, so §0.3's recentre FAB stays on it
                            // rather than at the bottom edge of a map that is off the bottom of the
                            // screen — a control the driver would have to scroll past to reach.
                            controlsBottomInset = controlsInset,
                            modifier = Modifier.fillMaxSize(),
                        )

                        // D2' §SCR-DA-010: "Offline → grey overlay + 'Go online to receive rides'". Only
                        // on the Mode C standby map — a Mode A/B dashboard has no standby to be off.
                        //
                        // The label centres in the whole map, which puts it at `viewport - sheet` on
                        // screen: exactly where the map's bottom edge is today, and on the first
                        // screenful whatever the handset. Arithmetic rather than luck — the map is
                        // a multiple of a height that was itself `viewport - banners - sheet`.
                        if (!state.online && !state.isScheduledMode && !state.loading) {
                            OfflineScrim()
                        }
                    }
                },
                sheet = {
                    if (state.isScheduledMode) {
                        JourneySheet(
                            state = state,
                            onStart = viewModel::startJourney,
                            onEndOrRestart = viewModel::endOrRestartJourney,
                            onChooseRoute = viewModel::chooseRoute,
                            onAutoEndChanged = viewModel::setAutoEndAtDestination,
                        )
                    } else {
                        StandbySheet(
                            state = state,
                            onToggleOnline = viewModel::toggleOnline,
                            onOpenDirectional = onOpenDirectional,
                            onOpenVehicles = onOpenVehicles,
                            onOpenEarnings = onOpenEarnings,
                        )
                    }
                },
            )
        }
    }

    // A ride already in hand outranks everything on this screen — SCR-DA-001's router resumes it,
    // and a driver who closed the app mid-trip must not land on a standby map that says they idle.
    LaunchedEffect(state.activeRideId) {
        state.activeRideId?.let(onOpenRide)
    }

    // US-9.1 — the first trip of the day is free, so the fee note belongs on the SECOND offer and
    // only while the day is still unpaid.
    val fee = state.standing.dailyFee

    OfferTakeover(
        state = offer,
        showFeeNote = fee != null && fee.tripsToday > 0 && fee.status == DailyFeeDayStatus.UNPAID,
        feeAmount = state.standing.dailyRate?.let(MoneyFormat::rupees).orEmpty(),
        directional = state.standing.directional?.active == true,
        onAccept = offerModel::accept,
        onReject = offerModel::reject,
        onFinished = { rideId ->
            offerModel.consumeOutcome()
            viewModel.refresh()
            rideId?.let(onOpenRide)
        },
    )
}

/**
 * The status header — the Driver Level badge and the read-only wallet balance (US-9.7, US-6A.14).
 *
 * The wireframe also prints `★4.8`. **There is no app-facing read for a driver's own rating**:
 * `dispatch.yaml` answers a level and a points total, `RideDriver.rating` is the *passenger's* view
 * of a driver on a ride, and the reputation contract is portal-only (C012). Rendering the points
 * behind a star would be a different number wearing the star's meaning, so the star is absent until
 * a read exists. Recorded as a spec gap in the C070 handoff.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun HomeTopBar(state: HomeState, onOpenLevel: () -> Unit, modifier: Modifier = Modifier) {
    TopAppBar(
        modifier = modifier,
        title = {
            state.standing.level?.let { level ->
                // Δ C072 — the badge is SCR-DA-019's entry point, and the only one D2' names.
                SolidBadge(
                    label = DashboardLabels.level(level),
                    accent = MaterialTheme.colorScheme.primary,
                    modifier = Modifier.clickable(onClick = onOpenLevel),
                )
            }
        },
        actions = {
            val mode = state.vehicles.live?.mode
            Text(
                text = when {
                    mode == ServiceMode.A -> stringResource(R.string.journey_no_fee)
                    state.standing.wallet != null -> MoneyFormat.rupees(state.standing.wallet.availableMinor)
                    else -> ""
                },
                style = MaterialTheme.typography.titleMedium,
                color = MaterialTheme.colorScheme.onSurface,
                modifier = Modifier.padding(end = MageRideTheme.spacing.sm),
            )
        },
        colors = TopAppBarDefaults.topAppBarColors(containerColor = MaterialTheme.colorScheme.background),
    )
}

/**
 * The banner stack: the daily-fee chip (SCR-DA-010), the ignition notice (SCR-DA-011, AL-32), the
 * low-balance nudge (US-9.9) and the 2nd-trip fee warning (US-9.1).
 *
 * The order is the wireframe's — money first, because a driver who cannot accept the next ride
 * needs to know before they read anything else.
 */
@Composable
private fun HomeBanners(state: HomeState, modifier: Modifier = Modifier) {
    Column(modifier = modifier.fillMaxWidth(), verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xxs)) {
        if (!state.isScheduledMode) {
            state.standing.dailyFee?.let { fee ->
                DashboardBanner(
                    text = if (fee.status == DailyFeeDayStatus.PAID) {
                        stringResource(R.string.home_daily_fee_paid, MoneyFormat.rupees(fee.dailyRateMinor))
                    } else if (fee.firstTripFree) {
                        stringResource(R.string.home_daily_fee_first_free, MoneyFormat.rupees(fee.dailyRateMinor))
                    } else {
                        stringResource(R.string.home_daily_fee_due, MoneyFormat.rupees(fee.dailyRateMinor))
                    },
                    accent = if (fee.status == DailyFeeDayStatus.PAID) {
                        MageRideTheme.status.success
                    } else {
                        MageRideTheme.status.warning
                    },
                    icon = Icons.Outlined.Payments,
                )
            }
        }

        // AL-32 — the tracker opened this session on ignition, and the dashboard says so rather
        // than pretending the driver did it. End Journey stays live regardless.
        if (state.isScheduledMode && state.journey.startedByDevice && state.journey.isRunning) {
            DashboardBanner(
                text = stringResource(R.string.journey_ignition_banner),
                accent = MaterialTheme.colorScheme.secondary,
                icon = Icons.Outlined.EvStation,
            )
        }

        if (state.journey.isRestartable) {
            DashboardBanner(
                text = stringResource(R.string.journey_auto_ended),
                accent = MageRideTheme.status.warning,
            )
        }

        (state.walletAlert as? WalletAlert.LowBalance)?.let { alert ->
            DashboardBanner(
                text = stringResource(R.string.home_low_balance, MoneyFormat.rupees(alert.threshold)),
                accent = MageRideTheme.status.warning,
            )
        }

        if (state.standing.cannotAffordNextTrip) {
            DashboardBanner(
                text = stringResource(
                    R.string.home_second_trip_fee,
                    state.standing.dailyRate?.let(MoneyFormat::rupees).orEmpty(),
                ),
                accent = MaterialTheme.colorScheme.error,
            )
        }

        state.error?.let { message ->
            DashboardBanner(text = stringResource(message), accent = MaterialTheme.colorScheme.error)
        }
    }
}

/** The grey overlay an offline standby map wears (D2' §SCR-DA-010). */
@Composable
private fun OfflineScrim(modifier: Modifier = Modifier) {
    Box(
        modifier = modifier
            .fillMaxSize()
            .background(MaterialTheme.colorScheme.scrim.copy(alpha = SCRIM_ALPHA)),
        contentAlignment = Alignment.Center,
    ) {
        Text(
            text = stringResource(R.string.home_offline_hint),
            style = MaterialTheme.typography.titleMedium,
            color = MaterialTheme.colorScheme.inverseOnSurface,
            textAlign = TextAlign.Center,
            modifier = Modifier.padding(MageRideTheme.spacing.lg),
        )
    }
}

/**
 * The codes on this screen that are **data, not copy**.
 *
 * `L3` is a Driver Level identifier, not a sentence: three identical values in the three
 * `strings.xml` files is exactly what `StringResourceTest` (correctly) fails on. Same rule the
 * language endonyms, the `+94` prefix and `Rs` follow (C068).
 */
internal object DashboardLabels {

    /** The Driver Level badge — `L1`…`L3` (US-6A.14). */
    fun level(level: Int): String = "L$level"
}

/** D2' §0.2's scrim opacity for a disabled surface behind a message. */
private const val SCRIM_ALPHA = 0.45f

/**
 * SCR-DA-010's three bands, measured in ONE pass (Δ MCS-26).
 *
 * **Why this is a `SubcomposeLayout` and not a `Column`.** The map's height is defined in terms of
 * the other two — *whatever the viewport has left once the banners and the sheet have had theirs* —
 * and a `Column` cannot express that: the only way to learn a sibling's height there is to let it
 * compose, report through `onSizeChanged`, and recompose. That is a feedback loop with a visible
 * first frame, and the frame it drew was the whole viewport enlarged, because
 * the two heights it subtracts both start at zero. A driver opening the dashboard watched the map
 * fill the screen and then shrink by a third.
 *
 * That first frame used to be reasoned about and accepted — the old height rule treated
 * a zero sheet as *"not measured yet"* and handed the map the plain viewport, on the grounds that
 * MapLibre has no GL surface up that early. It has one by the second frame, and the shrink is what
 * a person actually sees. Reported from a handset.
 *
 * Subcomposing measures the banners and the sheet **first**, computes the remainder, and only then
 * composes the map with a height it will keep. There is no earlier frame to see.
 *
 * @param viewport What one screenful is, from the `BoxWithConstraints` outside the scroll. It
 *   cannot be read in here: this layout is inside a `verticalScroll`, so its own `maxHeight` is
 *   `Infinity` — which is the whole point of the scroll and useless as a share-out.
 * @param map Given its final height and the overscroll past [viewport] that [DriverHomeMap] keeps
 *   its controls out of.
 */
@Composable
private fun HomeDashboardLayout(
    viewport: Dp,
    banners: @Composable () -> Unit,
    sheet: @Composable () -> Unit,
    map: @Composable (mapHeight: Dp, controlsInset: Dp) -> Unit,
    modifier: Modifier = Modifier,
) {
    SubcomposeLayout(modifier) { constraints ->
        // Height-unbounded: both of these are content that takes what it needs, and the map gets
        // what is left. Bounding them by the viewport would make a five-row banner stack fight the
        // sheet for the same pixels.
        val loose = constraints.copy(minHeight = 0, maxHeight = Constraints.Infinity)

        val bannerBands = subcompose(HomeBand.Banners, banners).map { it.measure(loose) }
        val sheetBands = subcompose(HomeBand.Sheet, sheet).map { it.measure(loose) }

        val bannersHeight = bannerBands.sumOf { it.height }
        val sheetHeight = sheetBands.sumOf { it.height }

        val mapHeight = homeMapHeight(viewport)
        val natural = mapHeight

        val mapPx = mapHeight.roundToPx()

        val mapBands = subcompose(HomeBand.Map) { map(mapHeight, mapHeight - natural) }
            .map { it.measure(constraints.copy(minHeight = mapPx, maxHeight = mapPx)) }

        layout(constraints.maxWidth, bannersHeight + mapPx + sheetHeight) {
            var y = 0

            bannerBands.forEach { band ->
                band.placeRelative(0, y)
                y += band.height
            }
            mapBands.forEach { band -> band.placeRelative(0, y) }
            y += mapPx
            sheetBands.forEach { band ->
                band.placeRelative(0, y)
                y += band.height
            }
        }
    }
}

/** The three slots [HomeDashboardLayout] subcomposes, in the order it measures them. */
private enum class HomeBand { Banners, Sheet, Map }

/**
 * How tall SCR-DA-010's map is drawn (Δ MCS-31).
 *
 * **A fraction of the viewport and nothing else — the third attempt at this, and the first that
 * cannot move.** The height was `(viewport − banners − sheet) × 1.5`, and BOTH subtrahends turned
 * out to be driven by data that arrives after the first frame:
 *
 *  * the banner stack is zero to five rows of `standing.dailyFee`, the low-balance threshold and
 *    the ignition and auto-ended notices — removed from the arithmetic in MCS-29;
 *  * and the **sheet is no better, which MCS-29 asserted it was and was wrong about.**
 *    `StandbyDashboard` gates a block on `needsVehicle`, returns early from the vehicle chip until
 *    `vehicles.live` answers, and prints "pending" for earnings until they arrive. It grows as the
 *    reads land and takes the map's height down with it.
 *
 * So a driver watched the map shrink twice, for two different reasons, and reported it again after
 * the first fix. Measuring anything that is still loading is the whole mistake. The viewport is the
 * one quantity on this screen that is settled on the first frame.
 *
 * **0.82 is where the old arithmetic landed once it had settled**, on the 411×891 handset this was
 * reported from: a 240dp sheet leaves 651, and 651 × 1.125 is 732, which is 0.82 of 891. The map
 * looks the way MCS-24 asked for it to look — past the fold, so it reads as a map and the scroll
 * stays discoverable — and gets there without a single frame at any other size.
 *
 * The sheet is not subtracted at all now. It sits below the map inside the scroll and is reached by
 * scrolling, which is what the surrounding `verticalScroll` has been for since MCS-24.
 */
internal fun homeMapHeight(viewport: Dp): Dp =
    (viewport * MAP_VIEWPORT_FRACTION).coerceAtLeast(ControlTokens.HomeMapMinimum)

/**
 * The share of one screenful the map takes.
 *
 * **No spec fixes this** — the wireframe's own `flex:1` is what it replaces. See [homeMapHeight]
 * for where the number comes from and why it is a fraction rather than a measurement.
 */
private const val MAP_VIEWPORT_FRACTION = 0.82f
