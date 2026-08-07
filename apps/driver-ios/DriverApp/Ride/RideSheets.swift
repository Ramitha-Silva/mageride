import MageRideShared
import SwiftUI

/// SCR-DI-015's three sheets, hosted together because at most one is ever up.
///
/// The OTP entry (P-07), AL-48's call-type chooser and AL-47's QR attestation are three different
/// conversations with three different consequences, and putting them in one file keeps
/// ``ActiveRideScreen`` the wireframe's layout tree rather than the wireframe plus its modals.
///
/// All three are `.sheet` detents rather than `.alert`s — the wireframe draws bottom sheets and the OTP
/// entry needs a real keyboard-avoiding layout, which an `.alert` text field does not give.
@MainActor
struct RideSheets: View {

    let sheet: RideSheet
    @ObservedObject var model: ActiveRideModel

    var body: some View {
        Group {
            switch sheet {
            case .startOtp: startOtp
            case .callType: callType
            case .qrConfirm: qrConfirm
            }
        }
        .presentationDetents([.medium])
        // AL-47's sheet is the last step of the ride: a driver who swiped it away would have a
        // completed trip with no settled payment and no obvious way back to it. The CTA on the sheet
        // underneath is that way back, so the dismiss is disabled here rather than being a trap.
        .interactiveDismissDisabled(sheet == .qrConfirm)
    }

    /// The rider's four-digit start OTP (P-07).
    ///
    /// **Five attempts and no more**: after that the endpoint answers `423 otp-locked` and the handoff
    /// needs support intervention. The count is the server's — nothing here tracks it, because a client
    /// that counted would be a second answer to a question `rides` already has.
    private var startOtp: some View {
        SheetBody(titleKey: "ride_otp_title", bodyKey: "ride_otp_body") {
            OtpEntryFields(model: model)
        }
    }

    /// **AL-48's call-type chooser** — and there are two options, not three.
    ///
    /// *"Free call"* is the in-app WebRTC session, which SCR-DI-031 places over CallKit; *"Normal call"*
    /// is a **direct cellular dial** of the counterparty's real number, which the ride carries from
    /// `Accepted` onward. The masked-number bridge and D-25's masked-SMS relay were both withdrawn, so
    /// a VoIP failure falls back to the same direct dial rather than to a relay (US-26.4, BR-30.3).
    private var callType: some View {
        SheetBody(titleKey: "ride_call_title") {
            Button { Task { await model.call(type: CallType.freeVoip) } } label: {
                Text(key: "ride_call_free")
            }
            .buttonStyle(.mageCta)

            Button { Task { await model.call(type: CallType.directDial) } } label: {
                Text(key: "ride_call_normal")
            }
            .buttonStyle(.mageCtaTonal)
        }
    }

    /// **AL-47 · *"QR payment received?"*** (US-26.1).
    ///
    /// The driver's answer **is** the settlement. *"Yes"* posts `POST /v1/fare/pay/driver-qr/confirm`
    /// and the payment reaches `DriverConfirmedQR`, which is terminal, settles like cash and releases
    /// the earning (R-05). *"Not received"* opens a Support → Finance ticket and moves no money —
    /// MageRide took no commission on this path and holds none of it, so there is nothing to reverse.
    private var qrConfirm: some View {
        SheetBody(titleKey: "ride_qr_title", bodyKey: "ride_qr_body") {
            Button { Task { await model.confirmQrPayment(received: true) } } label: {
                Text(key: "ride_qr_received")
            }
            .buttonStyle(.mageCta)

            Button { Task { await model.confirmQrPayment(received: false) } } label: {
                Text(key: "ride_qr_not_received")
            }
            .buttonStyle(.mageCtaTonal)
        }
    }
}

/// The four digits the rider reads out, and the button that spends one attempt.
///
/// Its own view because the entry needs `@State` and a sheet's `body` is rebuilt on every change of the
/// model it observes — a `@State` declared beside them would be reset mid-typing.
@MainActor
private struct OtpEntryFields: View {

    @ObservedObject var model: ActiveRideModel
    @State private var otp = ""

    var body: some View {
        VStack(spacing: MageRideSpacing.sm) {
            OtpField(value: $otp, length: OtpEntryFields.length)

            Button { Task { await model.startRide(otp: otp) } } label: {
                Text(key: "ride_otp_verify")
            }
            .buttonStyle(.mageCta)
            .disabled(otp.count != OtpEntryFields.length)
        }
    }

    /// Four digits (P-07). The package handoffs use the same length on the same entry.
    private static let length = 4
}

/// The shape all three sheets share — a title, an optional line of explanation, and the actions.
private struct SheetBody<Content: View>: View {

    let titleKey: String
    var bodyKey: String?
    @ViewBuilder let content: Content

    var body: some View {
        VStack(alignment: .leading, spacing: MageRideSpacing.sm) {
            Text(key: titleKey)
                .mageFont(.title)
                .foregroundStyle(MageRideColor.onSurface)

            if let bodyKey {
                Text(key: bodyKey)
                    .mageFont(.bodySmall)
                    .foregroundStyle(MageRideColor.onSurfaceVariant)
            }

            content
        }
        .padding(MageRideSpacing.md)
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}
