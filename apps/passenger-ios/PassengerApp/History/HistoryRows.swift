import MageRideShared
import SwiftUI

// SCR-PI-022's two cards, split out of `TripHistoryScreen.swift` so neither file is a wall — the
// same split `Booking/BookingRows.swift` makes for SCR-PI-009's two lists.
//
// The screen is the layout: the large title, the segmented tabs, the list and the chooser. This is
// what goes inside it.

/// One finished trip — the wireframe's `.card`.
///
/// ```
/// Nugegoda → Galle Face                    [Paid]
/// 17 Jun                                   Rs 850
/// 🚗 K. Fernando · +9477*****67              Call
/// ```
///
/// **Three things the cell draws are not on the row, and none of them is drawn wrong instead.**
/// `RideHistoryRow` carries no distance and no vehicle type, so the caption is the date alone rather
/// than *"17 Jun · 8.2 km · Sedan"* — both figures exist on query-svc's `TripDetail`, which is
/// SCR-PI-023's read and is one tap away. And the number is the **masked** one the list carries; the
/// Call resolves the real one (AL-48). All three are recorded in the C099 handoff.
///
/// **The driver block is absent entirely on a ride cancelled before assignment.** There was no
/// driver, and a contact row for one would be a fiction — this component's own fence, and what the
/// wireframe's third card draws.
struct TripHistoryCard: View {

    let row: RideHistoryRow
    let onOpen: () -> Void
    let onCall: () -> Void

    var body: some View {
        Button(action: onOpen) {
            VStack(alignment: .leading, spacing: MageRideSpacing.xs) {
                HStack(alignment: .top, spacing: MageRideSpacing.xs) {
                    Text(TripLabels.route(pickup: row.pickup?.address, dropoff: row.dropoff?.address))
                        .mageFont(.subtitle)
                        .foregroundStyle(MageRideColor.onSurface)
                        .multilineTextAlignment(.leading)
                    Spacer(minLength: 0)
                    StatusPill(titleKey: pill.key, tone: pill.tone)
                }

                HStack(spacing: MageRideSpacing.xs) {
                    Text(TripLabels.date(row.completedAt))
                        .mageFont(.bodySmall)
                        .foregroundStyle(MageRideColor.onSurfaceVariant)
                    Spacer(minLength: 0)
                    Text(row.fare.map { MoneyFormat.rupees($0.amountMinor) } ?? MoneyFormat.pending)
                        .mageFont(.bodyEmphasis)
                        .foregroundStyle(MageRideColor.onSurface)
                }

                if row.hasReachableDriver, let driver = row.driver {
                    HStack(spacing: MageRideSpacing.xs) {
                        Image(systemName: "car.fill")
                            .font(.system(size: MageRideControl.chipIcon))
                            .foregroundStyle(MageRideColor.onSurfaceVariant)
                        Text(driver.name + MageRideSymbols.separator + driver.mobileMasked)
                            .mageFont(.caption)
                            .foregroundStyle(MageRideColor.onSurfaceVariant)
                            .lineLimit(1)
                        Spacer(minLength: MageRideSpacing.xs)
                        // Its own control inside a card that is itself a button: SwiftUI gives the
                        // innermost button the tap, which is what a passenger reaching for *Call*
                        // expects. `.buttonStyle(.plain)` on both is what keeps that true.
                        TextLink(key: "ride_call", action: onCall)
                    }
                }
            }
            .padding(MageRideSpacing.sm)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(
                MageRideColor.background,
                in: RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
            )
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }

    private var pill: (key: String, tone: StatusPill.Tone) { RideStateLabel.pill(for: row.state) }
}

/// One upcoming scheduled ride.
///
/// **Nothing ever builds one today** — no read lists a passenger's own scheduled rides, so
/// ``HistoryRepository/scheduled(userId:)`` answers empty and the tab draws its empty state. The card
/// exists because the tab is one of the wireframe's three and because the day the route lands this is
/// what it renders; it is not dead code so much as the half of the feature that is ours.
struct ScheduledRideCard: View {

    let row: ScheduledRideRow

    var body: some View {
        VStack(alignment: .leading, spacing: MageRideSpacing.xxs) {
            Text(TripLabels.route(pickup: row.pickupLabel, dropoff: row.dropoffLabel))
                .mageFont(.subtitle)
                .foregroundStyle(MageRideColor.onSurface)
            Text(TripLabels.dateTime(row.pickupTime))
                .mageFont(.bodySmall)
                .foregroundStyle(MageRideColor.onSurfaceVariant)
        }
        .padding(MageRideSpacing.sm)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(
            MageRideColor.background,
            in: RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
        )
        .accessibilityElement(children: .combine)
    }
}
