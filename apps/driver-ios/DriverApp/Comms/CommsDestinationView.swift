import SwiftUI

/// C093's four destinations, in one arm of ``DriverDestinationView``.
///
/// A sub-view rather than four inline screens, for the reason every other cluster takes one arm:
/// `body` is an implicit `@ViewBuilder`, so every arm of that switch becomes another layer of
/// `_ConditionalContent` around every other arm's type, and four real screens inlined there would
/// cost the compiler far more than a switch over four cases in here.
///
/// **Two of the four are takeovers and two are not**, which is why they share a file rather than a
/// tab: SCR-DI-033 and SCR-DI-034 hang off the **Menu** tab (`‹ Menu` on both), while
/// ``DriverRoute/voipCall(rideId:)`` and ``DriverRoute/sos(rideId:)`` are
/// ``DriverRoute/isFullScreenTakeover`` and are presented over the whole app by ``DriverShell``. Both
/// takeovers close through ``DriverNavigator/closeTakeover()`` — the method the shell added for
/// exactly these two — rather than by popping a stack they were never on.
@MainActor
struct CommsDestinationView: View {

    let route: DriverRoute

    @EnvironmentObject private var graph: DriverGraph
    @EnvironmentObject private var navigator: DriverNavigator

    var body: some View {
        switch route {
        case .support:
            SupportScreen(model: graph.makeSupportModel())

        case .notifications:
            NotificationsScreen(
                model: graph.makeNotificationsModel(),
                // A stored deep link is resolved before it gets here (``NotificationsModel``), so
                // this is an ordinary navigation to a destination the route table already knows.
                onOpen: navigator.open
            )

        case .voipCall(let rideId):
            VoipCallScreen(
                model: graph.makeVoipCallModel(rideId: rideId),
                onFinished: navigator.closeTakeover
            )

        case .sos(let rideId):
            SosScreen(
                model: graph.makeSosModel(rideId: rideId),
                onFinished: navigator.closeTakeover
            )

        default:
            // Unreachable: ``DriverDestinationView`` routes exactly the four cases above here. The
            // arm exists because `DriverRoute` has thirty cases and Swift wants them all accounted
            // for — the same shape ``HomeDestinationView`` and ``ProfileDestinationView`` have.
            PlaceholderScreen(screen: route.path, route: route)
        }
    }
}
