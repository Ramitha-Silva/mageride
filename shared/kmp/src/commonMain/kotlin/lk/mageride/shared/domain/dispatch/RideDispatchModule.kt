package lk.mageride.shared.domain.dispatch

import lk.mageride.shared.data.api.ride.RideApi
import org.koin.core.module.Module
import org.koin.dsl.module

/**
 * The C015 slice of the Koin graph.
 *
 * **Deliberately small.** Almost all of Mode C's client-side logic is pure — [RideOffer],
 * [lk.mageride.shared.domain.ride.RideTransitions],
 * [lk.mageride.shared.domain.ride.CancellationMatrix], [DirectionalPredicate], [JobBoard] — and a
 * value type built from a server-supplied config is not something a DI graph should be holding a
 * stale copy of. [DirectionalPredicate] in particular takes the singleton
 * `dispatch.directional_config` row: binding one at start-up would pin whatever the thresholds
 * were the first time the app launched, which is precisely the failure the DoD's "never hardcoded"
 * is aimed at. Construct those at the call site from the config just read.
 *
 * What does belong here is the one object with a dependency and a lifetime: [OfferSession] holds
 * the driver's single offer slot (ADD Appendix B.2 invariant 3) and needs
 * [lk.mageride.shared.data.api.ride.RideApi]. One per app process, like the slot it mirrors.
 *
 * [RideApi] is resolved lazily for C014's reason: the graph is complete by the time an offer
 * arrives and is not while it is being built.
 *
 * **This module needs nothing from the app** beyond what C013 already asks for — an
 * `HttpClientEngine` and an `ApiConfig`.
 */
public val rideDispatchModule: Module = module {
    single {
        val scope = this
        OfferSession(api = { scope.get<RideApi>() })
    }
}
