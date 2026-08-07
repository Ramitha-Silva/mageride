import MageRideShared
import SwiftUI

/// SCR-PI-007 — the Mode A vehicle popup.
///
/// The cell: a grabber, a `MODE A · BUS` badge, the vehicle's disc beside its headline and second
/// line, and two `.card.fill` tiles — **Distance** and **ETA**. Its own `Δ iOS` clause is
/// `.sheet(.height(220))`, which is ``MageRideControl/vehiclePopupHeight``.
///
/// **Mode A only, and that is a fence rather than a layout choice** (AL-23, US-7.4). A Mode B marker
/// routes to SCR-PI-024 and a Mode C marker does nothing; neither ever reaches this view, because
/// ``LiveMapModel/onMarkerTapped(_:)`` decides by mode before a sheet exists.
///
/// **The distance is computed here; the ETA is the server's.** `VehicleFrame` is a position and
/// carries neither — so the popup measures the straight-line distance itself with `:shared`'s
/// haversine, and the ETA, driver and plate arrive separately as a ``VehicleDetail`` from
/// `GET /v1/nearby`. **A straight line is not a road**, so an ETA derived from the distance here
/// would be a number a passenger plans around and the platform never promised; until the lookup
/// lands, or when it fails, the tile says so.
///
/// **The driver's name is Mode A's alone** (US-7.12). A bus driver is public information; a Mode C
/// driver's name is not the passenger's to see until the ride is accepted — and no Mode C vehicle
/// reaches this view at all.
///
/// ### Two things the wireframe draws and no contract carries
///
/// - **The route.** The cell's headline is *"Route 138 — Pettah → Maharagama"*, and neither
///   `VehicleFrame` nor `NearbyVehicle` has a route number — so the **vehicle type** is the honest
///   headline rather than an invented one. A `query.yaml` change, not an app change.
/// - **`seen 6s ago`.** `VehicleFrame` carries no sample timestamp. The client knows when *it*
///   received a frame, not when the sample was taken, and the two differ by exactly the lag the
///   label exists to communicate — so the pill is not drawn.
///
/// Both were recorded by C078 from the Android side and are restated in the C096 handoff.
struct VehiclePopup: View {

    let vehicle: VehicleFrame
    let detail: VehicleDetail?
    let around: MapFix?

    var body: some View {
        VStack(alignment: .leading, spacing: MageRideSpacing.sm) {
            badge

            HStack(spacing: MageRideSpacing.sm) {
                token.image
                    .font(.system(size: MageRideControl.rowIcon))
                    .foregroundStyle(MageRideColor.onStatus)
                    .frame(width: MageRideControl.avatarSmall, height: MageRideControl.avatarSmall)
                    .background(token.color, in: Circle())

                VStack(alignment: .leading, spacing: 1) {
                    Text(key: token.nameKey)
                        .mageFont(.subtitle)
                        .foregroundStyle(MageRideColor.onSurface)
                    // The wireframe's `NB-4521 · K. Perera`. The plate is the vehicle's public
                    // identity and the id is the fallback, because a passenger comparing what is on
                    // screen with what is in front of them reads a number plate.
                    Text(subtitle)
                        .mageFont(.caption)
                        .foregroundStyle(MageRideColor.onSurfaceVariant)
                        .lineLimit(1)
                }

                Spacer(minLength: 0)
            }

            HStack(spacing: MageRideSpacing.xs) {
                MetricTile(titleKey: "popup_distance", value: MapFormat.distance(from: around, to: vehicle.point))
                MetricTile(titleKey: "popup_eta", value: MapFormat.eta(seconds: detail?.etaSeconds))
            }
        }
        .padding(.horizontal, MageRideSpacing.md)
        .padding(.top, MageRideSpacing.md)
        .padding(.bottom, MageRideSpacing.lg)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(MageRideColor.surface)
        .presentationDetents([.height(MageRideControl.vehiclePopupHeight)])
        .presentationDragIndicator(.visible)
    }

    /// The wireframe's `MODE A · BUS` pill.
    ///
    /// One format string rather than three concatenated `Text`s: the word *Mode* is copy and is
    /// translated, the `A` is a letter that is the same in all three scripts (see
    /// ``ModeToken/badge``), and the type name is looked up on its own key. `.textCase(.uppercase)`
    /// is what the cell's capitals are; it is a no-op in Sinhala and Tamil, which have no case.
    private var badge: some View {
        Text("popup_mode_badge".localisedFormat(ModeToken.a.badge, token.nameKey.localised))
            .mageFont(.label)
            .textCase(.uppercase)
            .foregroundStyle(MageRideColor.onStatus)
            .padding(.horizontal, MageRideSpacing.xs)
            .padding(.vertical, MageRideSpacing.xxs / 2)
            .background(
                ModeToken.a.color,
                in: RoundedRectangle(cornerRadius: MageRideRadius.sm, style: .continuous)
            )
    }

    /// The vehicle's legend token — §0.2's grey `private` row when the frame carried no type, which
    /// is what keeps the disc and the headline agreeing about an unknown vehicle.
    private var token: VehicleToken {
        VehicleToken.forType(vehicle.type) ?? .privateHire
    }

    /// The wireframe's second line — plate, driver, or both; the vehicle id when the lookup has not
    /// landed or came back with neither, rather than an empty row where a plate should be.
    private var subtitle: String {
        var parts: [String] = []
        if let plate = detail?.registrationNumber, !plate.isEmpty {
            parts.append(plate)
        }
        if let driver = detail?.driverName, !driver.isEmpty {
            parts.append("popup_driver".localisedFormat(driver))
        }
        return parts.isEmpty ? vehicle.vehicleId : parts.joined(separator: MageRideSymbols.separator)
    }
}
