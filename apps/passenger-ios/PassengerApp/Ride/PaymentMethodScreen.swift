import MageRideShared
import SwiftUI

/// SCR-PI-016 — how this ride gets paid.
///
/// The cell: a scrimmed map with a bottom sheet over it, *"Payment method"*, a `glist` of rails and
/// one **Confirm** CTA. Each row is a glyph, a label with an optional caption, and the amount on the
/// right.
///
/// **Three rails and no surcharge anywhere**, which is the Definition-of-Done line *"no surcharge is
/// ever displayed on a ride"*. The +5 % the wireframe still draws belonged to OnePay, and AL-57
/// retired OnePay as a ride method; AL-59 did the same to the platform-merchant LankaQR, which
/// collapses into the driver's own QR. **Every row therefore shows the same total.** That is not a
/// simplification — it is the point, and it is why ``PaymentMethodState`` has no per-rail amount.
///
/// **The `DEFAULT` badge is the passenger's stored preference, not a constant.** The cell draws it on
/// Cash, which is what an account that has never chosen one has (US-22.4, AL-14); a passenger who set
/// the wallet in Settings sees it there instead, because the badge is a fact about them rather than
/// about the list.
///
/// **Δ the cell draws no navigation bar and no back control.** A pushed SwiftUI screen keeps its
/// edge-swipe either way — the same argument C097 made on SCR-PI-009 — and the way *forward* is
/// Confirm.
@MainActor
struct PaymentMethodScreen: View {

    @StateObject private var model: PaymentMethodModel

    /// SCR-PI-017, on the confirmed rail.
    let onConfirmed: (PaymentMethod) -> Void

    /// The wallet row's action when the balance is short. Rs 40 missing should be one tap from
    /// fixed, not a disabled row.
    let onTopUp: () -> Void

    init(
        rideId: String,
        rides: RideRepository,
        sessions: PassengerSessions,
        preferences: AppPreferences,
        selection: PaymentSelection,
        onConfirmed: @escaping (PaymentMethod) -> Void,
        onTopUp: @escaping () -> Void
    ) {
        _model = StateObject(
            wrappedValue: PaymentMethodModel(
                rideId: rideId,
                rides: rides,
                sessions: sessions,
                preferences: preferences,
                selection: selection
            )
        )
        self.onConfirmed = onConfirmed
        self.onTopUp = onTopUp
    }

    var body: some View {
        ScrimmedSheet(pins: pins, camera: camera) {
            Text(key: "payment_title")
                .mageFont(.title)
                .foregroundStyle(MageRideColor.onSurface)
                .frame(maxWidth: .infinity, alignment: .leading)

            GroupedList {
                // Keyed on the rail's own wire value, and the last one asked for by identity rather
                // than by index: **Swift has no key paths into tuples**, so
                // `ForEach(Array(x.enumerated()), id: \.element.wire)` does not compile — the finding
                // `apps/driver-ios` records from C087 onwards.
                ForEach(model.state.rails, id: \.wire) { rail in
                    RailRow(
                        rail: rail,
                        state: model.state,
                        isLast: rail == model.state.rails.last,
                        onSelect: { model.choose(rail) },
                        onTopUp: onTopUp
                    )
                }
            }

            if let errorKey = model.state.errorKey {
                FormErrorText(messageKey: errorKey)
            }

            Button { model.confirm() } label: { Text(key: "payment_confirm") }
                .buttonStyle(.mageCta)
        }
        .toolbar(.hidden, for: .navigationBar)
        .task { model.start() }
        // A `Bool` rather than the chosen `PaymentMethod?`: `onChange(of:)` needs `Equatable`, and an
        // exported Kotlin enum is a class — asking the compiler whether two of them are equal is a
        // question this host cannot answer, and the rail is already on the model.
        .onChange(of: model.state.isConfirmed) { confirmed in
            guard confirmed else { return }
            onConfirmed(model.state.chosen)
            model.onConfirmConsumed()
        }
    }

    private var pins: [MapPin] {
        guard let ride = model.state.ride else { return [] }
        return [
            MapPin(kind: VehicleLayers.pinPickup, lat: ride.pickup.lat, lng: ride.pickup.lng),
            MapPin(kind: VehicleLayers.pinDropoff, lat: ride.dropoff.lat, lng: ride.dropoff.lng),
        ]
    }

    private var camera: MapCamera {
        guard let dropoff = model.state.ride?.dropoff else { return .colombo }
        return MapCamera(lat: dropoff.lat, lng: dropoff.lng)
    }
}

/// One rail.
///
/// The amount on the right is the **same on every row** — there is no rail that costs more, because
/// the one that did was retired. A row that recomputed a total would be reintroducing the surcharge
/// this screen exists without.
private struct RailRow: View {

    let rail: PaymentMethod
    let state: PaymentMethodState
    let isLast: Bool
    let onSelect: () -> Void
    let onTopUp: () -> Void

    var body: some View {
        VStack(spacing: 0) {
            Button(action: onSelect) {
                HStack(spacing: MageRideSpacing.sm) {
                    Image(systemName: symbolName)
                        .font(.system(size: MageRideControl.rowIcon))
                        .foregroundStyle(isSelected ? MageRideColor.primary : MageRideColor.onSurfaceVariant)
                        .frame(width: MageRideControl.listRowIcon)

                    VStack(alignment: .leading, spacing: 1) {
                        HStack(spacing: MageRideSpacing.xxs) {
                            Text(key: PaymentRails.labelKey(rail))
                                .mageFont(.body)
                                .foregroundStyle(MageRideColor.onSurface)
                            if rail == state.preferred {
                                SolidBadge(text: "payment_default".localised, color: MageRideColor.success)
                            }
                        }
                        Text(caption)
                            .mageFont(.caption)
                            .foregroundStyle(isShort ? MageRideColor.error : MageRideColor.onSurfaceVariant)
                            .multilineTextAlignment(.leading)
                    }

                    Spacer(minLength: MageRideSpacing.xs)
                    trailing
                }
                .padding(.horizontal, MageRideSpacing.sm)
                .frame(minHeight: MageRideControl.minimumTapTarget + MageRideSpacing.xs)
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)

            if !isLast {
                Rectangle()
                    .fill(MageRideColor.surfaceVariant)
                    .frame(height: MageRideControl.hairline)
                    .padding(.leading, MageRideSpacing.sm)
            }
        }
        .accessibilityElement(children: .combine)
        .accessibilityAddTraits(isSelected ? [.isButton, .isSelected] : .isButton)
    }

    @ViewBuilder
    private var trailing: some View {
        if isShort {
            TextLink(key: "payment_top_up", action: onTopUp)
        } else {
            // The same total on every row. See the type's note.
            Text(state.amountMinor.map(MoneyFormat.rupees) ?? MoneyFormat.pending)
                .mageFont(.subtitle)
                .foregroundStyle(isSelected ? MageRideColor.primary : MageRideColor.onSurface)
        }
    }

    private var isSelected: Bool { rail == state.chosen }

    /// Only the wallet can be short, and only once a balance has been read.
    private var isShort: Bool { rail == PaymentMethod.wallet && state.walletIsShort }

    /// The rail's own one-liner, except on the wallet — where the balance is the useful sentence and
    /// is what decides whether the row offers a selection or a top-up.
    private var caption: String {
        guard rail == PaymentMethod.wallet, let balance = state.walletBalanceMinor else {
            return PaymentRails.captionKey(rail).localised
        }
        return "payment_wallet_balance".localisedFormat(MoneyFormat.rupees(balance))
    }

    /// The cell's emoji, as SF Symbols. `💵 / 🏦 / 💳` in the drawing; a glyph from the system set
    /// here, which is what every other row in this app draws and what Dynamic Type scales.
    private var symbolName: String {
        switch rail {
        case PaymentMethod.wallet: return "wallet.pass.fill"
        case PaymentMethod.scanDriverQr: return "qrcode.viewfinder"
        case PaymentMethod.cod: return "shippingbox.fill"
        default: return "banknote.fill"
        }
    }
}
