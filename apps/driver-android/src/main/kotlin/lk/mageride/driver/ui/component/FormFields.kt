package lk.mageride.driver.ui.component

import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextAlign
import lk.mageride.driver.ui.theme.ControlTokens
import lk.mageride.driver.ui.theme.MageRideTheme

/**
 * The wireframe's `field lbl` — a labelled single-line entry.
 *
 * An `OutlinedTextField` rather than the wireframe's bare box because the box **is** an outlined
 * field: §0.2's `md 12` corner is the M3 medium shape the theme already provides, and M3 gives the
 * label, the error state and the IME wiring for free.
 *
 * @param supporting Helper or error copy under the field. Always a string resource.
 * @param isError Draws the field in `error`; pair it with [supporting].
 * @param prefix A fixed leader inside the field — `Rs` on every amount C073 asks for. **Data, not
 *   copy** (`MoneyFormat.PREFIX`), for the same reason `+94` is on [PhoneNumberField].
 */
// Nine parameters is an M3 slot API's shape, not a design smell — see `MageRideCta`'s own note.
@Suppress("LongParameterList")
@Composable
internal fun LabelledTextField(
    label: String,
    value: String,
    onValueChange: (String) -> Unit,
    modifier: Modifier = Modifier,
    placeholder: String? = null,
    supporting: String? = null,
    prefix: String? = null,
    isError: Boolean = false,
    singleLine: Boolean = true,
    keyboardOptions: KeyboardOptions = KeyboardOptions(imeAction = ImeAction.Next),
) {
    OutlinedTextField(
        value = value,
        onValueChange = onValueChange,
        modifier = modifier.fillMaxWidth(),
        label = { Text(text = label, style = MaterialTheme.typography.labelSmall) },
        placeholder = placeholder?.let { { Text(text = it) } },
        supportingText = supporting?.let { { Text(text = it, style = MaterialTheme.typography.labelSmall) } },
        prefix = prefix?.let { { Text(text = it, style = MaterialTheme.typography.titleMedium) } },
        isError = isError,
        singleLine = singleLine,
        shape = RoundedCornerShape(MageRideTheme.radius.md),
        keyboardOptions = keyboardOptions,
        textStyle = MaterialTheme.typography.titleMedium,
    )
}

/**
 * SCR-DA-003's `+94` field — a fixed country prefix and the nine national digits.
 *
 * The prefix is a **prefix, not a default**: Sri Lanka is the only country this platform operates
 * in (D5' §14.1 fixes the E.164 form at `+947XXXXXXXX`), and a country picker would offer a
 * choice that every downstream validator rejects. What the driver types is the national number
 * alone, so a paste of `0771234567` and one of `771234567` both work — see `PhoneNumber`.
 */
@Composable
internal fun PhoneNumberField(
    value: String,
    onValueChange: (String) -> Unit,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
    isError: Boolean = false,
    supporting: String? = null,
    placeholder: String = "",
    prefix: String = "",
) {
    OutlinedTextField(
        value = value,
        onValueChange = onValueChange,
        modifier = modifier.fillMaxWidth(),
        enabled = enabled,
        prefix = { Text(text = prefix, style = MaterialTheme.typography.titleMedium) },
        placeholder = { Text(text = placeholder) },
        supportingText = supporting?.let { { Text(text = it, style = MaterialTheme.typography.labelSmall) } },
        isError = isError,
        singleLine = true,
        shape = RoundedCornerShape(MageRideTheme.radius.md),
        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Phone, imeAction = ImeAction.Done),
        textStyle = MaterialTheme.typography.titleMedium,
    )
}

/**
 * SCR-DA-003's six OTP cells.
 *
 * One real text field behind six drawn boxes rather than six fields: six would need focus
 * management, would fight the SMS autofill that fills a whole code at once, and would make
 * "paste the code" impossible. The invisible field owns the value; the boxes render it.
 *
 * @param length Six — D5' §14.1's code length, passed rather than baked so a test can say so.
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
    Box(modifier = modifier.fillMaxWidth()) {
        OutlinedTextField(
            value = value,
            onValueChange = { entered -> onValueChange(entered.filter(Char::isDigit).take(length)) },
            modifier = Modifier.fillMaxWidth(),
            enabled = enabled,
            isError = isError,
            singleLine = true,
            shape = RoundedCornerShape(MageRideTheme.radius.md),
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.NumberPassword, imeAction = ImeAction.Done),
            textStyle = TextStyle(
                textAlign = TextAlign.Center,
                fontFamily = MaterialTheme.typography.headlineMedium.fontFamily,
                fontSize = MaterialTheme.typography.headlineMedium.fontSize,
                letterSpacing = MaterialTheme.typography.headlineMedium.letterSpacing,
            ),
        )
    }
}

/** The wireframe's `otp` row, drawn under the entry so the driver can count the digits. */
@Composable
internal fun OtpProgress(value: String, length: Int, modifier: Modifier = Modifier) {
    Row(
        modifier = modifier.fillMaxWidth(),
        horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs, Alignment.CenterHorizontally),
    ) {
        repeat(length) { index ->
            val filled = index < value.length
            Box(
                modifier = Modifier
                    .size(ControlTokens.OtpCell)
                    .border(
                        width = if (filled) ControlTokens.BorderSelected else ControlTokens.Border,
                        color = if (filled) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.outline,
                        shape = RoundedCornerShape(MageRideTheme.radius.sm),
                    ),
                contentAlignment = Alignment.Center,
            ) {
                Text(
                    text = value.getOrNull(index)?.toString().orEmpty(),
                    style = MaterialTheme.typography.titleLarge,
                    color = MaterialTheme.colorScheme.onSurface,
                )
            }
        }
    }
}
