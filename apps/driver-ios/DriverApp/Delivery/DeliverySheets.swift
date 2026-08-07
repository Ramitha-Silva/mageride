import MageRideShared
import SwiftUI

/// SCR-DI-016a/b/c's three bodies, hosted together because at most one is ever up.
///
/// **They are inline ``DashboardSheet``s, not `.presentationDetents` sheets** — the same decision
/// SCR-DI-010 and SCR-DI-015 already made and for the same reason: the map behind a delivery sheet is
/// context, not a screen the driver can get back to by swiping this away, and a presented sheet would
/// take the tab bar with it. The wireframe's own class is `.sheet` / `.overlay.sheet`, which is that
/// shape; a driver who swiped a delivery away would have a parcel in the boot and nothing on screen
/// about it. ``RideSheets`` is the other kind — three modals over a screen that stays.
@MainActor
struct DeliverySheets: View {

    @ObservedObject var model: DeliveryModel

    /// Opens SCR-DI-005. Threaded down rather than raised as a flag on the state, exactly as C087's two
    /// capture screens do it: the route carries no arguments, so `open(target)` and the navigation are
    /// one gesture and belong at the same call site.
    let onCaptureRequested: () -> Void

    var body: some View {
        switch model.state.sheet {
        case .review: DeliveryReviewSheet(model: model)
        case .pickup: DeliveryPickupSheet(model: model)
        case .complete: DeliveryCompleteSheet(model: model, onCaptureRequested: onCaptureRequested)
        }
    }
}

// MARK: - Sheet 1 of 3

/// **SCR-DI-016a · review & start**.
///
/// What a driver decides on before they set off: how far the two legs are, how the parcel is being paid
/// for, and who is at each end with a button to ring them. **Cancel is not a cancellation of the
/// delivery** — it releases the job (see ``DeliveryModel/cancel()``) — which is why it is an outlined
/// error button beside the CTA rather than a destructive confirm.
@MainActor
struct DeliveryReviewSheet: View {

    @ObservedObject var model: DeliveryModel

    var body: some View {
        DashboardSheet {
            HStack(spacing: MageRideSpacing.xs) {
                Text(key: "delivery_title")
                    .mageFont(.title)
                    .foregroundStyle(MageRideColor.onSurface)
                    .frame(maxWidth: .infinity, alignment: .leading)

                if let size = model.state.ride?.packageSize {
                    SolidBadge(label: PackageLabels.size(size).localised, accent: MageRideVehicleColor.truck)
                }
            }

            HStack(spacing: MageRideSpacing.xs) {
                MetricCard(labelKey: "delivery_leg_pickup", value: distance(model.state.pickupMetres))
                MetricCard(labelKey: "delivery_leg_drop", value: distance(model.state.dropMetres))
            }

            // The wireframe's `.kv` row — the label on the left, the value hard against the right.
            HStack(spacing: MageRideSpacing.xs) {
                Text(key: "delivery_payment")
                    .mageFont(.bodySmall)
                    .foregroundStyle(MageRideColor.onSurfaceVariant)
                Spacer(minLength: MageRideSpacing.xs)
                Text(key: PackageLabels.payment(model.state.ride?.paymentMethod))
                    .mageFont(.bodySmall)
                    .foregroundStyle(MageRideColor.onSurface)
            }
            .accessibilityElement(children: .combine)

            DeliveryPartyList(model: model)

            HStack(spacing: MageRideSpacing.xs) {
                Button { Task { await model.cancel() } } label: {
                    Text(key: "delivery_cancel")
                        .mageFont(.subtitle)
                        .foregroundStyle(MageRideColor.error)
                        .frame(maxWidth: .infinity, minHeight: MageRideControl.ctaHeight)
                        .overlay {
                            RoundedRectangle(cornerRadius: MageRideControl.ctaRadius, style: .continuous)
                                .strokeBorder(MageRideColor.error, lineWidth: 1)
                        }
                        .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .disabled(model.state.isBusy)
                .opacity(model.state.isBusy ? 0.4 : 1)

                Button { Task { await model.advance() } } label: {
                    Text(key: "delivery_start")
                }
                .buttonStyle(.mageCtaStatus(MageRideColor.success, loading: model.state.isBusy))
                .disabled(!model.state.canAdvance)
            }
        }
    }

    /// The distance is absent rather than zero before the first GNSS fix: `0.0 km` on a courier's review
    /// sheet reads as "you are already there".
    private func distance(_ metres: Double?) -> String {
        metres.map { MoneyFormat.distance(metres: $0) } ?? MoneyFormat.empty
    }
}

// MARK: - Sheet 2 of 3

/// **SCR-DI-016b · pickup & OTP**.
///
/// The sender's four digits are what release the parcel (P-07): a correct one fires
/// `package.picked_up`, which is also the event that sends the *recipient* their own code — by APNs if
/// they have an account and by SMS with a tracking link if they do not (AL-21, US-20.5).
@MainActor
struct DeliveryPickupSheet: View {

    @ObservedObject var model: DeliveryModel

    var body: some View {
        DashboardSheet {
            HStack(spacing: MageRideSpacing.xs) {
                Image(systemName: "shippingbox.fill")
                    .font(.system(size: MageRideControl.avatarSmall))
                    .foregroundStyle(MageRideColor.onSurfaceVariant)

                VStack(alignment: .leading, spacing: 1) {
                    Text(model.state.label(of: .sender))
                        .mageFont(.title)
                        .foregroundStyle(MageRideColor.onSurface)
                    Text(pickupLine)
                        .mageFont(.bodySmall)
                        .foregroundStyle(MageRideColor.onSurfaceVariant)
                }
                Spacer(minLength: 0)
            }
            .accessibilityElement(children: .combine)

            HStack(spacing: MageRideSpacing.xs) {
                DeliveryOutlinedAction(
                    labelKey: "ride_call_sender",
                    symbolName: "phone.fill",
                    tint: MageRideColor.onSurface,
                    isEnabled: model.state.phone(of: .sender) != nil
                ) {
                    Task { await model.call(.sender) }
                }

                DeliveryOutlinedAction(
                    labelKey: "ride_sos",
                    symbolName: "shield.lefthalf.filled",
                    tint: MageRideColor.error,
                    // SCR-DI-032 needs a fix to attach to the alarm — `POST /v1/sos` has no
                    // positionless form.
                    isEnabled: model.state.position != nil,
                    action: model.openSos
                )
            }

            DeliveryOtpBlock(model: model, labelKey: "delivery_pickup_otp_label")

            Button { Task { await model.advance() } } label: {
                Text(key: "delivery_verify_pickup")
            }
            .buttonStyle(.mageCta(loading: model.state.isBusy))
            .disabled(!model.state.canAdvance)
        }
    }

    private var pickupLine: String {
        guard let address = model.state.ride?.pickup.address, !address.isEmpty else { return "" }
        return "delivery_pickup_at".localisedFormat(address)
    }
}

// MARK: - Sheet 3 of 3

/// **SCR-DI-016c · complete**.
///
/// *"Delivery completed"* **replaces the old "Cash received (COD)"** (AL-33): the parcel changing hands
/// and the cash being counted are two different events, and coupling them meant a driver who had
/// delivered could not say so until the money was settled. Uncollected COD is reconciled separately and
/// becomes `Disputed` after 24 hours (P-14).
///
/// The photograph is P-10's fallback for a recipient who is not there, and it **completes the delivery
/// on its own** — the receipt then reports `photo_proof` rather than `otp_verified`.
@MainActor
struct DeliveryCompleteSheet: View {

    @ObservedObject var model: DeliveryModel
    let onCaptureRequested: () -> Void

    var body: some View {
        DashboardSheet {
            Text(key: "delivery_complete_title")
                .mageFont(.title)
                .foregroundStyle(MageRideColor.onSurface)
                .frame(maxWidth: .infinity, alignment: .leading)

            DeliveryOtpBlock(model: model, labelKey: "delivery_delivery_otp_label")

            DeliveryPartyList(model: model)

            HStack(spacing: MageRideSpacing.xs) {
                DeliveryOutlinedAction(
                    labelKey: model.state.proof == nil ? "delivery_photo_proof" : "delivery_photo_retake",
                    symbolName: "camera.fill",
                    tint: MageRideColor.onSurface,
                    isEnabled: !model.state.isBusy,
                    height: MageRideControl.ctaHeight
                ) {
                    model.requestProofCapture()
                    onCaptureRequested()
                }

                Button { Task { await model.advance() } } label: {
                    Text(key: "delivery_completed")
                }
                .buttonStyle(.mageCtaStatus(MageRideColor.success, loading: model.state.isBusy))
                .disabled(!model.state.canAdvance)
            }
        }
    }
}

// MARK: - The pieces two sheets share

/// The wireframe's `.glist` of both ends of the delivery, each row with **its own call button** (AL-33).
///
/// Sheets 1 and 3 draw the same pair, which is the point: a courier at the recipient's door still has to
/// be able to ring the sender.
@MainActor
private struct DeliveryPartyList: View {

    @ObservedObject var model: DeliveryModel

    var body: some View {
        GroupedList {
            DeliveryPartyRow(model: model, party: .sender, showsSeparator: true)
            DeliveryPartyRow(model: model, party: .recipient, showsSeparator: false)
        }
    }
}

/// One `.gr` row: the wireframe's coloured `ic` square, the party's name over their number, and the
/// green `📞 Call` link.
///
/// **Not ``GroupedRow``.** That row takes localisation *keys* for both of its lines, and both of these
/// are data off the wire — a role plus a name (`Recipient · Sunethra`) and a phone number. It also puts
/// a control on the right rather than a status.
///
/// **Δ iOS — the call button is the wireframe's `textlink`, not Android's boxed icon button.**
/// `driver_ios.html` draws `📞 Call` as a green text link on both sheets where `driver_android.html`
/// draws a 46×38 outlined `📞`; that is the HIG's grouped-list idiom and it is the team-approved
/// baseline for this platform. The tap target is still the 44pt floor.
///
/// The link is disabled rather than hidden when the ride carries no number: a missing row would make the
/// sheet look like a delivery with one party, and there are always two.
@MainActor
private struct DeliveryPartyRow: View {

    @ObservedObject var model: DeliveryModel
    let party: DeliveryParty
    let showsSeparator: Bool

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: MageRideSpacing.sm) {
                Image(systemName: party == .sender ? "tray.and.arrow.up.fill" : "tray.and.arrow.down.fill")
                    .font(.footnote)
                    .foregroundStyle(MageRideColor.onPrimary)
                    .frame(width: MageRideControl.listRowIcon, height: MageRideControl.listRowIcon)
                    .background(accent, in: RoundedRectangle(cornerRadius: MageRideRadius.sm, style: .continuous))

                VStack(alignment: .leading, spacing: 1) {
                    Text(model.state.label(of: party))
                        .mageFont(.body)
                        .foregroundStyle(MageRideColor.onSurface)
                    Text(phone ?? MoneyFormat.empty)
                        .mageFont(.caption)
                        .foregroundStyle(MageRideColor.onSurfaceVariant)
                }
                .accessibilityElement(children: .combine)

                Spacer(minLength: MageRideSpacing.xs)

                Button { Task { await model.call(party) } } label: {
                    HStack(spacing: MageRideSpacing.xxs) {
                        Image(systemName: "phone.fill")
                            .font(.caption)
                        Text(key: "delivery_call")
                            .mageFont(.bodySmall)
                    }
                    .foregroundStyle(MageRideColor.success)
                    .frame(minHeight: MageRideControl.minimumTapTarget)
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .disabled(phone == nil)
                .opacity(phone == nil ? 0.4 : 1)
            }
            .padding(.horizontal, MageRideSpacing.sm)
            .frame(minHeight: MageRideControl.minimumTapTarget)

            if showsSeparator {
                Rectangle()
                    .fill(MageRideColor.surfaceVariant)
                    .frame(height: MageRideControl.hairline)
                    .padding(.leading, MageRideSpacing.sm)
            }
        }
    }

    private var phone: String? { model.state.phone(of: party) }

    /// The wireframe's own two fills — `secondary` for the sender's 📤, `vehVan` for the recipient's 📥.
    private var accent: Color {
        party == .sender ? MageRideColor.secondary : MageRideVehicleColor.van
    }
}

/// The four boxes, and what the attempt budget has to say about them (P-07).
///
/// Three states, and they are genuinely different messages: the gate is open and quiet, the gate has
/// eaten a wrong code and says how many are left, or the five are gone and the handoff is with the admin
/// queue — at which point the entry is disabled, because a sixth box would be a lie.
@MainActor
private struct DeliveryOtpBlock: View {

    @ObservedObject var model: DeliveryModel
    let labelKey: String

    var body: some View {
        VStack(alignment: .leading, spacing: MageRideSpacing.xxs) {
            Text(key: labelKey)
                .mageFont(.caption)
                .foregroundStyle(MageRideColor.onSurfaceVariant)

            OtpField(
                value: Binding(get: { model.state.otp }, set: { model.onOtpChange($0) }),
                length: Int(PackageHandoff.companion.OTP_LENGTH),
                isEnabled: !model.state.isLocked && !model.state.isBusy,
                isError: model.state.attemptsUsed > 0
            )

            // The fifth wrong code is the one that raises the queue item, so the message is the
            // handoff's status and not a warning about the next attempt: ops resolve it from there and
            // the driver is not stuck with the parcel.
            if model.state.isLocked {
                Text(key: "delivery_otp_locked")
                    .mageFont(.bodySmall)
                    .foregroundStyle(MageRideColor.error)
            } else if model.state.attemptsUsed > 0 {
                Text("delivery_otp_attempts_left".localisedFormat(model.state.attemptsRemaining))
                    .mageFont(.bodySmall)
                    .foregroundStyle(MageRideColor.warning)
            }
        }
    }
}

/// The wireframe's `btn-out` — an outlined action that shares a row with another.
///
/// ``ActiveRideScreen`` draws the same shape privately for its Call / SOS pair; this one is the delivery
/// cluster's and takes a height, because sheet 3 sits one beside a full CTA and the two have to line up.
@MainActor
private struct DeliveryOutlinedAction: View {

    let labelKey: String
    let symbolName: String
    let tint: Color
    let isEnabled: Bool
    var height: CGFloat = MageRideControl.minimumTapTarget
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            HStack(spacing: MageRideSpacing.xxs) {
                Image(systemName: symbolName)
                    .font(.footnote)
                Text(key: labelKey)
                    .mageFont(.bodySmall)
            }
            .foregroundStyle(tint)
            .frame(maxWidth: .infinity, minHeight: height)
            .overlay {
                RoundedRectangle(cornerRadius: MageRideRadius.sm, style: .continuous)
                    .strokeBorder(MageRideColor.outline, lineWidth: 1)
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .disabled(!isEnabled)
        .opacity(isEnabled ? 1 : 0.4)
    }
}
