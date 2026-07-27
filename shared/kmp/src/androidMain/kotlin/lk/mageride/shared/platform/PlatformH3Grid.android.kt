package lk.mageride.shared.platform

import com.uber.h3core.H3Core
import lk.mageride.shared.data.models.GeoPoint
import lk.mageride.shared.domain.geo.H3Cell
import lk.mageride.shared.domain.geo.H3Grid
import lk.mageride.shared.domain.geo.H3GridUnavailableException
import java.io.IOException

/** Android/JVM: the reference H3 implementation through `com.uber:h3`. */
public actual fun platformH3Grid(): H3Grid? = H3JavaGrid

/**
 * [H3Grid] over `com.uber:h3`.
 *
 * `H3Core` is thread-safe and holds a JNI handle, so exactly one is created and it is created
 * **lazily** — `newInstance()` extracts a `libh3-java.so` for the running ABI out of the jar and
 * `System.load`s it, which is not work to do on a cold start for an app that may never open a map.
 *
 * The jar carries `android-arm` and `android-arm64` natives alongside the desktop ones, so the
 * same code serves the app and this module's JVM tests. If the extraction fails the failure is
 * reported as [H3GridUnavailableException] rather than an `IOException` from a call that looks
 * like pure geometry.
 */
internal object H3JavaGrid : H3Grid {

    private val h3: H3Core by lazy {
        try {
            H3Core.newInstance()
        } catch (e: IOException) {
            throw H3GridUnavailableException(NATIVE_LOAD_FAILED, e)
        } catch (e: UnsatisfiedLinkError) {
            throw H3GridUnavailableException(NATIVE_LOAD_FAILED, e)
        }
    }

    override fun cellAt(point: GeoPoint, resolution: Int): H3Cell =
        H3Cell(h3.latLngToCell(point.lat, point.lng, resolution))

    override fun gridDisk(origin: H3Cell, k: Int): Set<H3Cell> =
        h3.gridDisk(origin.index, k).mapTo(LinkedHashSet()) { H3Cell(it) }

    override fun center(cell: H3Cell): GeoPoint = h3.cellToLatLng(cell.index).let { GeoPoint(it.lat, it.lng) }

    override fun parent(cell: H3Cell, resolution: Int): H3Cell = H3Cell(h3.cellToParent(cell.index, resolution))

    private const val NATIVE_LOAD_FAILED = "com.uber:h3 could not load its native library for this ABI"
}
