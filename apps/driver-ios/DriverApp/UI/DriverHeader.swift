import SwiftUI

/// Who the driver is, as both SCR-DI-029 and SCR-DI-036 show it (Δ MCS-24).
///
/// - Parameters:
///   - name: The driver's own name. `nil` renders the "not set yet" copy rather than a blank.
///   - level: Driver Level 1–3 (D5' §4.2). `nil` while the read is in flight.
///   - registration: The plate of the vehicle this handset is live for (D-03), or `nil` when
///     nothing is eligible — a driver who has not onboarded one, or whose only vehicle is suspended.
///   - rating: The star average out of 5.
///
///     **Always `nil` today, and that is a spec gap rather than an omission.** No app-facing read
///     carries a driver's own average: `GET /v1/users/me` has no such field,
///     `GET /v1/drivers/{id}/level` answers points and not stars, `trips.ratings` has no aggregate
///     read, and the only place a driver `rating` appears anywhere on the surface is
///     `RideDetail.driver.rating` — the number a *passenger* is shown about the driver of *their*
///     ride. Four components have now reached that conclusion independently and each drew an em
///     dash. This carries the parameter so the day a read exists, one type gains a value and both
///     screens show it.
///   - photoUrl: The driver's own photograph, absolute and ready to load (Δ MCS-25).
///
///     **registry-svc's, not `GET /v1/users/me`'s.** This was a `hasPhoto` flag fed from
///     `UserProfile.photoUrl`, which is `nil` for every driver who onboarded in this app: Profile
///     Setup writes `registry.driver_profiles` and never touches `iam.users` (D3'
///     §`getDriverProfile`). `nil` draws the glyph — before the read answers, and for a driver
///     whose photo PDPA erasure has cleared.
struct DriverHeaderState {

    var name: String?
    var level: Int32?
    var registration: String?
    var rating: Double?
    var photoUrl: String?
}

/// The avatar, the name and level, and the vehicle and rating line.
///
/// **One view for two screens, deliberately.** The wireframes draw the same block at the top of the
/// Menu tab and the top of the profile screen, and before this they were two independent pieces of
/// layout that had already drifted: the menu showed a generic "driver" label above the platform id,
/// and the profile showed the driver's name above that same id — neither of which answers "who am I
/// and what am I driving".
///
/// The photo is the placeholder glyph. `UserProfile.photoUrl` is a URL and nothing in this target
/// loads a remote image (``CaptureTile`` deliberately never holds one either), so drawing the glyph
/// is the honest state — a grey circle that never resolves would read as a failed load rather than
/// as a feature that is not built.
struct DriverHeader<Trailing: View>: View {

    let state: DriverHeaderState
    @ViewBuilder var trailing: () -> Trailing

    var body: some View {
        HStack(spacing: MageRideSpacing.sm) {
            // The glyph is drawn underneath and the photograph over it, so it is also what shows
            // while the load is in flight and what is left if it fails. `AsyncImage`'s phase-based
            // form would give explicit placeholder and failure branches; this needs neither,
            // because the right thing to draw in both is the thing already there.
            ZStack {
                Image(systemName: "person.crop.circle.fill")
                    .font(.system(size: MageRideControl.avatarSmall))
                    .foregroundStyle(MageRideColor.onSurfaceVariant)

                if let photoUrl = state.photoUrl, let url = URL(string: photoUrl) {
                    AsyncImage(url: url) { image in
                        image
                            .resizable()
                            // The stored photograph is whatever shape the handset camera took and
                            // the wireframe's avatar is a circle. Fill rather than fit, or a
                            // portrait is letterboxed into a disc with the face in a band across it.
                            .scaledToFill()
                    } placeholder: {
                        // Deliberately nothing: the glyph underneath is the placeholder.
                        Color.clear
                    }
                    .frame(width: MageRideControl.avatarSmall, height: MageRideControl.avatarSmall)
                    .clipShape(Circle())
                }
            }
            .frame(width: MageRideControl.avatarSmall, height: MageRideControl.avatarSmall)

            VStack(alignment: .leading, spacing: 1) {
                HStack(spacing: MageRideSpacing.xxs) {
                    Text(state.name ?? "profile_unnamed".localised)
                        .mageFont(.title)
                        .foregroundStyle(MageRideColor.onSurface)
                        .lineLimit(1)

                    // Only once the level read has answered. `L1` is a real level and also what a
                    // defaulted integer would print, so a placeholder here would tell a Level 3
                    // driver they were Level 1 for as long as the request was in flight.
                    if let level = state.level {
                        // `SolidBadge` and `DashboardLabels.level`, not a second copy of either:
                        // "L3" is a Driver Level identifier and not a sentence, and the dashboard
                        // already owns the one definition of how it is spelled and drawn.
                        SolidBadge(label: DashboardLabels.level(Int(level)), accent: MageRideColor.primary)
                    }
                }

                Text(secondLine)
                    .mageFont(.label)
                    .foregroundStyle(MageRideColor.onSurfaceVariant)
                    .lineLimit(1)
            }

            Spacer(minLength: MageRideSpacing.xxs)

            trailing()
        }
    }

    /// The plate and the rating, joined by the separator the rest of this app uses.
    ///
    /// A driver with no live vehicle gets copy saying so rather than an empty line: "no vehicle yet"
    /// is the state that sends them to SCR-DI-026a, and a blank row would read as a value that is
    /// still loading.
    private var secondLine: String {
        let vehicle = state.registration ?? "driver_header_no_vehicle".localised

        guard let rating = state.rating else { return vehicle }

        let stars = String(format: "%.1f", rating)
        return "\(vehicle)\(MageRideSymbols.separator)\(MageRideSymbols.starFilled) \(stars)"
    }
}

extension DriverHeader where Trailing == EmptyView {

    /// The header with no affordance of its own — the Menu tab's.
    init(state: DriverHeaderState) {
        self.init(state: state) { EmptyView() }
    }
}
