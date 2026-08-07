import SwiftUI
import UIKit

/// `passenger_ios.html`'s cluster-4 shapes — the scrimmed sheet, the radar, the driver row, the star
/// strip, the OTP card and the receipt's key/value line.
///
/// Same rule as ``FormControls``, ``OnboardingControls``, ``MapControls`` and ``BookingControls``:
/// every measurement comes from ``MageRideSpacing`` / ``MageRideRadius`` / ``MageRideControl``, and a
/// later screen group takes a shape from here rather than redrawing one. C099's package screens want
/// ``DriverIdentityRow`` and ``KeyValueRow``; C102's SCR-PI-028 wants the identity row too.

// MARK: - Sheets over a map

/// A **destination** the wireframe draws as a sheet over a scrimmed map — SCR-PI-016 and SCR-PI-019.
///
/// Both cells draw `.map > .scrim` with an `.overlay.sheet` above it, and neither is a presented
/// `.sheet`: both are entries in ``PassengerRoute``, because both are places a passenger is *taken*
/// (a completed ride hands over to the first, the receipt to the second) rather than things they open
/// and dismiss. The scrim is therefore drawn rather than presented, exactly as ``LiveMapScreen``
/// draws its own panel for the reason that cell gives.
///
/// The map behind is a **backdrop**: it carries the trip's two pins so the passenger can see where
/// they have been, and takes no interaction. `pins` empty draws a plain map, which is what a ride the
/// screen could not read looks like.
struct ScrimmedSheet<Content: View>: View {

    var pins: [MapPin] = []
    var camera: MapCamera = .colombo
    @ViewBuilder let content: Content

    var body: some View {
        ZStack(alignment: .bottom) {
            MageRideMap(pins: pins, camera: camera)
                .allowsHitTesting(false)
                .overlay(MageRideColor.onSurface.opacity(0.35))
                .ignoresSafeArea(edges: .top)

            VStack(spacing: MageRideSpacing.sm) {
                SheetGrabber()
                content
            }
            .padding(MageRideSpacing.md)
            .frame(maxWidth: .infinity)
            .background(
                MageRideColor.background,
                in: TopRoundedRectangle(radius: MageRideRadius.card)
            )
        }
        .background(MageRideColor.surface)
    }
}

// MARK: - SCR-PI-014's radar

/// The wireframe's `.pulse` — a widening ring around a fixed core.
///
/// **A `TimelineView`, which is the cell's own `Δ iOS` clause** (*"`TimelineView` radar"*). It is also
/// the right primitive: the animation is a pure function of the clock, so there is no
/// `@State`, nothing to invalidate and nothing left running when the view goes away — where the
/// Android twin drives an `InfiniteTransition` it has to remember to key.
///
/// `.animation` schedule rather than `.periodic`: the system throttles it when the screen is off or
/// the view is occluded, and a search that lasts two minutes should not spend a battery on a ring
/// nobody is looking at. Reduce Motion is respected by the schedule itself.
struct RadarPulse: View {

    var body: some View {
        TimelineView(.animation) { context in
            let progress = RadarPulse.progress(at: context.date)
            ZStack {
                Circle()
                    .fill(MageRideColor.primary.opacity(1 - progress))
                    .scaleEffect(progress)
                Circle()
                    .fill(MageRideColor.primary)
                    .scaleEffect(RadarPulse.coreFraction)
                Image(systemName: "magnifyingglass")
                    .font(.system(size: MageRideControl.rowIcon, weight: .semibold))
                    .foregroundStyle(MageRideColor.onPrimary)
            }
            .frame(width: MageRideControl.radar, height: MageRideControl.radar)
        }
        .accessibilityHidden(true)
    }

    /// Where in the sweep `date` falls, in `0..<1`.
    ///
    /// Off the absolute clock rather than off an elapsed time held in state, so two rings drawn a
    /// frame apart are in the same place and nothing has to be reset when the view is rebuilt.
    static func progress(at date: Date) -> Double {
        let period = 1.6
        return date.timeIntervalSince1970.truncatingRemainder(dividingBy: period) / period
    }

    /// The solid centre, as a fraction of the ring. The wireframe's own dot.
    static let coreFraction: Double = 0.34
}

// MARK: - The driver

/// The wireframe's `.row` — `👤 K. Fernando ★4.8` over `Sedan · ABC-1234 · 3 min`.
///
/// Drawn on SCR-PI-015, on SCR-PI-015a's header and on SCR-PI-019, which is why it is here rather
/// than inside one of them. **Everything is optional**, because everything is: a ride before
/// acceptance has no driver at all, `RideDriver.rating` is nullable, and a plate arrives with the
/// vehicle rather than with the person.
struct DriverIdentityRow: View {

    let name: String?
    var rating: Double?
    var vehicle: String?
    var plate: String?
    var etaMinutes: Int32?
    var size: CGFloat = MageRideControl.avatarSmall

    var body: some View {
        HStack(spacing: MageRideSpacing.sm) {
            Circle()
                .fill(MageRideColor.surfaceVariant)
                .frame(width: size, height: size)
                .overlay {
                    Image(systemName: "person.fill")
                        .font(.system(size: size * 0.45))
                        .foregroundStyle(MageRideColor.outlineVariant)
                }

            VStack(alignment: .leading, spacing: 1) {
                HStack(spacing: MageRideSpacing.xxs) {
                    // `call_driver` rather than a second "we do not know their name" key: a ride
                    // before acceptance has no driver, and *"Your driver"* is exactly what the call
                    // sheet already says in that state.
                    Text(name ?? "call_driver".localised)
                        .mageFont(.subtitle)
                        .foregroundStyle(MageRideColor.onSurface)
                    if let rating {
                        Text("ride_rating".localisedFormat(rating))
                            .mageFont(.caption)
                            .foregroundStyle(MageRideColor.warning)
                    }
                }
                if let subtitle {
                    Text(subtitle)
                        .mageFont(.caption)
                        .foregroundStyle(MageRideColor.onSurfaceVariant)
                }
            }

            Spacer(minLength: 0)
        }
        .accessibilityElement(children: .combine)
    }

    /// `Sedan · ABC-1234 · 3 min`, with whatever the ride actually carries.
    private var subtitle: String? {
        let parts = [vehicle, plate, etaMinutes.map { "ride_arriving_minutes".localisedFormat(Int($0)) }]
        let joined = parts.compactMap { $0 }.joined(separator: MageRideSymbols.separator)
        return joined.isEmpty ? nil : joined
    }
}

// MARK: - The start code

/// The wireframe's `card fill` around *"Start OTP — give to driver"* and its four boxes.
///
/// **`code` is `nil` on every ride this build can produce, and that is a recorded platform gap rather
/// than a missing read.** `ride.yaml`'s `pickupOtp` is *"package bookings only"*, no operation returns
/// a rider start OTP, and ride-svc's own contracts say a start OTP is *"accepted and ignored in this
/// build: no endpoint issues one"* — the same hole `backend/src/PublicBff` recorded from the share-link
/// side. So the card is drawn as the cell draws it, the boxes are empty, and the caption says the
/// trip starts without one. Filling them with four digits nobody could check would be worse than
/// admitting it.
struct StartCodeCard: View {

    let code: String?

    var body: some View {
        VStack(spacing: MageRideSpacing.xs) {
            Text(key: "ride_start_otp")
                .mageFont(.label)
                .foregroundStyle(MageRideColor.onSurfaceVariant)

            HStack(spacing: MageRideSpacing.xs) {
                ForEach(0..<StartCodeCard.digits, id: \.self) { index in
                    Text(digit(at: index))
                        .mageFont(.title)
                        .foregroundStyle(MageRideColor.onSurface)
                        .frame(minWidth: MageRideControl.otpBox, minHeight: MageRideControl.otpBox)
                        .background(
                            code == nil ? MageRideColor.surfaceVariant : MageRideColor.primaryContainer,
                            in: RoundedRectangle(cornerRadius: MageRideRadius.sm, style: .continuous)
                        )
                }
            }

            if code == nil {
                Text(key: "ride_start_otp_unavailable")
                    .mageFont(.caption)
                    .foregroundStyle(MageRideColor.onSurfaceVariant)
                    .multilineTextAlignment(.center)
            }
        }
        .frame(maxWidth: .infinity)
        .padding(MageRideSpacing.sm)
        .background(
            MageRideColor.surfaceVariant,
            in: RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
        )
        .accessibilityElement(children: .combine)
    }

    private func digit(at index: Int) -> String {
        let characters = Array(code ?? "")
        return index < characters.count ? String(characters[index]) : " "
    }

    /// `^\d{4}$` — the pattern every OTP on this platform is fixed to.
    static let digits = 4
}

// MARK: - Stars

/// SCR-PI-019's `★★★★☆`, tappable.
///
/// **`onSelect` is what makes it a control**; passing `nil` draws the same strip read-only, which is
/// what C099's history rows want. The cell asks for *"star tap scale + haptic"* and both are here:
/// the scale is on the selected star and the haptic is `UIImpactFeedbackGenerator`, the same
/// `.impact(.light)` SCR-PI-006's chips use.
struct StarRating: View {

    let value: Int
    var maximum: Int = 5
    var size: CGFloat = MageRideControl.star
    var onSelect: ((Int) -> Void)?

    var body: some View {
        HStack(spacing: MageRideSpacing.xs) {
            ForEach(1...maximum, id: \.self) { star in
                Image(systemName: star <= value ? "star.fill" : "star")
                    .font(.system(size: size))
                    .foregroundStyle(star <= value ? MageRideColor.warning : MageRideColor.outline)
                    .scaleEffect(star == value ? 1.12 : 1)
                    .animation(.spring(response: 0.25, dampingFraction: 0.6), value: value)
                    .contentShape(Rectangle())
                    .onTapGesture {
                        guard let onSelect else { return }
                        UIImpactFeedbackGenerator(style: .light).impactOccurred()
                        onSelect(star)
                    }
                    .accessibilityLabel(Text("rate_stars".localisedFormat(star)))
                    .accessibilityAddTraits(star == value ? [.isButton, .isSelected] : .isButton)
            }
        }
        .accessibilityRepresentation {
            // One adjustable control rather than five buttons: a VoiceOver user setting a rating is
            // choosing a value, and swiping through five identical "N stars" buttons is not that.
            Stepper(value: .constant(value), in: 1...maximum) {
                Text("rate_stars".localisedFormat(value))
            }
        }
    }
}

// MARK: - The receipt

/// SCR-PI-018's `.kv` — a caption on the left and a value on the right.
///
/// `isTotal` draws the wireframe's own emphasised final row (`font-weight:700` over a hairline).
struct KeyValueRow: View {

    let titleKey: String
    let value: String
    var isTotal = false

    var body: some View {
        VStack(spacing: MageRideSpacing.xs) {
            if isTotal {
                Rectangle()
                    .fill(MageRideColor.outline)
                    .frame(height: MageRideControl.hairline)
            }
            HStack {
                Text(key: titleKey)
                    .mageFont(isTotal ? .bodyEmphasis : .bodySmall)
                    .foregroundStyle(isTotal ? MageRideColor.onSurface : MageRideColor.onSurfaceVariant)
                Spacer(minLength: MageRideSpacing.xs)
                Text(value)
                    .mageFont(isTotal ? .bodyEmphasis : .bodySmall)
                    .foregroundStyle(MageRideColor.onSurface)
            }
        }
        .accessibilityElement(children: .combine)
    }
}

/// The wireframe's `h-display` amount under a `t-label` caption — SCR-PI-017's *Amount due* and
/// SCR-PI-018's *Total paid*.
struct AmountHeadline: View {

    let captionKey: String
    let amountMinor: Int64?

    var body: some View {
        VStack(spacing: MageRideSpacing.xxs) {
            Text(key: captionKey)
                .mageFont(.label)
                .foregroundStyle(MageRideColor.onSurfaceVariant)
            Text(amountMinor.map(MoneyFormat.rupees) ?? MoneyFormat.pending)
                .mageFont(.display)
                .foregroundStyle(MageRideColor.onSurface)
        }
        .frame(maxWidth: .infinity)
        .accessibilityElement(children: .combine)
    }
}
