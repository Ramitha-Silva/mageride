package lk.mageride.passenger.map

/**
 * Where the camera should sit when nothing else has said.
 *
 * @property lat Degrees.
 * @property lng Degrees.
 * @property zoom MapLibre zoom level.
 */
internal data class MapCamera(val lat: Double, val lng: Double, val zoom: Double = DEFAULT_ZOOM) {
    internal companion object {
        /**
         * Close enough to read street names, wide enough to see the vehicles the R-06 view holds.
         *
         * The 19 res-7 cells reach about 3 km; at zoom 15 roughly a third of that is on screen,
         * which is what a passenger looking for the nearest tuk actually wants to see. Zooming
         * out to fit all nineteen would put the passenger's own position in a field of pins.
         */
        const val DEFAULT_ZOOM: Double = 15.0

        /** Colombo Fort. The cold-start camera before the first GNSS fix arrives. */
        val Default: MapCamera = MapCamera(lat = 6.9344, lng = 79.8428, zoom = 12.0)
    }
}
