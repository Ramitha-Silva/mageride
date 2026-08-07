import MageRideShared
import PhotosUI
import SwiftUI

/// SCR-PI-025a — *"Pay subscription"*.
///
/// The cell: `‹ Back · Pay subscription`, a `card fill` carrying the vehicle, the period and the payee
/// with the amount on the right; a **Choose payment mode** label; a `glist` with one `gr` per rail and
/// a `✓` on the chosen one; `📎 Attach transfer screenshot`; and a **Confirm & pay Rs 6,000** CTA.
///
/// **Three deliberate departures from that drawing, all of them AL-49/AL-59's.**
///
/// 1. **No OnePay row, and no `+5 %` anywhere.** A subscription is paid to the *fleet owner*, and
///    OnePay has one merchant account per merchant — the money would land in MageRide's. See
///    ``SubscriptionRails``. **Cash** takes the vacant row, which is what D2' §16e and US-23.6 ask for.
///    The wireframe needs a micro-change-set; C082 recorded it and the C100 handoff restates it.
/// 2. **The owner's details appear after Confirm, not before.** `payTo` is minted by `POST …/pay` and
///    served only from a verified payout profile, so the chooser cannot print an account number it has
///    not been given.
/// 3. **The attach row belongs to the transfer rail and stays live afterwards.** A passenger who
///    already has the slip attaches it first and one tap does both; one who needs the account number
///    first gets it from stage two and attaches then.
@MainActor
struct SubscriptionPayScreen: View {

    @StateObject private var model: SubscriptionPayModel

    /// Back to SCR-PI-025 once there is nothing left to do here.
    let onDone: () -> Void

    /// The system photo picker: **no `NSPhotoLibraryUsageDescription`**, because `PhotosPicker` is
    /// PHPicker and runs out of process, granting access to the one image the passenger chose. The
    /// same contract `apps/driver-ios`'s support screenshot uses, and the reason this app's
    /// `Info.plist` carries no photo-library purpose string either.
    @State private var picked: PhotosPickerItem?

    init(
        subscriptionId: String,
        subscriptions: SubscriptionRepository,
        sessions: PassengerSessions,
        bank: BankAppHandoff,
        keys: IdempotencyKeys,
        onDone: @escaping () -> Void
    ) {
        _model = StateObject(
            wrappedValue: SubscriptionPayModel(
                subscriptionId: subscriptionId,
                subscriptions: subscriptions,
                sessions: sessions,
                bank: bank,
                keys: keys
            )
        )
        self.onDone = onDone
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: MageRideSpacing.sm) {
                summary

                if model.state.payment == nil {
                    SectionLabel(key: "subscription_pay_choose")
                    rails
                } else {
                    SubscriptionPaymentStep(
                        state: model.state,
                        onOpenBankApp: { url in Task { await model.openBankApp(url: url) } },
                        attach: { slipAttachButton }
                    )
                }

                if let errorKey = model.state.errorKey {
                    FormErrorText(messageKey: errorKey)
                }

                cta
            }
            .padding(MageRideSpacing.md)
        }
        .background(MageRideColor.surface)
        .navigationTitle(Text(key: "subscription_pay_title"))
        .navigationBarTitleDisplayMode(.inline)
        .task { await model.load() }
        .onChange(of: picked) { item in
            Task { await attach(item) }
        }
    }

    // MARK: -

    /// The wireframe's filled header: vehicle, period, payee, amount.
    private var summary: some View {
        HStack(alignment: .top, spacing: MageRideSpacing.xs) {
            VStack(alignment: .leading, spacing: 1) {
                Text(model.state.subscription?.vehicleId ?? "")
                    .mageFont(.subtitle)
                    .foregroundStyle(MageRideColor.onSurface)
                Text(summaryCaption)
                    .mageFont(.caption)
                    .foregroundStyle(MageRideColor.onSurfaceVariant)
            }

            Spacer(minLength: MageRideSpacing.xs)

            // The closure rather than `map(MoneyFormat.rupees)`: that name is overloaded on `Money`
            // and on `Int64`, and an unapplied reference to it is one more thing to resolve on a host
            // that cannot compile this.
            Text(model.state.amount.map { MoneyFormat.rupees($0) } ?? MoneyFormat.pending)
                .mageFont(.title)
                .foregroundStyle(MageRideColor.onSurface)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(MageRideSpacing.sm)
        .background(
            MageRideColor.surfaceVariant,
            in: RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
        )
    }

    /// `Jun 2026 · next due 6 Jul · to ABC Fleet (Pvt) Ltd`.
    ///
    /// The payee comes straight from `payTo` — the only name this screen ever shows, and only once a
    /// verified profile has supplied one (AL-49).
    private var summaryCaption: String {
        var parts: [String] = []
        if let period = model.state.payment?.periodMonth {
            parts.append(TripLabels.monthYear(period))
        }
        if let nextDue = model.state.subscription?.nextDue {
            parts.append("subscriptions_next_due".localisedFormat(TripLabels.dayMonth(nextDue)))
        }
        if let payee = model.state.payment?.payTo?.accountHolderName, !payee.isEmpty {
            parts.append("subscription_pay_to".localisedFormat(payee))
        }
        return parts.joined(separator: SubscriptionLabels.separator)
    }

    /// The wireframe's `glist` of rails. No surcharge line on any of them — see ``SubscriptionRails``.
    private var rails: some View {
        VStack(alignment: .leading, spacing: MageRideSpacing.xs) {
            GroupedList {
                ForEach(SubscriptionRails.methods, id: \.wire) { method in
                    Button {
                        model.choose(method)
                    } label: {
                        railRow(method)
                    }
                    .buttonStyle(.plain)
                    .accessibilityAddTraits(isChosen(method) ? [.isButton, .isSelected] : .isButton)
                }
            }

            // US-23.4 — the transfer rail cannot be confirmed without a screenshot of the slip.
            if ModeBPaymentRules.shared.requiresSlip(method: model.state.method) {
                slipAttachButton
            }
        }
    }

    /// One `.gr` row: the glyph, the label, the caption, and the `✓` the wireframe puts on the
    /// selected rail where every other row carries a `›`.
    private func railRow(_ method: SubscriptionPayMethod) -> some View {
        GroupedRow(
            titleKey: SubscriptionRails.labelKey(method),
            subtitleKey: SubscriptionRails.captionKey(method),
            symbolName: SubscriptionRails.symbolName(method),
            symbolTint: MageRideColor.secondary,
            showsSeparator: !isLast(method)
        ) {
            Image(systemName: isChosen(method) ? "checkmark" : "chevron.right")
                .font(.footnote.weight(.semibold))
                .foregroundStyle(isChosen(method) ? MageRideColor.primary : MageRideColor.outlineVariant)
        }
        .contentShape(Rectangle())
    }

    /// The wireframe's `📎 Attach transfer screenshot`, and what it says once one is attached.
    private var slipAttachButton: some View {
        PhotosPicker(selection: $picked, matching: .images, photoLibrary: .shared()) {
            HStack(spacing: MageRideSpacing.xxs) {
                Image(systemName: model.state.slipName == nil ? "paperclip" : "checkmark.circle.fill")
                    .font(.footnote)
                Text(
                    model.state.slipName.map { "subscription_slip_attached".localisedFormat($0) }
                        ?? "subscription_attach_slip".localised
                )
                .mageFont(.bodySmall)
            }
            .foregroundStyle(model.state.slipName == nil ? MageRideColor.primary : MageRideColor.success)
            .frame(maxWidth: .infinity, minHeight: MageRideControl.outlinedAction)
            .background(
                MageRideColor.background,
                in: RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
            )
            .overlay {
                RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
                    .strokeBorder(MageRideColor.outline, lineWidth: MageRideControl.hairline * 2)
            }
            .contentShape(Rectangle())
        }
    }

    /// The wireframe's bottom bar, in its two states.
    ///
    /// **Initiated and not settled has a Done and no second action**: a cash month is waiting on the
    /// owner and a transfer whose slip is still owed is waiting on the picker, and neither has a button
    /// that finishes it from here.
    @ViewBuilder
    private var cta: some View {
        if model.state.payment == nil {
            Button {
                Task { await model.confirm() }
            } label: {
                Text(
                    model.state.amount.map { "subscription_pay_confirm".localisedFormat(MoneyFormat.rupees($0)) }
                        ?? "subscription_pay_confirm_unknown".localised
                )
            }
            .buttonStyle(.mageCta(loading: model.state.isSubmitting))
            .disabled(!model.state.canConfirm)
        } else {
            Button(action: onDone) { Text(key: "action_done") }
                .buttonStyle(.mageCta)
                .disabled(model.state.isSubmitting)
        }
    }

    private func isChosen(_ method: SubscriptionPayMethod) -> Bool { model.state.method == method }

    private func isLast(_ method: SubscriptionPayMethod) -> Bool {
        SubscriptionRails.methods.last.map { $0 == method } ?? false
    }

    /// Reads the picked image into memory.
    ///
    /// Bytes rather than the picker's item, for `apps/driver-ios`'s reason: a `PhotosPickerItem` is
    /// valid for the session that produced it, and this one has to survive until Confirm — which on
    /// the transfer rail is a round trip away.
    private func attach(_ item: PhotosPickerItem?) async {
        guard let item, let data = try? await item.loadTransferable(type: Data.self) else { return }
        await model.attachSlip(fileName: SubscriptionPayScreen.slipFileName, data: data)
    }

    /// What the attachment is called in the multipart part. Not user-facing — a `PhotosPickerItem`
    /// exposes no file name at all, and inventing three translated ones would be copy for a header.
    private static let slipFileName = "transfer-slip.jpg"
}

/// Stage two — what BR-23.10 says to do with the rail that was chosen.
///
/// The branch is `:shared`'s `ModeBPaymentStep` and not a second `switch` over the method: which
/// hand-off a rail resolves to depends on what the *server* sent back (`redirectUrl`, `qrPayload`,
/// `payTo`), and that decision lives in `ModeBPaymentRules` where the driver-side fare screen's
/// equivalent also lives.
private struct SubscriptionPaymentStep<Attach: View>: View {

    let state: SubscriptionPayState
    let onOpenBankApp: (String) -> Void
    @ViewBuilder let attach: Attach

    var body: some View {
        VStack(alignment: .leading, spacing: MageRideSpacing.xs) {
            if let step = state.step {
                content(step)
            } else {
                StepNote(titleKey: "subscription_unavailable", bodyKey: "subscription_unavailable_body")
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    /// The step is unwrapped before the `switch` on purpose: an `as` pattern applied to an `Optional`
    /// is a conditional cast Swift will accept and then reason about differently between releases, and
    /// this switch is the one place a passenger's money instructions are chosen.
    @ViewBuilder
    private func content(_ step: ModeBPaymentStep) -> some View {
        switch step {
        case let handoff as ModeBPaymentStepGatewayHandoff:
            gateway(handoff.action)

        case is ModeBPaymentStepShowOwnerLankaQr:
            // The image itself was fetched by the model when the payment landed; the link on the step
            // is the one it used, and nothing re-derives it here.
            OwnerQrPanel(image: state.ownerQr)

        case let transfer as ModeBPaymentStepTransferAndUploadSlip:
            BankDetails(payTo: transfer.payTo)
            if state.isAwaitingSlip {
                attach
            } else {
                StepNote(titleKey: "subscription_transfer_pending_title", bodyKey: "subscription_pending_body")
            }

        // US-23.6 — only the owner can mark cash received, in the web portal. Nothing on this handset
        // finishes it, and saying so beats a spinner that never resolves.
        case is ModeBPaymentStepHandToCollector:
            StepNote(titleKey: "subscription_cash_title", bodyKey: "subscription_cash_body")

        default:
            StepNote(titleKey: "subscription_unavailable", bodyKey: "subscription_unavailable_body")
        }
    }

    @ViewBuilder
    private func gateway(_ action: FarePaymentAction) -> some View {
        switch action {
        case let open as FarePaymentActionOpenBankApp:
            OutlinedAction(titleKey: "subscription_open_bank_app", symbolName: "building.columns.fill") {
                onOpenBankApp(open.url)
            }

        // AL-15's fallback: no bank app took the link, so the payload is what is left. It is the
        // OWNER's LankaQR string and is shown as text rather than re-encoded — this app renders no QR
        // of its own (AL-22).
        case let fallback as FarePaymentActionShowLankaQrFallback:
            StepNote(titleKey: "subscription_lankaqr_payload", text: fallback.payload)

        default:
            StepNote(titleKey: "subscription_unavailable", bodyKey: "subscription_unavailable_body")
        }
    }
}

/// AL-49's verified bank block, exactly as `payTo` gave it.
private struct BankDetails: View {

    let payTo: PayTo

    var body: some View {
        VStack(alignment: .leading, spacing: MageRideSpacing.xxs) {
            SectionLabel(key: "subscription_transfer_to")
            row(labelKey: "subscription_bank", value: payTo.bank)
            row(labelKey: "subscription_branch", value: payTo.branch)
            row(labelKey: "subscription_account_no", value: payTo.accountNo)
            row(labelKey: "subscription_account_holder", value: payTo.accountHolderName)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(MageRideSpacing.sm)
        .background(
            MageRideColor.surfaceVariant,
            in: RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
        )
    }

    /// One `label · value` line. Absent when the profile carried no value.
    ///
    /// The value is selectable, which is what an account number is for: a passenger copies it into
    /// their banking app, and re-typing sixteen digits is where a transfer goes to the wrong account.
    /// The same call `apps/driver-ios`'s SCR-DI-029 made about the driver's own platform id.
    @ViewBuilder
    private func row(labelKey: String, value: String?) -> some View {
        if let value, !value.isEmpty {
            HStack(alignment: .top, spacing: MageRideSpacing.xs) {
                Text(key: labelKey)
                    .mageFont(.caption)
                    .foregroundStyle(MageRideColor.onSurfaceVariant)
                Spacer(minLength: MageRideSpacing.xs)
                Text(value)
                    .mageFont(.bodySmall)
                    .foregroundStyle(MageRideColor.onSurface)
                    .multilineTextAlignment(.trailing)
                    .textSelection(.enabled)
            }
            .accessibilityElement(children: .combine)
        }
    }
}

/// The owner's own bank-app LankaQR, for the passenger to scan with theirs.
///
/// Decoded here rather than in the model so no imaging type reaches a class a test drives.
/// `UIImage(data:)` answers `nil` for anything it cannot decode — including an empty response — and
/// the caption below is what that produces rather than a crash.
///
/// `.interpolation(.none)` because a QR is a grid of squares: smoothing it is how a scannable code
/// stops scanning at the size a phone screen renders it.
private struct OwnerQrPanel: View {

    let image: Data?

    var body: some View {
        VStack(spacing: MageRideSpacing.xs) {
            if let decoded {
                Image(uiImage: decoded)
                    .resizable()
                    .interpolation(.none)
                    .scaledToFit()
                    .frame(width: MageRideControl.ownerQr, height: MageRideControl.ownerQr)
                    .accessibilityLabel(Text(key: "subscription_owner_qr"))
            }
            Text(key: decoded == nil ? "subscription_owner_qr_missing" : "subscription_owner_qr_hint")
                .mageFont(.bodySmall)
                .foregroundStyle(MageRideColor.onSurfaceVariant)
                .multilineTextAlignment(.center)
        }
        .frame(maxWidth: .infinity)
    }

    private var decoded: UIImage? { image.flatMap { UIImage(data: $0) } }
}

/// A titled note — the cash, pending, fallback-payload and unavailable states.
private struct StepNote: View {

    let titleKey: String

    /// Translated copy, for the three notes that have some.
    var bodyKey: String?

    /// A resolved value rather than a key, for the one note whose body is **server data**: AL-15's
    /// fallback prints the owner's LankaQR payload, which is not copy and must never be translated.
    var text: String?

    var body: some View {
        VStack(alignment: .leading, spacing: MageRideSpacing.xxs) {
            Text(key: titleKey)
                .mageFont(.subtitle)
                .foregroundStyle(MageRideColor.onSurface)

            if let bodyKey {
                Text(key: bodyKey)
                    .mageFont(.bodySmall)
                    .foregroundStyle(MageRideColor.onSurfaceVariant)
                    .fixedSize(horizontal: false, vertical: true)
            } else if let text {
                Text(text)
                    .mageFont(.bodySmall)
                    .foregroundStyle(MageRideColor.onSurfaceVariant)
                    .textSelection(.enabled)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(MageRideSpacing.sm)
        .background(
            MageRideColor.surfaceVariant,
            in: RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
        )
    }
}
