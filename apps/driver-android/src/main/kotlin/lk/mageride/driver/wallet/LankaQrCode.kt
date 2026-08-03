package lk.mageride.driver.wallet

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import com.google.zxing.BarcodeFormat
import com.google.zxing.EncodeHintType
import com.google.zxing.WriterException
import com.google.zxing.common.BitMatrix
import com.google.zxing.qrcode.QRCodeWriter
import com.google.zxing.qrcode.decoder.ErrorCorrectionLevel
import lk.mageride.driver.ui.theme.MageRideTheme

/**
 * AL-15's **fallback**: the LankaQR payload rendered as a scannable code.
 *
 * The primary path is the *"Pay"* deep link into the driver's own bank app, and this is what
 * SCR-DA-022 shows when that link could not be opened ([PaymentHandoff] answers `false`) or when
 * the server sent a payload and no link at all. `PaymentMethods.lankaQrAction` in `:shared` is
 * where that choice is made; this composable only draws what it decided.
 *
 * **Always black on white, in both themes.** A QR code is read by a camera's contrast, not by a
 * design system — inverting it for dark mode produces a code that many scanners refuse, so the
 * quiet zone and the modules are fixed and the card around them is what the theme colours. This is
 * the one place in the module that names a colour outside `ui/theme`, and that is why.
 */
@Composable
internal fun LankaQrCode(payload: String, modifier: Modifier = Modifier) {
    val matrix = remember(payload) { lankaQrMatrix(payload) } ?: return

    Canvas(
        modifier = modifier
            .fillMaxWidth()
            .aspectRatio(1f)
            .clip(RoundedCornerShape(MageRideTheme.radius.md))
            .background(Color.White)
            .padding(MageRideTheme.spacing.sm),
    ) {
        val module = size.minDimension / matrix.width
        val moduleSize = Size(module, module)

        for (row in 0 until matrix.height) {
            for (column in 0 until matrix.width) {
                if (!matrix.get(column, row)) continue
                drawRect(
                    color = Color.Black,
                    topLeft = Offset(x = column * module, y = row * module),
                    size = moduleSize,
                )
            }
        }
    }
}

/**
 * The payload as a module grid, or `null` when it cannot be encoded.
 *
 * `null` rather than a throw: an EMVCo payload arrives from a gateway, and a malformed one must
 * leave the driver on a screen that still offers OnePay rather than take the app down. The margin
 * is zero because the composable draws its own quiet zone as padding on a white field — ZXing's
 * default four-module margin would be drawn *inside* the same square and halve the code.
 */
internal fun lankaQrMatrix(payload: String): BitMatrix? = try {
    QRCodeWriter().encode(
        payload,
        BarcodeFormat.QR_CODE,
        MODULE_GRID,
        MODULE_GRID,
        mapOf(
            // `M` is EMVCo's own recommendation for a payment code: `L` is fragile on a screen
            // held at an angle and `H` inflates a long payload into modules too fine to scan.
            EncodeHintType.ERROR_CORRECTION to ErrorCorrectionLevel.M,
            EncodeHintType.MARGIN to 0,
            // The payload is EMVCo TLV — ASCII by construction — and naming the charset stops
            // ZXing guessing a multi-byte one and adding an ECI header no scanner needs.
            EncodeHintType.CHARACTER_SET to "ISO-8859-1",
        ),
    )
} catch (_: WriterException) {
    null
} catch (_: IllegalArgumentException) {
    null
}

/**
 * The requested output size — **one**, which asks ZXing for the bare module grid.
 *
 * `QRCodeWriter` scales its symbol into whatever size is asked for and **centres it**, so a request
 * for 512 px produces a 512-wide matrix whose modules are 11 px each and whose first eight columns
 * are padding. Two things would then be wrong: the canvas below draws one `drawRect` per matrix
 * cell, which at 512² is a quarter of a million rectangles a frame, and the grid it is measuring
 * would not start at the finder pattern. Asking for 1 makes `outputWidth = max(1, symbolWidth)`
 * and the multiple 1, so a cell is a module and the canvas does the scaling — which is what a
 * vector surface should be doing anyway. Not a `dp` for exactly that reason.
 */
private const val MODULE_GRID = 1
