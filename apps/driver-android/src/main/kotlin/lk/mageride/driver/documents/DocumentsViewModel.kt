package lk.mageride.driver.documents

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import lk.mageride.shared.db.driver.CachedDocumentImage

/**
 * SCR-DA-029a — the driver's own documents (Δ MCS-28).
 *
 * @property documents The driver's identity documents first, then each vehicle's, which is the
 *   order the query answers in and the order the screen groups by.
 * @property loading Only while the first read of the *cache* is in flight, which is milliseconds.
 *   The network refresh behind it does not raise this: a driver looking at their licence should not
 *   watch it be replaced by a spinner.
 * @property offline Whether the refresh failed. Not an error state — the point of this screen is
 *   that it works without a connection — so it is a note beside documents that are still shown.
 */
internal data class DocumentsState(
    val documents: List<CachedDocumentImage> = emptyList(),
    val loading: Boolean = true,
    val offline: Boolean = false,
)

/**
 * The documents screen's state (Δ MCS-28).
 *
 * **Cache first, always.** [refresh] goes to the network behind whatever is already drawn, and a
 * failure there leaves the documents on screen and raises [DocumentsState.offline] instead of
 * replacing them with an error. A screen whose whole purpose is a roadside with no signal must not
 * blank itself when it finds one.
 */
internal class DocumentsViewModel(private val store: DriverDocumentStore) : ViewModel() {

    private val mutableState = MutableStateFlow(DocumentsState())

    val state: StateFlow<DocumentsState> = mutableState.asStateFlow()

    init {
        paintFromCache()
        refresh()
    }

    /** What is on disk, drawn on the frame the screen opens. */
    private fun paintFromCache() {
        viewModelScope.launch {
            val cached = runCatching { store.cached() }.getOrNull().orEmpty()

            mutableState.update { it.copy(documents = cached, loading = false) }
        }
    }

    /** Re-lists, fetches anything new, and sweeps what has aged out (§0.4 condition 3). */
    @Suppress("TooGenericExceptionCaught")
    fun refresh() {
        viewModelScope.launch {
            try {
                val documents = store.refresh()

                mutableState.update { it.copy(documents = documents, loading = false, offline = false) }

                // After the refresh rather than before: the sweep and the fetch both touch the same
                // rows, and dropping an image a moment before re-downloading it is the one ordering
                // that costs a driver bytes for nothing.
                store.sweep()
            } catch (cause: CancellationException) {
                throw cause
            } catch (_: Throwable) {
                // Deliberately keeps the documents. See the class KDoc.
                mutableState.update { it.copy(loading = false, offline = true) }
            }
        }
    }
}
