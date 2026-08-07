import MageRideShared
import SafariServices
import SwiftUI

/// **SCR-DI-022 · top up wallet** (US-9.18, US-9.19, AL-05, AL-15).
///
/// The wireframe, top to bottom: a `‹ Wallet` navigation bar titled *"Top Up"*, the
/// **Card · OnePay · LankaQR** segmented control, the amount field, the voucher ladder with its
/// DB-configured discounts, the card that explains where the discount lives, and the CTA that reads
/// *"Pay Rs 1,800 · get Rs 2,000"* when a tile is selected.
///
/// **There is no bank-transfer segment and there is nowhere to add one** (AL-05). Read ``TopUpModel``
/// before touching the voucher path — buying one is a purchase on subscription-svc, not a top-up of
/// the discounted price.
///
/// **Δ iOS — the segmented control is a `Picker`, not a chip row.** `driver_ios.html` draws the method
/// row as `.seg`, which is `UISegmentedControl` in the wireframe's own CSS; the Android twin's
/// `FilterChip`s are Material's answer to the same choice. Same argument SCR-DI-020's period tabs make.
@MainActor
struct TopUpScreen: View {

    @StateObject private var model: TopUpModel

    init(model: @autoclosure @escaping () -> TopUpModel) {
        _model = StateObject(wrappedValue: model())
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: MageRideSpacing.sm) {
                banners

                methodPicker
                RupeeField(
                    labelKey: "wallet_amount_label",
                    value: Binding(get: { model.state.amount }, set: model.onAmountChange)
                )

                SectionLabel(key: "wallet_voucher_label")
                voucherTiles
                voucherNote

                payButton
            }
            .padding(MageRideSpacing.md)
        }
        .frame(maxWidth: .infinity)
        .background(MageRideColor.background)
        .navigationTitle(Text(key: "wallet_top_up_title"))
        .navigationBarTitleDisplayMode(.inline)
        .task { await model.refresh() }
        // ONE sheet modifier, not three, and that is not a tidy-up.
        //
        // A voucher bought on OnePay sets `onepayUrl` **and** `receipt` in the same breath, and one
        // bought on LankaQR sets `fallbackQr` and `receipt`; on Android those are two dialogs that
        // stack. SwiftUI presents at most one sheet per presentation context and silently drops the
        // rest — so three `.sheet` modifiers would leave the receipt un-presented on exactly the two
        // paths where it says the credit is on its way. Ranking them is the honest fix: the driver
        // finishes paying first, and the receipt is what is under the sheet they just closed.
        .sheet(item: presentedSheet) { sheet in
            switch sheet {
            case .checkout(let checkout):
                SafariView(url: checkout.url) { Task { await model.onCheckoutDismissed() } }
                    .ignoresSafeArea()
            case .fallback(let fallback):
                FallbackSheet(payload: fallback.payload, onDismiss: model.dismissFallback)
            case .receipt(let item):
                ReceiptSheet(receipt: item.receipt, onDismiss: model.dismissReceipt)
            }
        }
    }

    // MARK: - What is presented over the form

    /// The one sheet, chosen by rank, with a setter that dismisses whichever is up.
    ///
    /// The OnePay browser outranks the AL-15 code, which outranks the receipt: each is a step the
    /// driver has to finish before the next one means anything. A swipe on the sheet — or, for the
    /// browser, the controller's own **Done** — comes back through `set(nil)`, which is why the model
    /// is told rather than the state cleared here.
    private var presentedSheet: Binding<TopUpSheet?> {
        Binding(
            get: {
                if let checkout = model.state.onepayUrl { return TopUpSheet.checkout(checkout) }
                if let fallback = model.state.fallbackQr { return TopUpSheet.fallback(fallback) }
                return model.state.receipt.map { TopUpSheet.receipt(ReceiptItem($0)) }
            },
            set: { newValue in
                guard newValue == nil else { return }
                if model.state.onepayUrl != nil {
                    Task { await model.onCheckoutDismissed() }
                } else if model.state.fallbackQr != nil {
                    model.dismissFallback()
                } else {
                    model.dismissReceipt()
                }
            }
        )
    }

    // MARK: - The strip

    @ViewBuilder
    private var banners: some View {
        if let errorKey = model.state.errorKey {
            DashboardBanner(text: errorKey.localised, accent: MageRideColor.error)
                .onTapGesture(perform: model.dismissError)
        }
        if model.state.isAwaitingGateway {
            DashboardBanner(
                text: "wallet_topup_pending".localised,
                accent: MageRideColor.secondary,
                symbolName: "clock"
            )
        }
        if model.state.pending?.hasTimedOut == true {
            DashboardBanner(
                text: "wallet_topup_slow".localised,
                accent: MageRideColor.warning,
                symbolName: "clock.badge.exclamationmark"
            )
        }
    }

    // MARK: - The method

    /// The wireframe's `.seg` — three segments, because `TopupMethod` has three entries and no fourth
    /// can be added (AL-05). Card and OnePay wallet are separate segments over one endpoint: the choice
    /// between them is made on OnePay's hosted page, and a segment still has to know which one it is.
    private var methodPicker: some View {
        Picker(
            selection: Binding(get: { model.state.method }, set: model.select(method:)),
            label: Text(key: "wallet_method")
        ) {
            ForEach(TopupMethods.all, id: \.ordinal) { method in
                Text(key: method.labelKey).tag(method)
            }
        }
        .pickerStyle(.segmented)
        .accessibilityLabel(Text(key: "wallet_method"))
    }

    // MARK: - The vouchers

    /// The bulk-voucher ladder (US-9.19).
    ///
    /// Empty when the tier table has not been read or Finance has withdrawn every denomination —
    /// `VoucherCatalogue` deliberately ships no default ladder, because a rate nobody set is worse than
    /// no offer at all.
    @ViewBuilder
    private var voucherTiles: some View {
        if model.state.vouchers.isEmpty {
            Text(key: model.state.isLoading ? "wallet_voucher_loading" : "wallet_voucher_empty")
                .mageFont(.bodySmall)
                .foregroundStyle(MageRideColor.onSurfaceVariant)
        } else {
            FlowRow(spacing: MageRideSpacing.xs) {
                ForEach(model.state.vouchers, id: \.denomination.amountMinor) { quote in
                    VoucherTile(
                        quote: quote,
                        isSelected: quote.denomination.amountMinor == model.state.voucherDenominationMinor
                    ) {
                        model.selectVoucher(denominationMinor: quote.denomination.amountMinor)
                    }
                }
            }
        }
    }

    /// The wireframe's explanatory card — where the discount lives, and what it does not apply to.
    private var voucherNote: some View {
        NoticeCard(symbolName: "info.circle", accent: MageRideColor.secondary) {
            Text(key: "wallet_voucher_note")
                .mageFont(.caption)
                .foregroundStyle(MageRideColor.onSurfaceVariant)
        }
    }

    // MARK: - The CTA

    /// *"Pay Rs 1,800 · get Rs 2,000"*, or *"Pay Rs 2,000"* for a plain top-up.
    ///
    /// The two-part label appears **only** when a voucher tile is selected, because it is the only case
    /// where what is paid and what is credited differ — and that gap is the whole of US-9.19.
    private var payButton: some View {
        Button { Task { await model.pay() } } label: {
            Text(payLabel)
        }
        .buttonStyle(.mageCta(loading: model.state.isSubmitting))
        .disabled(!model.state.canPay)
    }

    private var payLabel: String {
        guard let payable = model.state.payableMinor else { return "wallet_pay".localised }
        guard let credited = model.state.creditedMinor, credited != payable else {
            return "wallet_pay_amount".localisedFormat(MoneyFormat.rupees(payable))
        }
        return "wallet_pay_and_get".localisedFormat(MoneyFormat.rupees(payable), MoneyFormat.rupees(credited))
    }
}

/// The three things SCR-DI-022 can put over its form, as one presentation.
///
/// See ``TopUpScreen/presentedSheet`` for why they are ranked rather than attached separately.
private enum TopUpSheet: Identifiable {

    /// OnePay's hosted page, in an `SFSafariViewController` (Δ iOS).
    case checkout(OnepayCheckout)

    /// AL-15's rendered payload, when no bank app claimed the link.
    case fallback(LankaQrPayload)

    /// The wireframe's *"Success → receipt"*.
    case receipt(ReceiptItem)

    var id: String {
        switch self {
        case .checkout(let checkout): return "checkout:" + checkout.id
        case .fallback(let fallback): return "fallback:" + fallback.id
        case .receipt(let receipt): return "receipt:" + receipt.id
        }
    }
}

/// One rung of the ladder — the wireframe's `chip sm`, `5k +15%`.
private struct VoucherTile: View {

    let quote: VoucherQuote
    let isSelected: Bool
    let onTap: () -> Void

    var body: some View {
        Button(action: onTap) {
            Text(
                "wallet_voucher_tile".localisedFormat(
                    MoneyFormat.rupees(quote.denomination),
                    MoneyFormat.percentOfBps(Int(quote.discountBps))
                )
            )
            .mageFont(.label)
            .foregroundStyle(isSelected ? MageRideColor.onPrimary : MageRideColor.onSurface)
            .padding(.horizontal, MageRideSpacing.sm)
            .padding(.vertical, MageRideSpacing.xs)
            .background(
                isSelected ? MageRideColor.primary : MageRideColor.surfaceVariant,
                in: Capsule()
            )
            .contentShape(Capsule())
        }
        .buttonStyle(.plain)
        .accessibilityAddTraits(isSelected ? [.isButton, .isSelected] : .isButton)
    }
}

/// AL-15's fallback: the payload rendered for a bank app to scan.
private struct FallbackSheet: View {

    let payload: String
    let onDismiss: () -> Void

    var body: some View {
        NavigationStack {
            VStack(spacing: MageRideSpacing.sm) {
                Text(key: "wallet_lankaqr_body")
                    .mageFont(.bodySmall)
                    .foregroundStyle(MageRideColor.onSurfaceVariant)
                    .multilineTextAlignment(.center)

                LankaQrCode(payload: payload)
                Spacer(minLength: 0)
            }
            .padding(MageRideSpacing.md)
            .frame(maxWidth: .infinity)
            .background(MageRideColor.background)
            .navigationTitle(Text(key: "wallet_lankaqr_title"))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .confirmationAction) {
                    Button(action: onDismiss) { Text(key: "action_close") }
                }
            }
        }
        .interactiveDismissDisabled()
    }
}

/// The wireframe's *"Success → receipt + count-up"* (Δ iOS).
///
/// The credited figure counts up to its value rather than appearing at it — the cell's own `Δ iOS`
/// clause, and the one place in this cluster where an animation carries meaning: what a driver is
/// checking on this sheet is that the number went **up**, and a figure that animates to it says so
/// without a second line of copy. `.animation` over a `@State`, so Reduce Motion is honoured by the
/// system rather than by a branch here.
private struct ReceiptSheet: View {

    let receipt: TopUpReceipt
    let onDismiss: () -> Void

    @State private var countedUpTo: Int64 = 0

    var body: some View {
        NavigationStack {
            VStack(spacing: MageRideSpacing.sm) {
                Image(systemName: "checkmark.circle.fill")
                    .font(.system(size: MageRideControl.illustrationIcon))
                    .foregroundStyle(MageRideColor.success)

                Text("wallet_receipt_paid".localisedFormat(MoneyFormat.rupees(receipt.paidMinor)))
                    .mageFont(.bodySmall)
                    .foregroundStyle(MageRideColor.onSurfaceVariant)

                Text("wallet_receipt_credited".localisedFormat(MoneyFormat.rupees(countedUpTo)))
                    .mageFont(.headline)
                    .foregroundStyle(MageRideColor.success)
                    .contentTransition(.numericText())
                    .animation(.easeOut(duration: 0.6), value: countedUpTo)

                if !receipt.isSettled {
                    Text(key: "wallet_receipt_pending")
                        .mageFont(.caption)
                        .foregroundStyle(MageRideColor.onSurfaceVariant)
                        .multilineTextAlignment(.center)
                }

                Spacer(minLength: 0)

                Button(action: onDismiss) { Text(key: "action_close") }
                    .buttonStyle(.mageCta)
            }
            .padding(MageRideSpacing.md)
            .frame(maxWidth: .infinity)
            .background(MageRideColor.background)
            .navigationTitle(Text(key: "wallet_receipt_title"))
            .navigationBarTitleDisplayMode(.inline)
        }
        .presentationDetents([.medium])
        .interactiveDismissDisabled()
        .onAppear { countedUpTo = receipt.creditedMinor }
    }
}

/// A receipt the sheet is up for.
///
/// ``TopUpReceipt`` is a value the model owns and re-creates; `Identifiable` on a wrapper keyed by the
/// pair of figures is what lets `.sheet(item:)` present it without a separate `Bool` the pair can
/// disagree with for a frame.
private struct ReceiptItem: Identifiable {

    let receipt: TopUpReceipt

    init(_ receipt: TopUpReceipt) {
        self.receipt = receipt
    }

    var id: String { "\(receipt.paidMinor):\(receipt.creditedMinor):\(receipt.isSettled)" }
}

/// `SFSafariViewController`, as a SwiftUI presentation (Δ iOS).
///
/// **The hosted page is in the app, and that is the cell's own `Δ iOS` clause** — *"OnePay via
/// `SFSafariViewController`"*. It is not merely a nicer browser: the controller shares no cookies with
/// Safari, shows the driver the real origin in a bar they cannot edit, and comes back under the app's
/// own navigation instead of through the task stack. On Android the same page is an `ACTION_VIEW` that
/// can fail when no browser is installed, which is a failure this platform does not have.
///
/// ``onFinished`` fires once, on whichever comes first: the driver's **Done**, or the gateway
/// redirecting onto ``PaymentReturn/host``. Both mean the same thing to the model — *the driver is
/// back, go and read the session* — and neither is trusted to say what became of it.
struct SafariView: UIViewControllerRepresentable {

    let url: URL
    let onFinished: () -> Void

    func makeUIViewController(context: Context) -> SFSafariViewController {
        let configuration = SFSafariViewController.Configuration()
        configuration.entersReaderIfAvailable = false
        let controller = SFSafariViewController(url: url, configuration: configuration)
        controller.dismissButtonStyle = .done
        controller.delegate = context.coordinator
        return controller
    }

    func updateUIViewController(_ controller: SFSafariViewController, context: Context) {
        // Nothing to push: the controller owns its own navigation, and re-assigning the delegate on
        // every SwiftUI update is how a checkout in progress loses its finish callback.
    }

    func makeCoordinator() -> Coordinator { Coordinator(onFinished: onFinished) }

    final class Coordinator: NSObject, SFSafariViewControllerDelegate {

        private let onFinished: () -> Void
        private var hasFinished = false

        init(onFinished: @escaping () -> Void) {
            self.onFinished = onFinished
        }

        func safariViewControllerDidFinish(_ controller: SFSafariViewController) {
            finishOnce()
        }

        /// The gateway sent the driver back.
        ///
        /// `initialLoadDidRedirectTo` rather than a Universal Link: a link the presenting app itself
        /// claims is not reliably handed back to it from inside its own `SFSafariViewController`, and
        /// this delegate callback is the platform's documented way to watch a hosted checkout finish.
        /// The URL is matched on its **host** only — see ``PaymentReturn/isReturn(_:)``.
        func safariViewController(_ controller: SFSafariViewController, initialLoadDidRedirectTo url: URL) {
            guard PaymentReturn.isReturn(url) else { return }
            controller.dismiss(animated: true) { [weak self] in self?.finishOnce() }
        }

        /// The two paths can both fire — a redirect that dismisses also ends with `…DidFinish` on some
        /// releases — and the model's poll must not be started twice against one session.
        private func finishOnce() {
            guard !hasFinished else { return }
            hasFinished = true
            onFinished()
        }
    }
}
