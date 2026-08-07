import SwiftUI

/// C091's five destinations, built from the graph's singletons.
///
/// The one arm ``DriverDestinationView`` gives this cluster, expanded — split out for the same reason
/// ``JobsDestinationView`` is: `body` is an implicit `@ViewBuilder`, so every arm of a `switch` wraps
/// every other arm's type in another `_ConditionalContent`, and five real screens inlined beside the
/// remaining placeholders is a type the compiler struggles to infer at all.
///
/// **All five sit on the Wallet tab**, which is what ``DriverRoute/tab`` already declares: SCR-DI-021 is
/// its root and the other four are pushed onto it. That is what makes the system back button say
/// `‹ Wallet` on each of them, which is the label every one of those four cells draws.
///
/// `@MainActor` on the whole view, not on its initialiser — see ``HomeDestinationView`` for why.
@MainActor
struct WalletDestinationView: View {

    let route: DriverRoute

    @EnvironmentObject private var graph: DriverGraph
    @EnvironmentObject private var navigator: DriverNavigator

    var body: some View {
        switch route {
        case .wallet:
            WalletScreen(model: graph.makeWalletModel(), onOpen: { navigator.open($0) })

        case .walletTopUp:
            TopUpScreen(model: graph.makeTopUpModel())

        case .walletRequestCredit:
            RequestCreditScreen(model: graph.makeRequestCreditModel())

        case .walletTransfer:
            CreditTransferScreen(model: graph.makeCreditTransferModel())

        case .walletHistory:
            WalletHistoryScreen(model: graph.makeWalletHistoryModel())

        default:
            // Unreachable: ``DriverDestinationView`` routes exactly the five cases above here. The arm
            // exists because `DriverRoute` has thirty cases and Swift wants them all accounted for.
            PlaceholderScreen(screen: route.path, route: route)
        }
    }
}
