package lk.mageride.shared.platform

import com.uber.h3core.H3Core
import lk.mageride.shared.data.models.GeoPoint
import lk.mageride.shared.domain.geo.H3Cell
import lk.mageride.shared.domain.geo.H3Grid
import lk.mageride.shared.domain.geo.H3GridUnavailableException
import java.io.IOException

/**
 * Android and the JVM: the reference H3 implementation through `com.uber:h3`.
 *
 * One actual for both targets, in the shared `jvmShared` source set, because the cell ids it
 * produces have to be identical on a handset and in the e2e harness — and identical to the ones
 * `MageRide.Shared.Geo` computes server-side, or a passenger joins `cell:{h3index}` groups that
 * nothing ever publishes to (C025 moved it here from androidMain).
 */
public actual fun platformH3Grid(): H3Grid? = H3JavaGrid

/**
 * [H3Grid] over `com.uber:h3`.
 *
 * `H3Core` is thread-safe and holds a JNI handle, so exactly one is created and it is created
 * **lazily** — loading a native library is not work to do on a cold start for an app that may
 * never open a map.
 *
 * **Two ways in, because the two targets package the native differently.**
 *
 *  - **Android** gets it from `lib/<abi>/libh3-java.so` in the APK, put there by the app module's
 *    `extractH3Natives` task, and reached with `System.loadLibrary` — `newSystemInstance()`. The
 *    jar's own `android-arm64/libh3-java.so` resource is NOT an option: AGP's java-resource merger
 *    drops every `*.so`, and the native-lib merger only recognises `lib/<abi>/`, so
 *    `newInstance()` inside an APK unpacks a resource that was never packaged. That was a process
 *    kill on the first `cellAt` after a passenger granted location, not a caught failure.
 *  - **The JVM** — this module's tests and the e2e harness — runs off the plain jar with no
 *    jniLibs anywhere, so the unpack-and-`System.load` path is the right one there.
 *
 * The fallback rather than a platform check keeps ONE actual serving both targets, which is the
 * whole point of `jvmShared` (C025). Either failure is reported as [H3GridUnavailableException]
 * rather than as an `UnsatisfiedLinkError` out of a call that looks like pure geometry — and h3
 * 4.4.0 ships arm64 and arm natives only, so an x86_64 emulator legitimately reaches it.
 */
internal object H3JavaGrid : H3Grid {

    private val h3: H3Core by lazy { load() }

    private fun load(): H3Core = try {
        H3Core.newSystemInstance()
    } catch (systemLoad: UnsatisfiedLinkError) {
        unpackFromJar(systemLoad)
    }

    private fun unpackFromJar(systemLoad: UnsatisfiedLinkError): H3Core = try {
        H3Core.newInstance()
    } catch (unpack: IOException) {
        unpack.addSuppressed(systemLoad)
        throw H3GridUnavailableException(NATIVE_LOAD_FAILED, unpack)
    } catch (unpack: UnsatisfiedLinkError) {
        // Both attempts are kept: "no such library on the system path" and "no such resource in
        // the jar" are different bugs with different fixes, and either one alone does not say
        // which of the two happened.
        unpack.addSuppressed(systemLoad)
        throw H3GridUnavailableException(NATIVE_LOAD_FAILED, unpack)
    }

    override fun cellAt(point: GeoPoint, resolution: Int): H3Cell =
        H3Cell(h3.latLngToCell(point.lat, point.lng, resolution))

    override fun gridDisk(origin: H3Cell, k: Int): Set<H3Cell> =
        h3.gridDisk(origin.index, k).mapTo(LinkedHashSet()) { H3Cell(it) }

    override fun center(cell: H3Cell): GeoPoint = h3.cellToLatLng(cell.index).let { GeoPoint(it.lat, it.lng) }

    override fun parent(cell: H3Cell, resolution: Int): H3Cell = H3Cell(h3.cellToParent(cell.index, resolution))

    private const val NATIVE_LOAD_FAILED = "com.uber:h3 could not load its native library for this ABI"
}
