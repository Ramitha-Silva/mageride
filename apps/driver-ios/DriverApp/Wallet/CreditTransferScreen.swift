import MageRideShared
import SwiftUI

/// **SCR-DI-024 · credit transfer + requests** (US-9.11/9.12, US-9.20/9.21, AL-01).
///
/// The wireframe, top to bottom: a `‹ Wallet` navigation bar titled *"Credit transfer"*, the
/// *"Incoming credit requests (push)"* label and its Approve/Decline cards, the *"or send directly"*
/// divider, the two `glist` fields, the *"You send / Recipient gets"* card and the CTA. The transfer
/// history the deliverable asks for follows underneath.
///
/// **Nothing on this screen may render a commission line.** The card below the fields prints both legs
/// precisely so the exact-value rule is visible; see ``CreditTransferModel``.
@MainActor
struct CreditTransferScreen: View {

    @StateObject private var model: CreditTransferModel

    init(model: @autoclosure @escaping () -> CreditTransferModel) {
        _model = StateObject(wrappedValue: model())
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: MageRideSpacing.sm) {
                banners
                incoming

                Divider().overlay(MageRideColor.outlineVariant)
                SectionLabel(key: "wallet_transfer_send_directly")

                DriverIdField(
                    labelKey: "wallet_driver_id_label",
                    value: Binding(get: { model.state.recipientId }, set: model.onRecipientIdChange),
                    supportingKey: model.state.isRecipientIdRejected
                        ? "wallet_driver_id_invalid"
                        : "wallet_driver_id_help",
                    isError: model.state.isRecipientIdRejected
                )
                RupeeField(
                    labelKey: "wallet_amount_label",
                    value: Binding(get: { model.state.amount }, set: model.onAmountChange),
                    supportingKey: amountSupportingKey
                )

                exactValueCard

                Button { Task { await model.send() } } label: {
                    Text(key: "wallet_transfer_action")
                }
                .buttonStyle(.mageCta(loading: model.state.isSubmitting))
                .disabled(!model.canSend)

                history
            }
            .padding(MageRideSpacing.md)
        }
        .frame(maxWidth: .infinity)
        .background(MageRideColor.background)
        .navigationTitle(Text(key: "wallet_transfer_title"))
        .navigationBarTitleDisplayMode(.inline)
        .refreshable { await model.refresh() }
        .task { await model.refresh() }
    }

    // MARK: - The strip

    @ViewBuilder
    private var banners: some View {
        if let errorKey = model.state.errorKey {
            DashboardBanner(text: errorKey.localised, accent: MageRideColor.error)
                .onTapGesture(perform: model.dismissError)
        }
        if let row = model.state.sent {
            DashboardBanner(
                text: "wallet_transfer_sent_notice".localisedFormat(MoneyFormat.rupees(row.amountMinor)),
                accent: MageRideColor.success,
                symbolName: "checkmark.circle.fill"
            )
            .onTapGesture(perform: model.dismissSent)
        }
    }

    /// A refusal the device worked out, shown under the amount rather than as a banner: it is about
    /// what is in the field, and it disappears when the field changes.
    private var amountSupportingKey: String? {
        guard !model.state.amount.isEmpty else { return nil }
        return model.rejectionForSend()?.messageKey
    }

    // MARK: - The inbox

    /// The approval inbox (US-9.11/9.12) — read, not pushed. See ``CreditTransferRepository/pending()``.
    @ViewBuilder
    private var incoming: some View {
        SectionLabel(key: "wallet_transfer_incoming")

        if model.state.incoming.isEmpty {
            Text(key: "wallet_transfer_incoming_empty")
                .mageFont(.bodySmall)
                .foregroundStyle(MageRideColor.onSurfaceVariant)
        } else {
            ForEach(model.state.incoming, id: \.transferId) { row in
                IncomingRequestCard(
                    row: row,
                    isBusy: model.state.busyTransferId == row.transferId,
                    onApprove: { Task { await model.approve(transferId: row.transferId) } },
                    onReject: { Task { await model.reject(transferId: row.transferId) } }
                )
            }
        }
    }

    // MARK: - The send

    /// The wireframe's *"You send Rs 1,000 / Recipient gets Rs 1,000 — exact value"*.
    ///
    /// Both figures come from `CreditTransferRules`, not from the input field twice: the rule is that
    /// the two are equal (AL-01), and a card that printed the same variable twice would still look
    /// right on the day somebody added a fee.
    private var exactValueCard: some View {
        VStack(alignment: .leading, spacing: MageRideSpacing.xxs) {
            TransferLine(
                labelKey: "wallet_transfer_you_send",
                value: model.state.debitedMinor.map { MoneyFormat.rupees($0) } ?? MoneyFormat.empty
            )
            TransferLine(
                labelKey: "wallet_transfer_recipient_gets",
                value: model.state.creditedMinor.map { MoneyFormat.rupees($0) } ?? MoneyFormat.empty,
                accent: MageRideColor.success
            )
            Text(key: "wallet_transfer_exact_value")
                .mageFont(.caption)
                .foregroundStyle(MageRideColor.success)
        }
        .padding(MageRideSpacing.sm)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(
            MageRideColor.surfaceVariant,
            in: RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
        )
    }

    // MARK: - The history

    /// Credit sent and received (US-9A.11).
    @ViewBuilder
    private var history: some View {
        if !model.state.history.isEmpty {
            SectionLabel(key: "wallet_transfer_history")
            GroupedList {
                ForEach(Array(model.state.history.enumerated()), id: \.element.transferId) { index, row in
                    TransferHistoryRow(row: row, showsSeparator: index < model.state.history.count - 1)
                }
            }
        }
    }
}

/// One incoming request.
///
/// The wireframe's subtitle reads *"Requested Rs 1,000 · Three-wheeler"*. **`TransferRow` carries no
/// vehicle** — `wallet.yaml` gives it a counterparty id, an optional name, an amount, a direction, a
/// status and a timestamp — so the vehicle is dropped rather than filled from a read that would be a
/// guess about which of the requester's vehicles they meant. Recorded in the C073 handoff.
private struct IncomingRequestCard: View {

    let row: TransferRow
    let isBusy: Bool
    let onApprove: () -> Void
    let onReject: () -> Void

    var body: some View {
        HStack(spacing: MageRideSpacing.xs) {
            VStack(alignment: .leading, spacing: 1) {
                Text(row.counterpartyName ?? row.counterpartyDriverId)
                    .mageFont(.body)
                    .foregroundStyle(MageRideColor.onSurface)
                    .lineLimit(1)
                    .truncationMode(.middle)
                Text("wallet_transfer_requested".localisedFormat(MoneyFormat.rupees(row.amountMinor)))
                    .mageFont(.caption)
                    .foregroundStyle(MageRideColor.onSurfaceVariant)
            }

            Spacer(minLength: MageRideSpacing.xs)

            if isBusy {
                ProgressView()
            } else {
                // The wireframe's two `textlink`s, not buttons: this is a card in a list and a pair of
                // filled bars on every row would make an inbox of three requests unreadable.
                DecisionLink(labelKey: "wallet_transfer_approve", accent: MageRideColor.success, action: onApprove)
                DecisionLink(labelKey: "wallet_transfer_decline", accent: MageRideColor.error, action: onReject)
            }
        }
        .padding(MageRideSpacing.sm)
        .frame(maxWidth: .infinity)
        .background(
            MageRideColor.surfaceVariant,
            in: RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
        )
    }
}

/// The wireframe's green **Approve** / red **Decline** `textlink`.
///
/// `.buttonStyle(.plain)` and an explicit tap target, because two adjacent borderless buttons inside a
/// row otherwise share the row's own tap handling and VoiceOver reads one element for both.
private struct DecisionLink: View {

    let labelKey: String
    let accent: Color
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Text(key: labelKey)
                .mageFont(.label)
                .foregroundStyle(accent)
                .padding(.horizontal, MageRideSpacing.xs)
                .frame(minHeight: MageRideControl.minimumTapTarget)
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityAddTraits(.isButton)
    }
}

/// The wireframe's `kv`.
private struct TransferLine: View {

    let labelKey: String
    let value: String
    var accent: Color?

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: MageRideSpacing.xs) {
            Text(key: labelKey)
                .mageFont(.bodySmall)
                .foregroundStyle(MageRideColor.onSurfaceVariant)
                .frame(maxWidth: .infinity, alignment: .leading)
            Text(value)
                .mageFont(.bodySmall)
                .foregroundStyle(accent ?? MageRideColor.onSurface)
        }
        .accessibilityElement(children: .combine)
    }
}

/// One past transfer.
private struct TransferHistoryRow: View {

    let row: TransferRow
    let showsSeparator: Bool

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: MageRideSpacing.xs) {
                VStack(alignment: .leading, spacing: 1) {
                    Text(row.counterpartyName ?? row.counterpartyDriverId)
                        .mageFont(.body)
                        .foregroundStyle(MageRideColor.onSurface)
                        .lineLimit(1)
                        .truncationMode(.middle)
                    Text(key: row.direction.labelKey)
                        .mageFont(.caption)
                        .foregroundStyle(MageRideColor.onSurfaceVariant)
                }
                Spacer(minLength: MageRideSpacing.xs)
                StatusPill(label: row.status.labelKey.localised, tone: row.status.tone)
                Text(MoneyFormat.rupees(row.amountMinor))
                    .mageFont(.title)
                    .foregroundStyle(MageRideColor.onSurface)
            }
            .padding(.horizontal, MageRideSpacing.sm)
            .frame(minHeight: MageRideControl.minimumTapTarget)

            if showsSeparator {
                Rectangle()
                    .fill(MageRideColor.surfaceVariant)
                    .frame(height: MageRideControl.hairline)
                    .padding(.leading, MageRideSpacing.sm)
            }
        }
        .accessibilityElement(children: .combine)
    }
}
