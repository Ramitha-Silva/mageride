import MageRideShared
import SwiftUI

/// SCR-PI-024 — *"Private transport"*, the Mode B access request.
///
/// The cell: `‹ Back · Private transport`, a `card fill` carrying a `MODE B` badge and the line
/// *"Request access to track a private vehicle. Owner approves in the Driver App."*, a **Vehicle ID**
/// field, the Pending/Accepted/Rejected card, a `spacer`, and a **Send request** CTA pinned to the
/// bottom.
///
/// **Two doors, one screen** (AL-23). A Mode B marker on SCR-PI-010 hands over the vehicle id it just
/// drew (``LiveMapModel/onMarkerTapped(_:)`` routes it there); the Menu tab's *"Private transport"*
/// row opens the same screen with an empty field. The route's associated value is optional for exactly
/// that reason — see ``PassengerRoute/modeBRequest(vehicleId:)``.
///
/// **A Mode B marker never opens SCR-PI-007.** That popup is Mode A's; a private vehicle a passenger
/// has no grant for has nothing to show them but this.
@MainActor
struct ModeBRequestScreen: View {

    @StateObject private var model: ModeBRequestModel

    /// SCR-PI-025, from the accepted card's link.
    let onOpenSubscriptions: () -> Void

    init(
        vehicleId: String?,
        subscriptions: SubscriptionRepository,
        sessions: PassengerSessions,
        keys: IdempotencyKeys,
        onOpenSubscriptions: @escaping () -> Void
    ) {
        _model = StateObject(
            wrappedValue: ModeBRequestModel(
                vehicleId: vehicleId,
                subscriptions: subscriptions,
                sessions: sessions,
                keys: keys
            )
        )
        self.onOpenSubscriptions = onOpenSubscriptions
    }

    var body: some View {
        VStack(alignment: .leading, spacing: MageRideSpacing.sm) {
            explainer

            LabelledTextField(
                labelKey: "mode_b_vehicle_id",
                value: vehicleIdBinding,
                placeholder: ModeBRequestScreen.vehicleIdExample,
                supportingKey: model.state.isPrefilled ? "mode_b_from_marker" : "mode_b_type_id",
                autocapitalisation: .characters
            )
            .disabled(model.state.isSending || model.state.existing != nil)

            if let status = model.state.status {
                decision(status)
            }

            if let errorKey = model.state.errorKey {
                FormErrorText(messageKey: errorKey)
            }

            // The wireframe's `spacer` — the CTA is pinned to the bottom of the body.
            Spacer(minLength: MageRideSpacing.md)

            Button {
                Task { await model.send() }
            } label: {
                Text(key: "mode_b_send_request")
            }
            .buttonStyle(.mageCta(loading: model.state.isSending))
            .disabled(!model.state.canSend)
        }
        .padding(MageRideSpacing.md)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .background(MageRideColor.surface)
        .navigationTitle(Text(key: "mode_b_request_title"))
        .navigationBarTitleDisplayMode(.inline)
        .task { await model.loadExisting() }
    }

    // MARK: -

    /// The wireframe's filled `MODE B` card.
    private var explainer: some View {
        VStack(alignment: .leading, spacing: MageRideSpacing.xxs) {
            // C096's own mode copy, not a second key for the same words — D2' §0.2 has one Mode B
            // label and `LocalizationTests` reads two identical values as a translation nobody did.
            SolidBadge(text: ModeToken.b.nameKey.localised, color: ModeToken.b.color)

            Text(key: "mode_b_explainer")
                .mageFont(.bodySmall)
                .foregroundStyle(MageRideColor.onSurfaceVariant)
                .fixedSize(horizontal: false, vertical: true)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(MageRideSpacing.sm)
        .background(
            MageRideColor.surfaceVariant,
            in: RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
        )
    }

    /// US-4.6's chip, as the wireframe's tinted card.
    ///
    /// **Pending is the state this screen can actually observe.** Accepted is inferred from a
    /// subscription existing (see ``ModeBRequestModel``); Rejected has no signal on this surface at
    /// all and is drawn only because the enum can carry it — a rejection reaches a passenger through
    /// the owner, not through the platform. Restated in the C100 handoff.
    private func decision(_ status: AccessRequestStatus) -> some View {
        let labels = ModeBRequestScreen.decisionLabels(for: status)

        return VStack(alignment: .leading, spacing: MageRideSpacing.xxs) {
            StatusPill(titleKey: labels.titleKey, tone: labels.tone)

            Text(key: labels.noteKey)
                .mageFont(.bodySmall)
                .foregroundStyle(MageRideColor.onSurfaceVariant)
                .fixedSize(horizontal: false, vertical: true)

            if model.state.isAccepted {
                TextLink(key: "mode_b_open_subscriptions") { onOpenSubscriptions() }
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(MageRideSpacing.sm)
        .background(
            // The wireframe's status cards are a wash of their own colour, not a solid fill — the
            // same treatment ``StatusPill`` gives its own background.
            ModeBRequestScreen.tint(for: labels.tone).opacity(0.14),
            in: RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
        )
    }

    private var vehicleIdBinding: Binding<String> {
        Binding(get: { model.state.vehicleId }, set: model.onVehicleIdChange)
    }

    /// The three decisions, as copy and a tone.
    ///
    /// A `static` function rather than a computed property on ``AccessRequestStatus`` for
    /// ``RideStateLabel``'s reason: the mapping from a wire enum onto trilingual copy belongs beside
    /// the screen that draws it, and there is exactly one of those.
    ///
    /// `nonisolated` because it is a pure table and this type is `@MainActor` — a suite asserting the
    /// mapping should not have to be main-actor isolated to read it.
    nonisolated static func decisionLabels(for status: AccessRequestStatus) -> (titleKey: String,
                                                                               noteKey: String,
                                                                               tone: StatusPill.Tone) {
        if status == AccessRequestStatus.accepted {
            return ("mode_b_status_accepted", "mode_b_accepted_note", .ok)
        }
        if status == AccessRequestStatus.rejected {
            return ("mode_b_status_rejected", "mode_b_rejected_note", .error)
        }
        // US-4.7's first month free, which is the fleet's offer and not the platform's.
        return ("mode_b_status_pending", "mode_b_pending_note", .warning)
    }

    private static func tint(for tone: StatusPill.Tone) -> Color {
        switch tone {
        case .ok: return MageRideColor.success
        case .warning: return MageRideColor.warning
        case .error: return MageRideColor.error
        case .info: return MageRideColor.secondary
        case .muted: return MageRideColor.onSurfaceVariant
        }
    }

    /// The wireframe's example Vehicle ID, as the field's placeholder.
    ///
    /// A registration handle is the same characters in all three scripts — the same argument
    /// ``LanguageDisplay``, ``PhoneNumber`` and ``MoneyFormat/prefix`` make — so it is a Swift constant
    /// rather than three identical `.strings` values, which `LocalizationTests` reads as a translation
    /// nobody did.
    static let vehicleIdExample = "MR-VEH-48213"
}
