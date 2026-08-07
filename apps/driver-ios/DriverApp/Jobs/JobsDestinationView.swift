import SwiftUI

/// C090's four destinations, built from the graph's singletons.
///
/// The one arm ``DriverDestinationView`` gives this cluster, expanded — split out for the same reason
/// ``HomeDestinationView`` is: `body` is an implicit `@ViewBuilder`, so every arm of a `switch` wraps
/// every other arm's type in another `_ConditionalContent`, and four real screens inlined beside
/// eighteen placeholders is a type the compiler struggles to infer at all.
///
/// **All four sit on the Jobs tab**, which is what ``DriverRoute/tab`` already declares: SCR-DI-017
/// is its root and the other three are pushed onto it. That is also true of the two the *dashboard*
/// opens — SCR-DI-019's `L3` badge and SCR-DI-020's *"Today"* line both switch tab and push, which is
/// ``DriverNavigator/open(_:)``'s own rule and needs nothing here.
///
/// `@MainActor` on the whole view, not on its initialiser — see ``HomeDestinationView`` for why.
@MainActor
struct JobsDestinationView: View {

    let route: DriverRoute

    @EnvironmentObject private var graph: DriverGraph
    @EnvironmentObject private var navigator: DriverNavigator

    var body: some View {
        switch route {
        case .jobs:
            JobBoardScreen(
                model: graph.makeJobBoardModel(),
                onOpenScheduled: { navigator.open(.scheduledRides) }
            )

        case .scheduledRides:
            ScheduledRidesScreen(model: graph.makeScheduledRidesModel())

        case .driverLevel:
            DriverLevelScreen(model: graph.makeDriverLevelModel())

        case .earnings:
            EarningsScreen(model: graph.makeEarningsModel())

        default:
            // Unreachable: ``DriverDestinationView`` routes exactly the four cases above here. The
            // arm exists because `DriverRoute` has thirty cases and Swift wants them all accounted
            // for.
            PlaceholderScreen(screen: route.path, route: route)
        }
    }
}
