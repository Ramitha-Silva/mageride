package lk.mageride.driver.home

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
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
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import lk.mageride.driver.R
import lk.mageride.driver.ui.MoneyFormat
import lk.mageride.driver.ui.component.DashboardBanner
import lk.mageride.driver.ui.component.SolidBadge
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
        Column(modifier = Modifier.padding(insets).fillMaxSize()) {
            HomeBanners(state = state)

            Box(modifier = Modifier.weight(1f)) {
                DriverHomeMap(
                    position = state.position,
                    vehicleType = state.vehicles.live?.vehicleType,
                    modifier = Modifier.fillMaxSize(),
                )

                // D2' §SCR-DA-010: "Offline → grey overlay + 'Go online to receive rides'". Only
                // on the Mode C standby map — a Mode A/B dashboard has no standby to be off.
                if (!state.online && !state.isScheduledMode && !state.loading) {
                    OfflineScrim()
                }
            }

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
