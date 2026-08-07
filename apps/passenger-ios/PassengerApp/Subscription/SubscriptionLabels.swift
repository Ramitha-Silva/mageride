import MageRideShared
import SwiftUI

/// The four places a Mode B wire value becomes copy, in one file.
///
/// Same rule as ``RideStateLabel`` and ``VehicleLabels``: a `switch` over an exported enum that
/// produces a translated string belongs beside the screens that draw it, and there is one of it.
/// `SubscriptionFlowTests` walks these tables so a status that reached the default arm is a failing
/// build rather than a pill that says *"Checking…"* forever.
///
/// **The `·` between the fragments is punctuation, not copy.** Three identical values in the three
/// `.strings` files is what `LocalizationTests` reads as a translation nobody did — the same rule
/// ``MoneyFormat/prefix`` and ``TripLabels`` follow.
enum SubscriptionLabels {

    /// `Paid · Rs 6,000 / month · next due 6 Jul`, or the Free vehicle's one line.
    ///
    /// The leading word is the vehicle's **Service payment** classification (AL-51's rename of "Mode B
    /// classification"), not the state of this month — that is the pill's job, and the two are
    /// genuinely different: a Paid vehicle whose month is unpaid says `Paid` here and `Payment due`
    /// there.
    static func billingLine(_ card: SubscriptionCard) -> String {
        guard let fare = card.fare else { return "subscriptions_free_caption".localised }

        var parts = [
            "subscriptions_billing_paid".localised,
            "subscriptions_per_month".localisedFormat(MoneyFormat.rupees(fare)),
        ]
        if let nextDue = card.subscription.nextDue {
            parts.append("subscriptions_next_due".localisedFormat(TripLabels.dayMonth(nextDue)))
        }
        return parts.joined(separator: SubscriptionLabels.separator)
    }

    /// SCR-PI-025's card pill — `Paid` / `Pending verification` / `Payment due` / `Free` / `Checking…`.
    ///
    /// A Free vehicle collects nothing, so it never carries a month at all. `nil` is *still loading,
    /// or the statement could not be read*, and neither of those is "Paid".
    static func cardPill(_ card: SubscriptionCard) -> (titleKey: String, tone: StatusPill.Tone) {
        guard card.isPaid else { return ("subscriptions_free", .muted) }

        guard let status = card.monthStatus else { return ("subscriptions_status_unknown", .muted) }
        if status == SubscriberMonthStatus.paid { return ("subscriptions_status_paid", .ok) }
        if status == SubscriberMonthStatus.pendingVerification { return ("subscriptions_status_pending", .warning) }
        return ("subscriptions_status_due", .warning)
    }

    /// SCR-PI-025b's row pill — `Paid` / `Paid · cash` / `Pending verification` / `Not paid`.
    ///
    /// `initiated` is a hand-off nobody completed and `failed` is one that broke. Neither is money the
    /// owner has, so neither may look like it.
    static func paymentPill(_ payment: SubscriptionPayment) -> (titleKey: String, tone: StatusPill.Tone) {
        if payment.status == SubscriptionPaymentStatus.paid {
            // Cash is the owner's own attestation rather than a gateway's — the wireframe draws it
            // muted for exactly that reason (US-23.6).
            return payment.method == SubscriptionPayMethod.cash
                ? ("subscription_status_paid_cash", .muted)
                : ("subscription_status_paid", .ok)
        }
        if payment.status == SubscriptionPaymentStatus.pendingVerification {
            return ("subscription_status_pending", .warning)
        }
        return ("subscription_status_unpaid", .error)
    }

    /// `6 Jun · LankaQR — deep link` — SCR-PI-025b's `kv` line.
    ///
    /// The date is when the payment **settled** where there is one, and the period otherwise: an
    /// unpaid month has no `paidAt` and never will. Both are read in Colombo (D-38) — a `paidAt` is an
    /// instant, and the day it falls on differs from the handset's for five and a half hours a day.
    static func paymentLine(_ payment: SubscriptionPayment) -> String {
        // The closure rather than `map(TripLabels.date)`: that name is overloaded on `Timestamp` and
        // on `Date`, and an unapplied reference to it does not resolve.
        let day = payment.paidAt.map { TripLabels.date($0) } ?? TripLabels.dayMonth(payment.periodMonth)
        return day + SubscriptionLabels.separator + SubscriptionRails.labelKey(payment.method).localised
    }

    /// The wireframe's `·` between two fragments of one line.
    static let separator = " · "
}
