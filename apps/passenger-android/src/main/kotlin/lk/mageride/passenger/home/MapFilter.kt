package lk.mageride.passenger.home

import lk.mageride.shared.data.models.ServiceMode
import lk.mageride.shared.data.models.VehicleType
import lk.mageride.shared.realtime.VehicleFrame

/**
 * SCR-PA-006's answer — which of what is on the map (US-7.7).
 *
 * **A view preference and nothing more.** Three things decide whether a vehicle reaches this
 * filter at all, and none of them is here: a Mode C vehicle on active hire is dropped by
 * fanout-svc from the public geocell groups (US-7.16), a vehicle past the freshness window is
 * dropped for staleness (US-7.17), and a Mode B vehicle appears only for a passenger the
 * `share:{userId}` entitlement covers (D-23). Turning "Mode B" on cannot conjure a vehicle a
 * passenger has no grant for, and turning "Mode C" on cannot resurrect one that is on a fare.
 * `MapFilterTest` pins that.
 *
 * @property modes Which of A / B / C are shown. All three by default.
 * @property types Which of [CHIP_TYPES] are shown. All eight by default.
 */
internal data class MapFilter(
    val modes: Set<ServiceMode> = ServiceMode.entries.toSet(),
    val types: Set<VehicleType> = CHIP_TYPES,
) {

    /**
     * Whether [frame] belongs on the map.
     *
     * A vehicle whose type has **no chip** is never hidden by the type row — see [CHIP_TYPES].
     * The mode toggle still applies to it, because every vehicle has a mode.
     */
    fun allows(frame: VehicleFrame): Boolean {
        val modeAllowed = frame.mode == null || frame.mode in modes
        val type = frame.type
        val typeAllowed = type == null || type !in CHIP_TYPES || type in types
        return modeAllowed && typeAllowed
    }

    /** The wireframe's mode row. */
    fun withMode(mode: ServiceMode, enabled: Boolean): MapFilter =
        copy(modes = if (enabled) modes + mode else modes - mode)

    /** The wireframe's type chips. */
    fun withType(type: VehicleType, enabled: Boolean): MapFilter =
        copy(types = if (enabled) types + type else types - type)

    /** Whether anything at all is shown — an all-off filter is why the map can be empty. */
    val showsNothing: Boolean get() = modes.isEmpty() || types.isEmpty()

    internal companion object {

        /**
         * The eight chips `passenger_android.html` draws, in its order.
         *
         * **Not all eleven canonical types**, and the three that are missing are missing for a
         * reason: `truck` and `mini_truck` are delivery-only (AL-09, Epic 20) and a passenger does
         * not hail one, and `private` is the Mode B grey — already covered by the Mode B toggle
         * one row above, so a chip for it would be a second control for the same vehicles.
         *
         * A vehicle of an unlisted type is therefore **unfilterable rather than hidden**: it is
         * still drawn, because a marker that vanished with no control to bring it back is a bug a
         * passenger cannot diagnose.
         *
         * **Train is its own chip**, which is the whole of US-7.7's *"only trains"* — it is a
         * distinct `VehicleType` with a distinct colour (§0.2 calls the rail red out explicitly),
         * so filtering it separately needs no special case.
         */
        val CHIP_TYPES: Set<VehicleType> = linkedSetOf(
            VehicleType.BUS,
            VehicleType.TRAIN,
            VehicleType.THREE_WHEELER,
            VehicleType.FLEX,
            VehicleType.SEDAN,
            VehicleType.MINI_VAN,
            VehicleType.VAN,
            VehicleType.MOTORBIKE,
        )

        /** The mode rows, in the wireframe's order. */
        val MODES: List<ServiceMode> = listOf(ServiceMode.A, ServiceMode.B, ServiceMode.C)
    }
}
