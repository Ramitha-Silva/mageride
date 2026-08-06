package lk.mageride.passenger.ui.component

import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.LocalTextStyle
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.OutlinedTextFieldDefaults
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextAlign
import lk.mageride.passenger.ui.theme.ControlTokens
import lk.mageride.passenger.ui.theme.MageRideTheme

/**
 * The wireframe's `field.lbl` — a caption above an outlined input.
 *
 * SCR-PA-004's *"Full name"* is this. The label is drawn above the box rather than floating inside
 * it, which is what the wireframe shows and what keeps a Sinhala label from being clipped by M3's
 * floating-label animation.
 */
@Composable
internal fun LabelledTextField(
    label: String,
    value: String,
    onValueChange: (String) -> Unit,
    modifier: Modifier = Modifier,
    placeholder: String? = null,
    enabled: Boolean = true,
    isError: Boolean = false,
    keyboardType: KeyboardType = KeyboardType.Text,
    imeAction: ImeAction = ImeAction.Next,
) {
    Column(
        modifier = modifier.fillMaxWidth(),
        verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xxs),
    ) {
        SectionLabel(label)
        OutlinedTextField(
            value = value,
            onValueChange = onValueChange,
            modifier = Modifier.fillMaxWidth(),
            enabled = enabled,
            isError = isError,
            singleLine = true,
            placeholder = placeholder?.let { { Text(it, style = MaterialTheme.typography.bodyLarge) } },
            shape = RoundedCornerShape(MageRideTheme.radius.md),
            keyboardOptions = KeyboardOptions(keyboardType = keyboardType, imeAction = imeAction),
            colors = OutlinedTextFieldDefaults.colors(
                focusedBorderColor = MaterialTheme.colorScheme.primary,
                unfocusedBorderColor = MaterialTheme.colorScheme.outline,
            ),
        )
    }
}

/**
 * SCR-PA-003's `+94` phone entry — the wireframe's `field` with a `pre` prefix.
 *
 * **`+94` is a prefix, not a country picker.** Sri Lanka is the only country MageRide operates in
 * and every other dialling code is rejected downstream, so what is typed is the national number.
 * See `PhoneNumber` for why both `0771234567` and `771234567` have to work.
 *
 * The prefix and the `7X XXX XXXX` mask are Kotlin constants rather than string resources: they
 * are the same characters in all three languages, and three identical values is exactly what
 * `StringResourceTest` reads as a translation nobody did.
 */
@Composable
internal fun PhoneNumberField(
    value: String,
    onValueChange: (String) -> Unit,
    countryCode: String,
    placeholder: String,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
    isError: Boolean = false,
) {
    OutlinedTextField(
        value = value,
        onValueChange = onValueChange,
        modifier = modifier.fillMaxWidth(),
        enabled = enabled,
        isError = isError,
        singleLine = true,
        prefix = {
            Text(
                text = countryCode,
                style = MaterialTheme.typography.titleMedium,
                color = MaterialTheme.colorScheme.onSurface,
            )
        },
        placeholder = {
            Text(
                text = placeholder,
                style = MaterialTheme.typography.bodyLarge,
                color = MaterialTheme.colorScheme.outlineVariant,
            )
        },
        shape = RoundedCornerShape(MageRideTheme.radius.md),
        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Phone, imeAction = ImeAction.Done),
        colors = OutlinedTextFieldDefaults.colors(
            focusedBorderColor = MaterialTheme.colorScheme.primary,
            unfocusedBorderColor = MaterialTheme.colorScheme.outline,
        ),
    )
}

/**
 * SCR-PA-003's six OTP cells.
 *
 * **One text field behind six boxes, not six fields.** A per-cell field has to move focus on every
 * keystroke, and the cases that then break are the ones that matter most: a paste of the whole
 * code, a backspace at the start of a cell, and the keyboard's own one-time-code autofill, which
 * inserts six characters into one field at once. So the field is invisible and full-width, the
 * boxes are decoration over it, and the caret is wherever the string ends.
 *
 * **SMS Retriever is NOT wired**, and that is a signing dependency rather than a decision: the
 * Retriever API matches an SMS against a hash of the app's *signing certificate*, and this repo has
 * no release signing config (C103 owns it), so the hash cannot be computed for any build produced
 * today. `KeyboardType.NumberPassword` still gets the OS's own one-time-code suggestion on the
 * keyboard strip. Recorded in the C077 handoff.
 *
 * @param length Six (D5' §14.1).
 */
@Composable
internal fun OtpEntry(
    value: String,
    onValueChange: (String) -> Unit,
    length: Int,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
    isError: Boolean = false,
) {
    BasicTextField(
        value = value,
        onValueChange = { input -> onValueChange(input.filter(Char::isDigit).take(length)) },
        modifier = modifier.fillMaxWidth(),
        enabled = enabled,
        keyboardOptions = KeyboardOptions(
            keyboardType = KeyboardType.NumberPassword,
            imeAction = ImeAction.Done,
        ),
        // The field itself draws nothing; `decorationBox` is the six boxes.
        textStyle = LocalTextStyle.current.copy(color = androidx.compose.ui.graphics.Color.Transparent),
        decorationBox = {
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(
                    MageRideTheme.spacing.xs,
                    Alignment.CenterHorizontally,
                ),
            ) {
                repeat(length) { index -> OtpCell(value.getOrNull(index), enabled, isError) }
            }
        },
    )
}

/** One box of [OtpEntry]. Filled cells take the primary border, exactly as the wireframe draws. */
@Composable
private fun OtpCell(digit: Char?, enabled: Boolean, isError: Boolean) {
    val border = when {
        isError -> MaterialTheme.colorScheme.error
        digit != null -> MaterialTheme.colorScheme.primary
        else -> MaterialTheme.colorScheme.outline
    }
    Box(
        modifier = Modifier
            .width(ControlTokens.OtpCellWidth)
            .height(ControlTokens.OtpCellHeight)
            .border(
                width = if (digit != null) ControlTokens.BorderSelected else ControlTokens.Border,
                color = border,
                shape = RoundedCornerShape(MageRideTheme.radius.sm),
            ),
        contentAlignment = Alignment.Center,
    ) {
        Text(
            text = digit?.toString().orEmpty(),
            style = TextStyle(
                fontSize = MaterialTheme.typography.titleLarge.fontSize,
                fontWeight = MaterialTheme.typography.titleLarge.fontWeight,
                textAlign = TextAlign.Center,
            ),
            color = if (enabled) MaterialTheme.colorScheme.onSurface else MaterialTheme.colorScheme.outlineVariant,
        )
    }
}
