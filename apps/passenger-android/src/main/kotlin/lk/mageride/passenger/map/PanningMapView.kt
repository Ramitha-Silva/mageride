package lk.mageride.passenger.map

import android.annotation.SuppressLint
import android.content.Context
import android.view.MotionEvent
import org.maplibre.android.maps.MapView

/**
 * A [MapView] that claims a gesture the moment a finger lands on it.
 *
 * **The problem this exists for.** A map is a scrollable surface inside other scrollable surfaces:
 * SCR-PA-026 puts one in the middle of a `verticalScroll` column, and SCR-PA-010 puts one under a
 * `ModalNavigationDrawer`. Without this, a drag over the map is arbitrated between the map and
 * whichever ancestor also wants it, and the result is not "one of them wins" — it is both of them
 * moving a little. A vertical pan on SCR-PA-026 crawled because the column took half of every
 * drag; a horizontal pan on SCR-PA-010 fought the drawer's edge swipe the same way.
 *
 * **Why the fix is the old View contract rather than a Compose modifier.** `MapView` predates
 * Compose and does its own gesture detection in `onTouchEvent`; there is no Compose gesture here
 * to give priority to. `requestDisallowInterceptTouchEvent` is exactly the ask —
 * *"this stream is mine, do not intercept it"* — and Compose honours it: `AndroidView` wraps this
 * in an `AndroidViewHolder`, whose `requestDisallowInterceptTouchEvent` feeds Compose's pointer
 * interop and stops ancestor gesture detectors from stealing the stream.
 *
 * The flag is released on UP and CANCEL, so the parent gets everything back the instant the finger
 * leaves: a drag that *starts* on the sheet still scrolls the sheet, which is what SCR-PA-010's
 * recents list needs.
 *
 * Nothing is consumed here — `super.onTouchEvent` still runs, so MapLibre's own pan, pinch, rotate
 * and tilt are untouched.
 */
@SuppressLint("ViewConstructor") // Created in code by `MageRideMap`; never inflated from XML.
internal class PanningMapView(context: Context) : MapView(context) {

    override fun onTouchEvent(event: MotionEvent): Boolean {
        when (event.actionMasked) {
            MotionEvent.ACTION_DOWN -> parent?.requestDisallowInterceptTouchEvent(true)

            MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL ->
                parent?.requestDisallowInterceptTouchEvent(false)

            else -> Unit
        }
        return super.onTouchEvent(event)
    }
}
