package lk.mageride.passenger.home

import lk.mageride.shared.data.models.ServiceMode
import lk.mageride.shared.data.models.VehicleType
import lk.mageride.shared.realtime.VehicleFrame
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/**
 * SCR-PA-006's answer, as a value.
 *
 * The filter is the one piece of C078 with no Android in it at all, which is why it is a data class
 * rather than a `when` inside a composable: what a passenger switched off has to survive a
 * recomposition, a rotation and a batch of frames arriving, and all three are easier to get wrong
 * than the arithmetic is.
 */
class MapFilterTest {

    @Test
    fun everything_is_on_by_default() {
        // A passenger who has never opened the filter sees the whole map. The opposite default —
        // an empty set meaning "no restriction" — reads the same in code and shows nothing on the
        // first launch, which is US-7.14's empty state fired at someone who did nothing.
        val filter = MapFilter()

        assertTrue(filter.allows(bus()), "a bus")
        assertTrue(filter.allows(tuk()), "an on-demand tuk")
        assertTrue(filter.allows(privateVan()), "a Mode B van")
        assertFalse(filter.showsNothing)
    }

    @Test
    fun switching_a_mode_off_hides_only_that_mode() {
        // US-7.7 — "filter the live map by mode and vehicle type". Mode and type are independent
        // axes: a passenger who wants buses only turns off B and C, and a passenger who wants
        // everything except trains turns off one chip.
        val noOnDemand = MapFilter().withMode(ServiceMode.C, enabled = false)

        assertTrue(noOnDemand.allows(bus()))
        assertTrue(noOnDemand.allows(privateVan()))
        assertFalse(noOnDemand.allows(tuk()), "the Mode C tuk is filtered out")
    }

    @Test
    fun switching_a_type_off_hides_it_in_every_mode() {
        // A type chip is about the vehicle, not about who operates it: a passenger who has turned
        // off vans does not want the Mode B one either.
        val noVans = MapFilter().withType(VehicleType.VAN, enabled = false)

        assertFalse(noVans.allows(privateVan()))
        assertTrue(noVans.allows(bus()))
    }

    @Test
    fun a_type_with_no_chip_is_never_hidden_by_the_chips() {
        // AL-09 has ten canonical types; SCR-PA-006 draws eight chips. Trucks and mini-trucks are
        // freight (Mode D's world) and have no chip, so a type set that does not mention them must
        // mean "not filtered" rather than "off" — otherwise switching one unrelated chip would
        // silently erase every lorry from the map.
        val onlyBuses = MapFilter(types = setOf(VehicleType.BUS))

        assertTrue(onlyBuses.allows(frame(VehicleType.TRUCK, ServiceMode.A)))
        assertTrue(onlyBuses.allows(frame(VehicleType.MINI_TRUCK, ServiceMode.A)))
        assertFalse(onlyBuses.allows(frame(VehicleType.SEDAN, ServiceMode.C)), "sedan has a chip and is off")
    }

    @Test
    fun a_frame_that_declares_neither_is_always_drawn() {
        // `VehicleFrame.type` and `.mode` are both nullable on the wire, and a frame that omits
        // them is still a vehicle at a coordinate. Dropping it would make an under-populated
        // payload look like an empty map — the one failure a passenger cannot tell from an outage.
        val frame = VehicleFrame(vehicleId = ANON, lat = 6.9, lng = 79.8)

        assertTrue(MapFilter().allows(frame))
        assertTrue(MapFilter(modes = emptySet(), types = emptySet()).allows(frame))
    }

    @Test
    fun an_empty_axis_is_reported_as_showing_nothing() {
        // What US-7.14 needs to tell "you switched everything off" apart from "there is nothing
        // here". Either axis emptied is enough: no mode selected and no type selected both draw an
        // empty map, and the message is the same one — turn something back on.
        assertTrue(MapFilter(modes = emptySet()).showsNothing)
        assertTrue(MapFilter(types = emptySet()).showsNothing)
        assertFalse(MapFilter(modes = setOf(ServiceMode.A)).showsNothing)
    }

    @Test
    fun the_chips_are_the_eight_passenger_types_in_the_wireframes_order() {
        // Pinned because the order is the wireframe's and the set is AL-09's: a chip row that
        // gained a truck or lost the three-wheeler would be a §0.2 drift nobody would notice in a
        // screenshot.
        assertEquals(
            listOf(
                VehicleType.BUS,
                VehicleType.TRAIN,
                VehicleType.THREE_WHEELER,
                VehicleType.FLEX,
                VehicleType.SEDAN,
                VehicleType.MINI_VAN,
                VehicleType.VAN,
                VehicleType.MOTORBIKE,
            ),
            MapFilter.CHIP_TYPES.toList(),
        )
        assertEquals(listOf(ServiceMode.A, ServiceMode.B, ServiceMode.C), MapFilter.MODES)
    }

    // ------------------------------------------------------------------------------------------

    private fun bus() = frame(VehicleType.BUS, ServiceMode.A)
    private fun tuk() = frame(VehicleType.THREE_WHEELER, ServiceMode.C)
    private fun privateVan() = frame(VehicleType.VAN, ServiceMode.B)

    private fun frame(type: VehicleType, mode: ServiceMode) = VehicleFrame(
        vehicleId = ANON,
        lat = 6.9271,
        lng = 79.8612,
        type = type,
        mode = mode,
    )

    private companion object {
        const val ANON = "01JVEH0000000000000000009"
    }
}
