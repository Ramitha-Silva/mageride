import MageRideShared
import SwiftUI

/// **SCR-DI-023 · request credit** (US-9.10, AL-01, AL-34).
///
/// The wireframe, top to bottom: a `‹ Wallet` navigation bar titled *"Request credit"*, the `card fill`
/// that explains what a Driver ID is for and says there is **no QR scan**, the `glist` of two rows, and
/// the CTA.
///
/// **One block is not in the wireframe: the outstanding requests underneath.** D2' §SCR-DI-023's states
/// name *"Requested → Awaiting driver approval"* as a state of this screen and the C091 deliverable
/// asks for the pending-outgoing state; with nothing pending the screen is exactly the wireframe's.
/// Recorded in the C091 handoff.
///
/// **Nothing here opens a camera.** AL-34 removed the scan tile, and the fence is the shape of the
/// cluster rather than a check in this view: no file under `DriverApp/Wallet` reaches
/// ``DocumentCaptureCoordinator``, and ``WalletFenceTests`` fails the build if one starts to.
@MainActor
struct RequestCreditScreen: View {

    @StateObject private var model: RequestCreditModel

    init(model: @autoclosure @escaping () -> RequestCreditModel) {
        _model = StateObject(wrappedValue: model())
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: MageRideSpacing.sm) {
                if let errorKey = model.state.errorKey {
                    DashboardBanner(text: errorKey.localised, accent: MageRideColor.error)
                        .onTapGesture(perform: model.dismissError)
                }
                if let row = model.state.justRequested {
                    DashboardBanner(
                        text: "wallet_request_awaiting".localisedFormat(MoneyFormat.rupees(row.amountMinor)),
                        accent: MageRideColor.secondary,
                        symbolName: "hourglass"
                    )
                    .onTapGesture(perform: model.dismissAcknowledgement)
                }

                NoticeCard(symbolName: "person.text.rectangle", accent: MageRideColor.secondary) {
                    Text(key: "wallet_request_intro")
                        .mageFont(.caption)
                        .foregroundStyle(MageRideColor.onSurfaceVariant)
                }

                DriverIdField(
                    labelKey: "wallet_driver_id_label",
                    value: Binding(get: { model.state.holderId }, set: model.onHolderIdChange),
                    supportingKey: model.state.isHolderIdRejected
                        ? "wallet_driver_id_invalid"
                        : "wallet_driver_id_help",
                    isError: model.state.isHolderIdRejected
                )
                RupeeField(
                    labelKey: "wallet_amount_label",
                    value: Binding(get: { model.state.amount }, set: model.onAmountChange)
                )

                outstanding

                Button { Task { await model.request() } } label: {
                    Text(key: "wallet_request_action")
                }
                .buttonStyle(.mageCta(loading: model.state.isSubmitting))
                .disabled(!model.state.canRequest)
            }
            .padding(MageRideSpacing.md)
        }
        .frame(maxWidth: .infinity)
        .background(MageRideColor.background)
        .navigationTitle(Text(key: "wallet_request_title"))
        .navigationBarTitleDisplayMode(.inline)
        .task { await model.refresh() }
    }

    /// *"Waiting for approval"* — every request this driver has raised that is still open.
    @ViewBuilder
    private var outstanding: some View {
        if !model.state.outgoing.isEmpty {
            SectionLabel(key: "wallet_request_outstanding")
            GroupedList {
                ForEach(Array(model.state.outgoing.enumerated()), id: \.element.transferId) { index, row in
                    PendingRequestRow(row: row, showsSeparator: index < model.state.outgoing.count - 1)
                }
            }
        }
    }
}

/// One outstanding request.
///
/// `counterpartyName` is optional on the wire — wallet-svc sends it when registry-svc had one — so the
/// id is what identifies the row when it does not, which is also the only thing the requester typed.
private struct PendingRequestRow: View {

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
                    Text(MoneyFormat.rupees(row.amountMinor))
                        .mageFont(.caption)
                        .foregroundStyle(MageRideColor.onSurfaceVariant)
                }
                Spacer(minLength: MageRideSpacing.xs)
                StatusPill(label: row.status.labelKey.localised, tone: row.status.tone)
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
