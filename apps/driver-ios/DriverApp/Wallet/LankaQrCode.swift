import CoreImage
import CoreImage.CIFilterBuiltins
import SwiftUI

/// AL-15's **fallback**: the LankaQR payload rendered as a scannable code.
///
/// The primary path is the *"Pay"* link into the driver's own bank app, and this is what SCR-DI-022
/// shows when that link could not be opened (``PaymentHandoff/openBankApp(_:)`` answers `false`) or
/// when the server sent a payload and no link at all. `PaymentMethods.lankaQrAction` in `:shared` is
/// where that choice is made; this view only draws what it decided.
///
/// **Δ iOS — the encoder is Core Image's and there is no dependency.** The Android twin added
/// `com.google.zxing:core` for this one job (and C074 later used its reader half for SCR-DA-027).
/// `CIQRCodeGenerator` is a first-party filter that has shipped since iOS 7, so the same fallback
/// costs this target nothing — which also means the AL-34 fence is easier to hold here than there:
/// **no code in this app can read a QR at all**, because nothing links a decoder.
///
/// **Always black on white, in both themes.** A QR code is read by a camera's contrast, not by a
/// design system — inverting it for dark mode produces a code many scanners refuse — so the modules
/// and the quiet zone are fixed and the card around them is what the theme colours. `CIQRCodeGenerator`
/// already emits black-on-white, so nothing here tints it either.
struct LankaQrCode: View {

    let payload: String

    var body: some View {
        Group {
            if let image = LankaQrCode.render(payload) {
                Image(uiImage: image)
                    // Nearest-neighbour, not the default smoothing: a QR symbol is a grid of hard
                    // squares and a bilinear upscale of a 30-module image is a grey blur no scanner
                    // reads. This is the whole reason the filter's tiny output is drawn rather than
                    // rasterised at display size.
                    .interpolation(.none)
                    .resizable()
                    .scaledToFit()
                    .padding(MageRideSpacing.sm)
                    .background(.white, in: RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous))
                    .accessibilityLabel(Text(key: "wallet_lankaqr_title"))
            } else {
                // A payload the encoder refused. The screen keeps its copy and its Close button rather
                // than an empty square: an EMVCo payload arrives from a gateway, and a malformed one
                // must leave the driver on a screen that still offers OnePay.
                Text(key: "wallet_lankaqr_unavailable")
                    .mageFont(.bodySmall)
                    .foregroundStyle(MageRideColor.error)
                    .multilineTextAlignment(.center)
                    .frame(maxWidth: .infinity)
            }
        }
        .frame(maxWidth: .infinity)
        .aspectRatio(1, contentMode: .fit)
    }

    /// The payload as a symbol, or `nil` when it cannot be encoded.
    ///
    /// `nil` rather than a fatal error, for the reason above. The filter answers `nil` for a payload
    /// too long to fit the format, and `CIContext.createCGImage` for an extent it cannot render.
    ///
    /// The correction level is **M**, EMVCo's own recommendation for a payment code: `L` is fragile on
    /// a screen held at an angle and `H` inflates a long payload into modules too fine to scan. The
    /// payload is EMVCo TLV — ASCII by construction — so it is encoded as ISO-8859-1, which stops the
    /// generator adding an ECI header no scanner needs.
    static func render(_ payload: String) -> UIImage? {
        guard let data = payload.data(using: .isoLatin1) else { return nil }

        let filter = CIFilter.qrCodeGenerator()
        filter.message = data
        filter.correctionLevel = "M"

        guard let output = filter.outputImage,
              let cgImage = context.createCGImage(output, from: output.extent)
        else { return nil }

        return UIImage(cgImage: cgImage)
    }

    /// One context for the app.
    ///
    /// A `CIContext` allocates a rendering pipeline, and building one per draw is the classic Core
    /// Image cost. The fallback dialog can be re-rendered on every Dynamic Type change.
    private static let context = CIContext()
}
