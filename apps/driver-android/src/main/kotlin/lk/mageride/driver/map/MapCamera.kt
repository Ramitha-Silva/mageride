package lk.mageride.driver.map

/** Where the camera should sit when nothing else has said. */
internal data class MapCamera(val lat: Double, val lng: Double, val zoom: Double = DEFAULT_ZOOM) {
    internal companion object {
        /** Close enough to read street names, wide enough to see the next junction. */
        const val DEFAULT_ZOOM: Double = 15.0

        /** Colombo Fort. The cold-start camera before the first GNSS fix arrives. */
        val Default: MapCamera = MapCamera(lat = 6.9344, lng = 79.8428, zoom = 12.0)
    }
}
