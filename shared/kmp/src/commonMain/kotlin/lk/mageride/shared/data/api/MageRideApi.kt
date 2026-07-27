package lk.mageride.shared.data.api

import lk.mageride.shared.data.api.comms.KtorNotificationApi
import lk.mageride.shared.data.api.comms.KtorVoipApi
import lk.mageride.shared.data.api.comms.NotificationApi
import lk.mageride.shared.data.api.comms.VoipApi
import lk.mageride.shared.data.api.content.ContentApi
import lk.mageride.shared.data.api.content.KtorContentApi
import lk.mageride.shared.data.api.dispatch.DispatchApi
import lk.mageride.shared.data.api.dispatch.KtorDispatchApi
import lk.mageride.shared.data.api.fare.FareApi
import lk.mageride.shared.data.api.fare.KtorFareApi
import lk.mageride.shared.data.api.iam.IamApi
import lk.mageride.shared.data.api.iam.KtorIamApi
import lk.mageride.shared.data.api.query.KtorQueryApi
import lk.mageride.shared.data.api.query.QueryApi
import lk.mageride.shared.data.api.registry.KtorRegistryApi
import lk.mageride.shared.data.api.registry.RegistryApi
import lk.mageride.shared.data.api.ride.KtorRideApi
import lk.mageride.shared.data.api.ride.RideApi
import lk.mageride.shared.data.api.safety.KtorSafetyApi
import lk.mageride.shared.data.api.safety.SafetyApi
import lk.mageride.shared.data.api.subscription.KtorSubscriptionApi
import lk.mageride.shared.data.api.subscription.SubscriptionApi
import lk.mageride.shared.data.api.support.KtorSupportApi
import lk.mageride.shared.data.api.support.SupportApi
import lk.mageride.shared.data.api.transit.KtorTransitApi
import lk.mageride.shared.data.api.transit.TransitApi
import lk.mageride.shared.data.api.trip.KtorTripStateApi
import lk.mageride.shared.data.api.trip.TripStateApi
import lk.mageride.shared.data.api.version.KtorVersionApi
import lk.mageride.shared.data.api.version.VersionApi
import lk.mageride.shared.data.api.wallet.KtorWalletApi
import lk.mageride.shared.data.api.wallet.WalletApi

/**
 * Every typed client, built over one [ApiTransport].
 *
 * One object rather than sixteen constructor parameters: an app injects this and reads
 * `api.ride`, and Swift gets `api.ride` too rather than sixteen separately-registered Koin
 * lookups. The individual interfaces are *also* bound in [apiModule], so a view model that needs
 * only [RideApi] can ask for exactly that and be handed a fake in a test.
 *
 * There is deliberately no per-service configuration here. Every client shares the transport,
 * which means every call shares the credential, the version-gate headers, the retry policy and
 * the breaker — the conventions in D3' §0 apply to all 176 operations or they apply to none.
 */
public class MageRideApi(public val transport: ApiTransport, signals: MageRideApiSignals = MageRideApiSignals()) {

    /** iam-svc — auth, profile, session, saved addresses. */
    public val iam: IamApi = KtorIamApi(transport)

    /** registry-svc — driver identity, vehicles, onboarding, sharing. */
    public val registry: RegistryApi = KtorRegistryApi(transport)

    /** trip-state-svc — Mode A/B tracking sessions. */
    public val tripState: TripStateApi = KtorTripStateApi(transport)

    /** ride-svc — the Mode C ride aggregate. */
    public val ride: RideApi = KtorRideApi(transport)

    /** dispatch-svc — presence, Directional, Job Board, Driver Level. */
    public val dispatch: DispatchApi = KtorDispatchApi(transport)

    /** fare-svc — estimates, final fare, payments. */
    public val fare: FareApi = KtorFareApi(transport)

    /** subscription-svc — daily fee, credit, vouchers, Mode B subscriptions. */
    public val subscription: SubscriptionApi = KtorSubscriptionApi(transport)

    /** wallet-svc — balance, ledger, transfers, top-ups. */
    public val wallet: WalletApi = KtorWalletApi(transport)

    /** query-svc — nearby, trips, earnings, geocoding. */
    public val query: QueryApi = KtorQueryApi(transport)

    /** transit-svc — GTFS planning and the Dataset Manager. */
    public val transit: TransitApi = KtorTransitApi(transport)

    /** safety-svc — SOS, trip share, reports, blocks. */
    public val safety: SafetyApi = KtorSafetyApi(transport)

    /** support-svc — FAQ and tickets. */
    public val support: SupportApi = KtorSupportApi(transport)

    /** content-svc — cities, templates, broadcasts. */
    public val content: ContentApi = KtorContentApi(transport)

    /** voip-svc — call signalling. */
    public val voip: VoipApi = KtorVoipApi(transport)

    /** notification-svc — push tokens and preferences. */
    public val notification: NotificationApi = KtorNotificationApi(transport)

    /** version-check — the D-31 cold-start gate. */
    public val version: VersionApi = KtorVersionApi(transport, signals)
}
