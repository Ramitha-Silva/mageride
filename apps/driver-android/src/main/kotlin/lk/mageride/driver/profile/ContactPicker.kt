package lk.mageride.driver.profile

import android.content.ContentResolver
import android.content.Intent
import android.net.Uri
import android.provider.ContactsContract
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.ui.platform.LocalContext

/**
 * SCR-DA-029's contact picker for the AL-13 emergency contact.
 *
 * **No `READ_CONTACTS`, deliberately.** `ACTION_PICK` over
 * `ContactsContract.CommonDataKinds.Phone.CONTENT_URI` hands back the URI of the **one row the
 * driver chose**, carrying a one-shot read grant for it — so the app reads that number and cannot
 * read the address book. Requesting the permission would work too, and would ask a driver for
 * their whole contact list in order to store one number; the Play policy on it exists for a reason
 * and this app has none.
 *
 * Contrast `ActivityResultContracts.PickContact`, which answers a *contact* URI rather than a phone
 * row — resolving a number out of one needs the permission, which is exactly what this avoids.
 *
 * @param onPicked The chosen contact's display name and its number **exactly as the address book
 *   stores it** — `PhoneNumber.normalise` is what turns `0771234567`, `+94 77 123 4567` and
 *   `077-1234567` into the same nine digits. Not called at all when the driver backed out or the
 *   chosen row held no number: a picker that cannot answer leaves the two fields to be typed.
 * @return The lambda the ✎ row calls to open the address book.
 */
@Composable
internal fun rememberContactPicker(onPicked: (name: String, phone: String) -> Unit): () -> Unit {
    val context = LocalContext.current
    val deliver by rememberUpdatedState(onPicked)
    val request = remember {
        Intent(Intent.ACTION_PICK, ContactsContract.CommonDataKinds.Phone.CONTENT_URI)
    }

    val launcher = rememberLauncherForActivityResult(ActivityResultContracts.StartActivityForResult()) { result ->
        result.data?.data?.let { uri ->
            context.contentResolver.readContact(uri)?.let { (name, phone) -> deliver(name, phone) }
        }
    }

    return { launcher.launch(request) }
}

/**
 * The name and number behind [uri], or `null` when the row answers neither.
 *
 * Two projected columns and one row: the URI names a single `Phone` data row, so there is nothing
 * to filter and nothing to sort. Failure is `null` rather than a throw — a picker is a convenience
 * over two fields that are already on screen.
 */
private fun ContentResolver.readContact(uri: Uri): Pair<String, String>? {
    val projection = arrayOf(
        ContactsContract.CommonDataKinds.Phone.DISPLAY_NAME,
        ContactsContract.CommonDataKinds.Phone.NUMBER,
    )

    return query(uri, projection, null, null, null)?.use { cursor ->
        if (!cursor.moveToFirst()) return@use null
        val name = cursor.getString(0).orEmpty()
        val phone = cursor.getString(1).orEmpty()
        if (phone.isBlank()) null else name to phone
    }
}
