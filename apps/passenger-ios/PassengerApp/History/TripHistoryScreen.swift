import MageRideShared
import SwiftUI

/// SCR-PI-022 — Past / Scheduled / Packages. Tab 2, and the app's second root.
///
/// The cell: a large *"Your trips"* title, a three-way `tabbar2`, and a stack of cards. Its own
/// `Δ iOS` clause is **`Picker(.segmented)` + `List` + `.refreshable`**, and all three are here — the
/// `tabbar2` class is `UISegmentedControl` in the wireframe's own CSS, the same reading C097 made of
/// SCR-PI-009's toggles.
///
/// **Each completed trip shows the driver and offers a Call** (US-8.7 / US-24.4) so a passenger can
/// reach them about a lost item, and the number is hidden for a trip cancelled before assignment —
/// this component's fence. See ``TripHistoryModel`` for why the Call costs a second read.
///
/// **The Picker sits above the `List` rather than inside it.** A segmented control that scrolls away
/// is a filter a passenger has to hunt for, and `.refreshable` belongs to the list underneath it
/// either way.
@MainActor
struct TripHistoryScreen: View {

    @StateObject private var model: TripHistoryModel

    /// SCR-PI-023.
    let onOpenTrip: (String) -> Void

    /// SCR-PI-015a's *"Free call"* → SCR-PI-028, for the ride the chooser was opened on.
    let onFreeCall: (String) -> Void

    private let choice: CallChoice
    private let contact: RideContact

    init(
        history: HistoryRepository,
        sessions: PassengerSessions,
        choice: CallChoice,
        contact: RideContact,
        onOpenTrip: @escaping (String) -> Void,
        onFreeCall: @escaping (String) -> Void
    ) {
        _model = StateObject(wrappedValue: TripHistoryModel(history: history, sessions: sessions))
        self.choice = choice
        self.contact = contact
        self.onOpenTrip = onOpenTrip
        self.onFreeCall = onFreeCall
    }

    var body: some View {
        VStack(spacing: MageRideSpacing.xs) {
            Picker("", selection: tabBinding) {
                ForEach(HistoryTab.allCases) { tab in
                    Text(key: tab.titleKey).tag(tab)
                }
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .padding(.horizontal, MageRideSpacing.md)

            if let errorKey = model.state.errorKey {
                FormErrorText(messageKey: errorKey)
                    .padding(.horizontal, MageRideSpacing.md)
            }

            list
        }
        .background(MageRideColor.surface)
        .navigationTitle(Text(key: "history_title"))
        .navigationBarTitleDisplayMode(.large)
        .task { await model.refresh() }
        // `item:` rather than `isPresented:`, so the sheet and the model hold one answer between
        // them: a swipe-dismiss has to clear the target or the next Call opens nothing.
        .sheet(item: callBinding) { target in
            CallChooserSheet(
                driverName: target.driverName,
                driverPhone: target.phone,
                choice: choice,
                contact: contact,
                onFreeCall: {
                    model.onCallConsumed()
                    onFreeCall(target.rideId)
                },
                onDismiss: { model.onCallConsumed() }
            )
        }
    }

    // MARK: -

    @ViewBuilder
    private var list: some View {
        List {
            if model.state.isLoading {
                LoadingRow(messageKey: "history_loading")
                    .modifier(HistoryRowStyle())
            } else if model.state.isEmpty {
                emptyState
                    .modifier(HistoryRowStyle())
            } else if model.state.tab == .scheduled {
                ForEach(model.state.scheduled) { row in
                    ScheduledRideCard(row: row)
                        .modifier(HistoryRowStyle())
                }
            } else {
                ForEach(model.state.visibleRides, id: \.rideId) { row in
                    TripHistoryCard(
                        row: row,
                        onOpen: { onOpenTrip(row.rideId) },
                        onCall: { Task { await model.call(row) } }
                    )
                    .modifier(HistoryRowStyle())
                }
            }
        }
        .listStyle(.plain)
        // The cards carry the surface; `List`'s own grouped background under them would be a second
        // one. iOS 16 is this target's floor, which is the version this modifier arrived in.
        .scrollContentBackground(.hidden)
        .refreshable { await model.refresh() }
    }

    /// The cell's *"empty → illustration"*, and it says **which** kind of empty (US-7.14's rule,
    /// applied to a list): nothing scheduled is not the same as nothing ever ridden.
    ///
    /// ``IllustrationPanel`` hides itself from VoiceOver — it is decoration — so the label is put back
    /// on the container, which is the one thing a reader should announce here.
    private var emptyState: some View {
        IllustrationPanel(
            symbolName: "list.bullet.rectangle",
            caption: model.state.tab.emptyKey.localised,
            height: MageRideControl.illustrationPanel
        )
        .padding(.top, MageRideSpacing.xl)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(Text(key: model.state.tab.emptyKey))
    }

    private var tabBinding: Binding<HistoryTab> {
        Binding(
            get: { model.state.tab },
            set: { tab in
                model.select(tab)
                if tab == .scheduled { Task { await model.loadScheduled() } }
            }
        )
    }

    /// The chooser's target, as a binding a `.sheet(item:)` can clear.
    private var callBinding: Binding<CallTarget?> {
        Binding(
            get: { model.state.callTarget },
            set: { if $0 == nil { model.onCallConsumed() } }
        )
    }
}

/// The card treatment every row in this list shares — the wireframe draws cards on the page's own
/// surface, not `List`'s inset rows with separators.
private struct HistoryRowStyle: ViewModifier {

    func body(content: Content) -> some View {
        content
            .listRowInsets(
                EdgeInsets(
                    top: MageRideSpacing.xxs,
                    leading: MageRideSpacing.md,
                    bottom: MageRideSpacing.xxs,
                    trailing: MageRideSpacing.md
                )
            )
            .listRowSeparator(.hidden)
            .listRowBackground(Color.clear)
    }
}
