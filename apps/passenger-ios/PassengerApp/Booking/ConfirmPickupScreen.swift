import MageRideShared
import SwiftUI

/// SCR-PI-011 — the **rider's** side of a proxy pickup (P-02, P-13).
///
/// The cell: a warning banner carrying the countdown **and the privacy promise on the same line**, a
/// full-bleed map with the pin, and a sheet with *"<Booker> wants your pickup location"*, one line of
/// explanation and the Decline / Share pair.
///
/// **The promise is on screen before the decision, not after it.** *"Expires in 4:38 · declining
/// never shares GPS"* is one banner because those two facts belong together: a rider deciding
/// whether to share is deciding under time pressure, and the thing they most need to know is that
/// the other button costs them nothing.
///
/// **The pin is fixed and the map moves** — SCR-PI-011's *"drag to adjust"*, done the way every
/// centre-pin picker does it. ``MageRideMap`` `onCameraIdle` reports where it settled; the rider
/// never has to hit a small marker.
///
/// The booker's name comes from the push, not from a read: this screen is all the rider sees of the
/// booking, and agreeing to share your position with *"somebody"* is not consent.
@MainActor
struct ConfirmPickupScreen: View {

    @StateObject private var model: ConfirmPickupModel

    /// Who asked. From the silent FCM's `data.bookerName` — see ``PushRouter``.
    let bookerName: String?
    let onFinished: () -> Void

    init(
        requestId: String,
        bookings: BookingRepository,
        locations: PassengerLocationSource,
        bookerName: String?,
        onFinished: @escaping () -> Void
    ) {
        _model = StateObject(
            wrappedValue: ConfirmPickupModel(requestId: requestId, bookings: bookings, locations: locations)
        )
        self.bookerName = bookerName
        self.onFinished = onFinished
    }

    var body: some View {
        VStack(spacing: 0) {
            FormattedBanner(
                messageKey: "confirm_pickup_banner",
                arguments: [model.state.countdown],
                tone: .warning,
                symbolName: "clock"
            )
            .padding(.horizontal, MageRideSpacing.sm)
            .padding(.bottom, MageRideSpacing.xs)

            ZStack {
                MageRideMap(
                    camera: model.state.pin.map { MapCamera(lat: $0.lat, lng: $0.lng) } ?? .colombo,
                    onCameraIdle: { model.onPinMoved($0) }
                )
                Image(systemName: "mappin")
                    .font(.system(size: MageRideControl.listRowIcon))
                    .foregroundStyle(MageRideColor.success)
                    .accessibilityHidden(true)
            }
            .overlay(alignment: .bottom) {
                // The centre-pin pattern needs saying once: nothing on screen looks draggable,
                // because the thing that moves is the map.
                MapNotice(messageKey: "confirm_pickup_drag")
            }

            sheet
        }
        .background(MageRideColor.surface)
        .toolbar(.hidden, for: .navigationBar)
        .task { model.start() }
        .onChange(of: model.state.outcome) { outcome in
            // Every terminal state closes the screen — shared, refused or expired, the rider has
            // nothing left to do and the request is gone.
            guard outcome != nil else { return }
            onFinished()
        }
    }

    private var sheet: some View {
        VStack(alignment: .leading, spacing: MageRideSpacing.xs) {
            SheetGrabber()
                .frame(maxWidth: .infinity)

            // The booker's name when the push carried one, and an honest *"Someone"* when it did
            // not — agreeing to share your position with somebody unnamed is still a decision the
            // rider gets to make with what the platform actually told them.
            Text(bookerName.map { "confirm_pickup_who".localisedFormat($0) } ?? "confirm_pickup_who_unknown".localised)
                .mageFont(.subtitle)
                .foregroundStyle(MageRideColor.onSurface)

            Text(key: "confirm_pickup_why")
                .mageFont(.bodySmall)
                .foregroundStyle(MageRideColor.onSurfaceVariant)
                .padding(.bottom, MageRideSpacing.xs)

            if let errorKey = model.state.errorKey {
                FormErrorText(messageKey: errorKey)
            }

            HStack(spacing: MageRideSpacing.xs) {
                OutlinedAction(titleKey: "confirm_pickup_decline") { model.decline() }
                Button { model.share() } label: {
                    Text(key: "confirm_pickup_share")
                }
                .buttonStyle(.mageCta(loading: model.state.isSending))
                .disabled(!model.state.canShare)
            }
        }
        .padding(.horizontal, MageRideSpacing.md)
        .padding(.top, MageRideSpacing.xs)
        .padding(.bottom, MageRideSpacing.lg)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(MageRideColor.background, in: TopRoundedRectangle(radius: MageRideRadius.lg))
        .mageElevated()
    }
}
