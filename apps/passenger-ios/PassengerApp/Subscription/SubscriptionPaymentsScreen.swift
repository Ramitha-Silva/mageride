import MageRideShared
import SwiftUI

/// SCR-PI-025b — *"Payment history"*.
///
/// The cell: `‹ Back · Payment history`, a `card fill` carrying the vehicle and `Rs 6,000/mo`, then a
/// card per month — the month and a status pill on one row, `6 Jun · LankaQR` and the amount on the
/// `kv` row under it — with `Apr 2026 · Paid · cash` reading *"marked received by owner"*, because
/// that is literally who wrote the row (US-23.6).
///
/// **Three statuses, and the middle one is the point.** *Paid* is settled, *Pending verification* is
/// an online transfer whose slip the owner has not confirmed yet (US-23.4), and *Paid · cash* is the
/// owner's own attestation. A payment sitting at `initiated` — a gateway hand-off the passenger
/// abandoned — is shown as unpaid rather than as pending: an initiated payment is not a promise of
/// money, which is `ModeBPaymentRules.monthStatus`'s rule and this screen's too.
@MainActor
struct SubscriptionPaymentsScreen: View {

    @StateObject private var model: SubscriptionPaymentsModel

    init(subscriptionId: String, subscriptions: SubscriptionRepository, sessions: PassengerSessions) {
        _model = StateObject(
            wrappedValue: SubscriptionPaymentsModel(
                subscriptionId: subscriptionId,
                subscriptions: subscriptions,
                sessions: sessions
            )
        )
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: MageRideSpacing.xs) {
                header

                if let errorKey = model.state.errorKey {
                    FormErrorText(messageKey: errorKey)
                }

                if model.state.isLoading {
                    LoadingRow(messageKey: "subscription_payments_loading")
                } else if model.state.isEmpty {
                    Text(key: "subscription_payments_empty")
                        .mageFont(.body)
                        .foregroundStyle(MageRideColor.onSurfaceVariant)
                        .multilineTextAlignment(.center)
                        .frame(maxWidth: .infinity)
                        .padding(.top, MageRideSpacing.xl)
                } else {
                    ForEach(model.state.payments, id: \.paymentId) { payment in
                        PaymentRow(payment: payment)
                    }
                }
            }
            .padding(MageRideSpacing.md)
        }
        .background(MageRideColor.surface)
        .refreshable { await model.refresh() }
        .navigationTitle(Text(key: "subscription_payments_title"))
        .navigationBarTitleDisplayMode(.inline)
        .task { await model.refresh() }
    }

    // MARK: -

    /// The wireframe's filled header: the vehicle and its monthly fare.
    private var header: some View {
        HStack(alignment: .firstTextBaseline, spacing: MageRideSpacing.xs) {
            // The Vehicle ID — the same gap SCR-PI-025's card has. See ``SubscriptionsScreen``.
            Text(model.state.subscription?.vehicleId ?? "")
                .mageFont(.subtitle)
                .foregroundStyle(MageRideColor.onSurface)

            Spacer(minLength: MageRideSpacing.xs)

            if let fare = model.state.fare {
                Text("subscriptions_per_month".localisedFormat(MoneyFormat.rupees(fare)))
                    .mageFont(.label)
                    .foregroundStyle(MageRideColor.onSurfaceVariant)
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

/// One month.
private struct PaymentRow: View {

    let payment: SubscriptionPayment

    var body: some View {
        VStack(alignment: .leading, spacing: MageRideSpacing.xxs) {
            HStack(spacing: MageRideSpacing.xs) {
                Text(TripLabels.monthYear(payment.periodMonth))
                    .mageFont(.body)
                    .foregroundStyle(MageRideColor.onSurface)
                Spacer(minLength: MageRideSpacing.xs)
                StatusPill(titleKey: pill.titleKey, tone: pill.tone)
            }

            HStack(alignment: .firstTextBaseline, spacing: MageRideSpacing.xs) {
                Text(SubscriptionLabels.paymentLine(payment))
                    .mageFont(.caption)
                    .foregroundStyle(MageRideColor.onSurfaceVariant)
                Spacer(minLength: MageRideSpacing.xs)
                Text(MoneyFormat.rupees(payment.money))
                    .mageFont(.subtitle)
                    .foregroundStyle(MageRideColor.onSurface)
            }

            // The wireframe's *"marked by owner"* line, on the one method that has no other evidence
            // behind it: cash never touches a gateway (US-23.6).
            if isOwnerMarkedCash {
                Text(key: "subscription_cash_marked")
                    .mageFont(.caption)
                    .foregroundStyle(MageRideColor.onSurfaceVariant)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(MageRideSpacing.sm)
        .background(
            MageRideColor.surfaceVariant,
            in: RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
        )
        // One row, one announcement: *"June 2026, paid, six June, LankaQR, six thousand rupees"*
        // rather than five elements a reader has to assemble.
        .accessibilityElement(children: .combine)
    }

    private var pill: (titleKey: String, tone: StatusPill.Tone) { SubscriptionLabels.paymentPill(payment) }

    private var isOwnerMarkedCash: Bool {
        payment.method == SubscriptionPayMethod.cash && payment.status == SubscriptionPaymentStatus.paid
    }
}
