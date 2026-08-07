import MageRideShared
import SwiftUI

/// SCR-PI-025 — *"My subscriptions"*, the vehicles this passenger may see.
///
/// The cell: a large title, then a card per vehicle — the vehicle, a `B` badge in the `vehPrivate`
/// colour, `Paid · Rs 6,000/mo · next due 6 Jul` or `Free (office transport)`, and a second row
/// carrying the status pill and the three controls: **💳 Pay** (Paid only), **🧾** history, and a
/// compact **✕** unsubscribe. Its own `Δ iOS` clause is *"also `.swipeActions` unsubscribe"*, which is
/// why the list is a `List` and not the `ScrollView` the other three screens in this cluster use.
///
/// **The swipe and the ✕ land on the same confirm.** Losing sight of a school van because a thumb
/// brushed a list is exactly the accident AL-25 makes unrecoverable without the owner, so neither
/// affordance acts on its own — the same call `apps/passenger-android` made about its
/// `SwipeToDismissBox`.
///
/// **The card's title is the Vehicle ID, and that is a contract gap rather than a layout choice.** The
/// wireframe prints *"Office Van · MR-VEH-48213"*; `Subscription` carries `vehicleId` and nothing else,
/// and `GET /v1/vehicles/{id}` is *"visible to the owner, an assigned driver and internal roles"* — a
/// passenger reading it gets `403 not-owner`. So the id is what is drawn. C082 recorded it and the
/// C100 handoff restates it.
///
/// The title is `subscriptions_title` — *"My subscriptions"*, the Android string and the Menu row that
/// opens this screen. The cell abbreviates it to *"Subscriptions"* in a 320pt mock; the two apps
/// naming the same screen the same thing is worth more than the mock's shorter word.
@MainActor
struct SubscriptionsScreen: View {

    @StateObject private var model: SubscriptionsModel

    /// SCR-PI-025a.
    let onPay: (String) -> Void

    /// SCR-PI-025b.
    let onHistory: (String) -> Void

    init(
        subscriptions: SubscriptionRepository,
        sessions: PassengerSessions,
        live: PassengerLiveMap,
        keys: IdempotencyKeys,
        onPay: @escaping (String) -> Void,
        onHistory: @escaping (String) -> Void
    ) {
        _model = StateObject(
            wrappedValue: SubscriptionsModel(
                subscriptions: subscriptions,
                sessions: sessions,
                live: live,
                keys: keys
            )
        )
        self.onPay = onPay
        self.onHistory = onHistory
    }

    var body: some View {
        List {
            if let errorKey = model.state.errorKey {
                FormErrorText(messageKey: errorKey)
                    .modifier(SubscriptionRowStyle())
            }

            if model.state.isLoading {
                LoadingRow(messageKey: "subscriptions_loading")
                    .modifier(SubscriptionRowStyle())
            } else if model.state.isEmpty {
                emptyState
                    .modifier(SubscriptionRowStyle())
            } else {
                ForEach(model.state.cards) { card in
                    SubscriptionCardView(
                        card: card,
                        isLeaving: model.state.leaving == card.id,
                        onPay: { onPay(card.id) },
                        onHistory: { onHistory(card.id) },
                        onUnsubscribe: { model.confirmUnsubscribe(card) }
                    )
                    .modifier(SubscriptionRowStyle())
                    // The cell's own `Δ iOS` clause. `allowsFullSwipe: false` on the only edge it
                    // has: one uncommitted gesture must not be able to end a subscription, and the
                    // action opens the confirm rather than acting.
                    .swipeActions(edge: .trailing, allowsFullSwipe: false) {
                        Button(role: .destructive) {
                            model.confirmUnsubscribe(card)
                        } label: {
                            Label {
                                Text(key: "subscriptions_unsubscribe")
                            } icon: {
                                Image(systemName: "xmark")
                            }
                        }
                    }
                }
            }
        }
        .listStyle(.plain)
        // The cards carry the surface; `List`'s own grouped background under them would be a second
        // one. The same treatment SCR-PI-022's list gets.
        .scrollContentBackground(.hidden)
        .background(MageRideColor.surface)
        .refreshable { await model.refresh() }
        .navigationTitle(Text(key: "subscriptions_title"))
        .navigationBarTitleDisplayMode(.large)
        .task { await model.refresh() }
        .alert(
            Text(key: "subscriptions_unsubscribe_title"),
            isPresented: confirmBinding,
            presenting: model.state.confirming
        ) { card in
            Button(role: .destructive) {
                Task { await model.unsubscribe(card) }
            } label: {
                Text(key: "subscriptions_unsubscribe")
            }
            Button(role: .cancel) { model.dismissConfirm() } label: { Text(key: "action_cancel") }
        } message: { _ in
            // AL-25 spelled out before the tap that cannot be taken back, and US-23.12 under it: the
            // passenger's row survives on the fleet's roster, muted, until the owner removes it.
            // Leaving the vehicle and leaving the fleet's books are two different things.
            Text("subscriptions_unsubscribe_body".localised + "\n\n" + "subscriptions_muted_note".localised)
        }
    }

    // MARK: -

    /// The wireframe's centred body copy, which says how to get a first subscription rather than only
    /// that there is none (US-7.14's rule, applied to a list).
    private var emptyState: some View {
        Text(key: "subscriptions_empty")
            .mageFont(.body)
            .foregroundStyle(MageRideColor.onSurfaceVariant)
            .multilineTextAlignment(.center)
            .frame(maxWidth: .infinity)
            .padding(.top, MageRideSpacing.xl)
    }

    private var confirmBinding: Binding<Bool> {
        Binding(
            get: { model.state.confirming != nil },
            set: { if !$0 { model.dismissConfirm() } }
        )
    }
}

/// One vehicle.
private struct SubscriptionCardView: View {

    let card: SubscriptionCard
    let isLeaving: Bool
    let onPay: () -> Void
    let onHistory: () -> Void
    let onUnsubscribe: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: MageRideSpacing.xs) {
            HStack(alignment: .top, spacing: MageRideSpacing.xs) {
                VStack(alignment: .leading, spacing: 1) {
                    // The Vehicle ID. See the screen's note — there is no passenger-readable name.
                    Text(card.subscription.vehicleId)
                        .mageFont(.subtitle)
                        .foregroundStyle(MageRideColor.onSurface)
                    Text(SubscriptionLabels.billingLine(card))
                        .mageFont(.caption)
                        .foregroundStyle(MageRideColor.onSurfaceVariant)
                }

                Spacer(minLength: MageRideSpacing.xs)

                // `B` — the same letter in all three scripts, which is why it is a constant. The
                // wireframe tints this badge `vehPrivate` rather than `modeB`; MAP-03's legend colour
                // is what makes the card and the marker recognisably the same vehicle.
                SolidBadge(text: ModeToken.b.badge, color: VehicleToken.privateHire.color)
            }

            HStack(spacing: MageRideSpacing.xs) {
                StatusPill(titleKey: pill.titleKey, tone: pill.tone)

                Spacer(minLength: MageRideSpacing.xs)

                // 💳 Pay and 🧾 are Paid-only: a Free vehicle collects nothing, so a payment screen
                // and a statement would both be empty by construction (US-23.2).
                if card.isPaid {
                    Button(action: onPay) {
                        Label {
                            Text(key: "subscriptions_pay")
                        } icon: {
                            Image(systemName: "creditcard.fill")
                        }
                        .mageFont(.label)
                        .foregroundStyle(MageRideColor.primary)
                    }
                    .buttonStyle(.plain)
                    .disabled(isLeaving)

                    Button(action: onHistory) {
                        Image(systemName: "doc.plaintext.fill")
                            .font(.system(size: MageRideControl.rowIcon))
                            .foregroundStyle(MageRideColor.onSurfaceVariant)
                    }
                    .buttonStyle(.plain)
                    .disabled(isLeaving)
                    .accessibilityLabel(Text(key: "subscriptions_history"))
                }

                Button(action: onUnsubscribe) {
                    Image(systemName: "xmark")
                        .font(.system(size: MageRideControl.chipIcon, weight: .semibold))
                        .foregroundStyle(MageRideColor.error)
                        .frame(width: MageRideControl.minimumTapTarget, height: MageRideControl.minimumTapTarget)
                        .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .disabled(isLeaving)
                .accessibilityLabel(Text(key: "subscriptions_unsubscribe"))
            }
        }
        .padding(MageRideSpacing.sm)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(
            MageRideColor.surfaceVariant,
            in: RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
        )
        .opacity(isLeaving ? 0.5 : 1)
    }

    /// Read outside the `HStack` rather than bound inside it: a `@ViewBuilder` body is a result
    /// builder, and a tuple-returning `let` in the middle of one is the kind of thing that compiles
    /// on one Swift version and not the next.
    private var pill: (titleKey: String, tone: StatusPill.Tone) { SubscriptionLabels.cardPill(card) }
}

/// The card treatment every row in this list shares — the wireframe draws cards on the page's own
/// surface, not `List`'s inset rows with separators. `HistoryRowStyle`'s twin, private to its cluster
/// for the reason that one is: it is the *list's* geometry rather than a reusable shape.
private struct SubscriptionRowStyle: ViewModifier {

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
