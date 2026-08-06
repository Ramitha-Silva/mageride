package lk.mageride.passenger.settings

import androidx.annotation.StringRes
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import lk.mageride.passenger.location.PassengerLocationSource
import lk.mageride.passenger.location.asPoint
import lk.mageride.shared.data.api.IdempotencyKeyGenerator
import lk.mageride.shared.data.models.GeoPoint
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.iam.SavedAddress
import lk.mageride.shared.data.models.iam.SavedAddressInput

/**
 * Which of the two shortcuts an address is, if either (AL-26, US-22.1).
 *
 * **A flag pair on the wire and one value here.** `SavedAddress` carries `isHome` and `isWork`
 * independently, which admits an address that is somehow both; the server's partial unique indexes
 * allow at most one of each per user but nothing stops one row setting both. Collapsing them into
 * an enum is what makes *"Home, Work or neither"* the only shape a screen can express.
 */
internal enum class AddressShortcut {
    HOME,
    WORK,
    NONE,
    ;

    companion object {
        /** What [address] is. Home wins if a row somehow claims both — it is the row drawn first. */
        fun of(address: SavedAddress): AddressShortcut = when {
            address.isHome == true -> HOME
            address.isWork == true -> WORK
            else -> NONE
        }
    }
}

/**
 * SCR-PA-026a — the add-address ModalBottomSheet, as it is being filled in.
 *
 * The wireframe's four fields and nothing else (AL-26's fence): Address Line 1, Address Line 2,
 * Address Line 3 and a free-text Label. [shortcut] carries no control of its own — it is decided by
 * *which row was tapped* on the screen behind the sheet, so a passenger editing Home never has to
 * be told they are.
 *
 * @property addressId The row being replaced, or `null` when this is a new address.
 * @property point Where the pin was when the sheet opened. The sheet does not move it — the map is
 *   behind the scrim — so it is a value here rather than a stream.
 * @property locating Whether the reverse geocode is still in flight. The fields are editable
 *   throughout; the lookup only fills in what is still empty.
 */
internal data class AddressSheetState(
    val point: GeoPoint,
    val addressId: Ulid? = null,
    val shortcut: AddressShortcut = AddressShortcut.NONE,
    val label: String = "",
    val line1: String = "",
    val line2: String = "",
    val line3: String = "",
    val locating: Boolean = false,
    val saving: Boolean = false,
) {

    /**
     * *"Save address"* is live once the row can be identified and found.
     *
     * `label` and `line1` are the contract's two required fields (`SavedAddressInput.required`),
     * and trimming is what stops a row of spaces passing a `isNotEmpty` check on the way to a
     * `400`.
     */
    val canSave: Boolean get() = !saving && label.isNotBlank() && line1.isNotBlank()

    /** An existing row offers Delete; a new one has nothing to delete (US-22.3). */
    val editing: Boolean get() = addressId != null
}

/**
 * SCR-PA-026's state.
 *
 * @property pin Where the map's fixed centre marker is. `null` until the first fix or the first
 *   camera settle — the map opens on its default centre and the CTA waits for one.
 * @property sheet SCR-PA-026a, or `null` when it is closed.
 */
internal data class SavedAddressesState(
    val addresses: List<SavedAddress> = emptyList(),
    val pin: GeoPoint? = null,
    val loading: Boolean = true,
    val sheet: AddressSheetState? = null,
    val busyWith: Ulid? = null,
    @param:StringRes val error: Int? = null,
) {
    /** The ★ Home row. `null` renders the wireframe's row with *"Not set"* under it. */
    val home: SavedAddress? get() = addresses.firstOrNull { it.isHome == true }

    /** The ★ Work row. */
    val work: SavedAddress? get() = addresses.firstOrNull { it.isWork == true }

    /** Everything else — the 📍 rows, in the order the server sent them. */
    val labelled: List<SavedAddress>
        get() = addresses.filter { it.isHome != true && it.isWork != true }
}

/**
 * SCR-PA-026 and SCR-PA-026a — the passenger's address book.
 *
 * **Home and Work are rows, not a mode.** The wireframe draws them first and always, above the
 * labelled addresses, and the states line says *"Save Home & Work by pin on OSM map"* while drawing
 * no control for it: the ✎ on either row is that control. Tapping it opens the same sheet the
 * `＋ Add address` CTA does, with the shortcut already decided and the label pre-filled — which is
 * why nothing in SCR-PA-026a has to ask *"is this your Home?"*, and why AL-26's four fields are all
 * the sheet has.
 *
 * **The pin is the map's centre, not a marker the passenger drags.** MapLibre's draggable-annotation
 * API lives in a plugin artifact this module cannot depend on (see the module CLAUDE.md), so the
 * map moves under a fixed pin — the same centre-pin picker C079's `MapPickSheet` and SCR-PA-011
 * both use, and the reason `MageRideMap` has an `onCameraIdle` at all.
 *
 * **A reverse geocode is a pre-fill, never a gate** (AL-14). `GET /v1/geo/reverse` answers `404` in
 * the sea and `503` when Nominatim is down, and neither stops a passenger saving the place they
 * just pinned: [AddressBook.describe] answers `null`, the three lines open empty, and the keyboard
 * is already up.
 */
@Suppress("TooManyFunctions") // Two screens on one: a list with four actions and a four-field sheet.
internal class SavedAddressesViewModel(
    private val addresses: AddressBook,
    private val locations: PassengerLocationSource,
    private val keys: IdempotencyKeyGenerator,
) : ViewModel() {

    private val mutableState = MutableStateFlow(SavedAddressesState())

    val state: StateFlow<SavedAddressesState> = mutableState.asStateFlow()

    init {
        refresh()
        // One fix, not a subscription. The pin is where the passenger is *when the screen opens*;
        // following the handset afterwards would drag the map out from under a thumb that had just
        // positioned it.
        viewModelScope.launch { openOnFirstFix() }
    }

    fun refresh() {
        mutableState.update { it.copy(loading = true, error = null) }
        viewModelScope.launch { load() }
    }

    fun clearError() {
        mutableState.update { it.copy(error = null) }
    }

    /** The map settled somewhere. */
    fun onPinMoved(point: GeoPoint) {
        mutableState.update { it.copy(pin = point) }
    }

    /** `＋ Add address` — a new, unlabelled address at the pin. */
    fun addAddress() {
        openSheet(existing = null, shortcut = AddressShortcut.NONE, defaultLabel = "")
    }

    /**
     * The ✎ on the Home or Work row (US-22.1).
     *
     * An existing shortcut is **edited where it is** — its own coordinates, not the pin — because
     * the ✎ on a row that already has an address is a request to correct that address, and moving
     * it to wherever the map happens to be centred would be a different action with the same icon.
     * A shortcut with no address yet takes the pin, which is *"save Home & Work by selecting the
     * location on the map"*.
     */
    fun editShortcut(shortcut: AddressShortcut, defaultLabel: String) {
        val existing = when (shortcut) {
            AddressShortcut.HOME -> mutableState.value.home
            AddressShortcut.WORK -> mutableState.value.work
            AddressShortcut.NONE -> null
        }
        openSheet(existing = existing, shortcut = shortcut, defaultLabel = defaultLabel)
    }

    /** The ✎ on a labelled row. */
    fun edit(address: SavedAddress) {
        openSheet(existing = address, shortcut = AddressShortcut.of(address), defaultLabel = address.label)
    }

    fun dismissSheet() {
        mutableState.update { it.copy(sheet = null) }
    }

    fun onLabelChanged(value: String) = editSheet { it.copy(label = value) }

    fun onLine1Changed(value: String) = editSheet { it.copy(line1 = value) }

    fun onLine2Changed(value: String) = editSheet { it.copy(line2 = value) }

    fun onLine3Changed(value: String) = editSheet { it.copy(line3 = value) }

    /**
     * *"Save address"* — `POST` for a new row, `PUT` for one being edited (US-22.2/22.3).
     *
     * **All three lines and the label travel, empty ones as `null`.** The contract types line 2 and
     * line 3 as optional strings, and sending `""` would store a blank the list then has to render;
     * a line the passenger left empty is a line they do not have.
     */
    fun save() {
        val sheet = mutableState.value.sheet ?: return
        if (!sheet.canSave) return

        val input = SavedAddressInput(
            label = sheet.label.trim(),
            line1 = sheet.line1.trim(),
            line2 = sheet.line2.trim().takeIf(String::isNotEmpty),
            line3 = sheet.line3.trim().takeIf(String::isNotEmpty),
            lat = sheet.point.lat,
            lng = sheet.point.lng,
            isHome = sheet.shortcut == AddressShortcut.HOME,
            isWork = sheet.shortcut == AddressShortcut.WORK,
        )

        editSheet { it.copy(saving = true) }
        mutableState.update { it.copy(error = null) }

        viewModelScope.launch { commit(sheet.addressId, input) }
    }

    /**
     * Deletes the address being edited (US-22.3).
     *
     * From inside the sheet, because that is the only place the wireframe leaves for it: SCR-PA-026
     * draws a ✎ per row and no ✕, and its own states line says *"edit/delete supported"*. Reaching
     * delete through edit also means the passenger has just been shown which address it is.
     */
    fun delete() {
        val addressId = mutableState.value.sheet?.addressId ?: return

        mutableState.update { it.copy(sheet = null, busyWith = addressId, error = null) }
        viewModelScope.launch { remove(addressId) }
    }

    // ------------------------------------------------------------------------------------------

    private fun editSheet(change: (AddressSheetState) -> AddressSheetState) {
        mutableState.update { current -> current.copy(sheet = current.sheet?.let(change)) }
    }

    /**
     * Opens SCR-PA-026a and starts the lookup behind it.
     *
     * An existing row opens on its own stored lines and skips the geocoder entirely: it has been
     * named already, and overwriting a passenger's own wording with Nominatim's is the opposite of
     * what an edit is for.
     */
    private fun openSheet(existing: SavedAddress?, shortcut: AddressShortcut, defaultLabel: String) {
        val point = existing?.let { GeoPoint(lat = it.lat, lng = it.lng) }
            ?: mutableState.value.pin
            ?: return

        mutableState.update {
            it.copy(
                error = null,
                sheet = AddressSheetState(
                    point = point,
                    addressId = existing?.addressId,
                    shortcut = shortcut,
                    label = existing?.label ?: defaultLabel,
                    line1 = existing?.line1.orEmpty(),
                    line2 = existing?.line2.orEmpty(),
                    line3 = existing?.line3.orEmpty(),
                    locating = existing == null,
                ),
            )
        }

        if (existing == null) viewModelScope.launch { describe(point) }
    }

    /**
     * Fills the empty lines in from `GET /v1/geo/reverse`.
     *
     * `line1` and `city` are the two fields `GeocodedPlace` carries, and they are AL-26's first and
     * **third** lines — street then city — because line 2 is *"area/suburb"* and Nominatim's answer
     * has no such field. Guessing one by splitting `displayName` on commas would fill a form with
     * an answer nobody checked; the passenger is looking at the sheet and can type it.
     *
     * Nothing already typed is overwritten: the lookup is slower than the keyboard, and a field
     * that rewrote itself under a thumb would be worse than an empty one.
     */
    private suspend fun describe(point: GeoPoint) {
        val place = addresses.describe(point)
        editSheet { sheet ->
            sheet.copy(
                locating = false,
                line1 = sheet.line1.ifBlank { place?.line1.orEmpty() },
                line3 = sheet.line3.ifBlank { place?.city.orEmpty() },
            )
        }
    }

    @Suppress("TooGenericExceptionCaught")
    private suspend fun load() {
        try {
            val items = addresses.list()
            mutableState.update { it.copy(addresses = items, loading = false) }
        } catch (cause: CancellationException) {
            throw cause
        } catch (cause: Throwable) {
            mutableState.update { it.copy(loading = false, error = SettingsErrors.messageFor(cause)) }
        }
    }

    @Suppress("TooGenericExceptionCaught")
    private suspend fun commit(addressId: Ulid?, input: SavedAddressInput) {
        try {
            val saved = if (addressId == null) {
                addresses.add(input, keys.next())
            } else {
                addresses.replace(addressId, input)
            }
            mutableState.update { current ->
                current.copy(sheet = null, addresses = current.addresses.merge(saved))
            }
        } catch (cause: CancellationException) {
            throw cause
        } catch (cause: Throwable) {
            editSheet { it.copy(saving = false) }
            mutableState.update { it.copy(error = SettingsErrors.messageFor(cause)) }
        }
    }

    @Suppress("TooGenericExceptionCaught")
    private suspend fun remove(addressId: Ulid) {
        try {
            addresses.remove(addressId)
            mutableState.update { current ->
                current.copy(
                    busyWith = null,
                    addresses = current.addresses.filterNot { it.addressId == addressId },
                )
            }
        } catch (cause: CancellationException) {
            throw cause
        } catch (cause: Throwable) {
            mutableState.update { it.copy(busyWith = null, error = SettingsErrors.messageFor(cause)) }
        }
    }

    /**
     * Puts [saved] into the list, replacing whatever it replaced.
     *
     * **Also clears the shortcut off whichever row used to hold it**, because that is what the
     * server just did: *"moving the Home or Work flag to this address clears it from whichever
     * address held it"*. Re-reading the list would learn the same thing at the cost of a round
     * trip, and would leave two Home rows on screen until it came back.
     */
    private fun List<SavedAddress>.merge(saved: SavedAddress): List<SavedAddress> {
        val position = indexOfFirst { it.addressId == saved.addressId }
        val others = filterNot { it.addressId == saved.addressId }.map { address ->
            address.copy(
                isHome = if (saved.isHome == true) false else address.isHome,
                isWork = if (saved.isWork == true) false else address.isWork,
            )
        }
        // An edit keeps its position in a list the server ordered; a new address joins the end.
        return if (position < 0) others + saved else others.take(position) + saved + others.drop(position)
    }

    /** The first fix, as the map's opening centre. Nothing is drawn from it afterwards. */
    private suspend fun openOnFirstFix() {
        val fix = locations.fixes.first()
        mutableState.update { current -> current.copy(pin = current.pin ?: fix.asPoint()) }
    }
}
