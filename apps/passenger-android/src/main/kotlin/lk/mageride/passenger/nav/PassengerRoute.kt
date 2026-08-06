package lk.mageride.passenger.nav

/**
 * Every destination the Passenger App has, as one sealed hierarchy.
 *
 * **The shell owns the route table; the screen groups own the screens.** C077–C084 each register
 * their composables against the entries below rather than inventing paths, which is what makes a
 * cross-group navigation (`Live map -> Mode B request`, AL-23; `Ride -> Pay fare`, AL-47) a
 * compile-time reference instead of a string two components have to spell the same way.
 *
 * `path` is the navigation-compose route pattern; `{}` segments are arguments. Nothing outside
 * this file writes one.
 *
 * **What is deliberately NOT here.** Eight of the wireframe's screen ids are sheets, popups or
 * overlays rather than destinations, and registering a route for one would put a back-stack entry
 * behind something the user dismisses with a swipe: SCR-PA-006 (mode filter, a modal bottom
 * sheet), SCR-PA-007 (the Mode A marker popup), SCR-PA-012a (paste-link sheet), SCR-PA-015a (the
 * call-type chooser), SCR-PA-026a (save-address sheet), SCR-PA-030a (raise-ticket sheet),
 * SCR-PA-031 (the D-31 update dialog, which the shell hosts) and SCR-PA-032 (the offline banner,
 * likewise). Each belongs to the screen that opens it.
 */
internal sealed interface PassengerRoute {

    /** The route pattern this destination is registered under. */
    val path: String

    // ---- C077 · auth / onboarding ------------------------------------------------------

    /** SCR-PA-001 — boot + session router. The start destination; it decides where to go. */
    data object Splash : PassengerRoute {
        override val path: String = "splash"
    }

    /** SCR-PA-002 — the three-slide carousel and the language boxes, first run only (AL-26/AL-36). */
    data object Onboarding : PassengerRoute {
        override val path: String = "onboarding"
    }

    /** SCR-PA-003 — +94 phone then SMS OTP. Phone-OTP only, no Google Sign-In (AL-07). */
    data object Login : PassengerRoute {
        override val path: String = "login"
    }

    /** SCR-PA-004 — first profile: name and an optional photo (US-1.5). */
    data object ProfileSetup : PassengerRoute {
        override val path: String = "profile-setup"
    }

    /** SCR-PA-005 — the location-permission rationale, with the Settings deep link on denial. */
    data object LocationPermission : PassengerRoute {
        override val path: String = "permissions/location"
    }

    // ---- C078 · the live map and search ------------------------------------------------

    /**
     * SCR-PA-010 — the live map. Bottom-nav tab 1 and the app's home.
     *
     * This is the one screen that owns the R-06 subscription: 19 res-7 cells around the
     * passenger, joined through the shell's `live/PassengerLiveMap`.
     */
    data object LiveMap : PassengerRoute {
        override val path: String = "map"
    }

    /** SCR-PA-008 — destination search. **Geo only** — a route number returns places (AL-17). */
    data object SearchLocation : PassengerRoute {
        override val path: String = "search"
    }

    // ---- C079 · booking ----------------------------------------------------------------

    /** SCR-PA-009 — the multimodal results and the booking itself (AL-18, AL-19). */
    data object RideBooking : PassengerRoute {
        override val path: String = "booking"
    }

    /** SCR-PA-010b — who the ride is for, when it is not the booker (P-01, US-8.19). */
    data object ProxyRider : PassengerRoute {
        override val path: String = "booking/proxy-rider"
    }

    /** SCR-PA-012 — a parcel instead of a person (US-20.1/20.2, P-06). */
    data object PackageBooking : PassengerRoute {
        override val path: String = "booking/package"
    }

    /** SCR-PA-013 — a ride in the future. Destination is mandatory (AL-36). */
    data object ScheduleRide : PassengerRoute {
        override val path: String = "booking/schedule"
    }

    /**
     * SCR-PA-011 — the **rider's** side of a proxy pickup (P-02, P-13).
     *
     * Reached from an FCM `location_request`, which is a **silent data message carrying no
     * deeplink at all** — `{kind, requestId, bookerName, ttl}` and nothing else. `PushRouter`
     * therefore builds this destination from `data.requestId`; see its KDoc.
     *
     * @property requestId `rides.location_requests.request_id`, the public handle. It scopes the
     *   confirm, the decline and the `booker:{bookerId}:loc-req:{requestId}` hub group.
     */
    data class ConfirmPickup(val requestId: String) : PassengerRoute {
        override val path: String = "pickup-confirm/$requestId"

        companion object {
            const val ARG_REQUEST_ID: String = "requestId"

            /** The pattern the NavHost registers, as distinct from one instance's concrete path. */
            const val PATTERN: String = "pickup-confirm/{$ARG_REQUEST_ID}"
        }
    }

    // ---- C080 · the ride and its payment -----------------------------------------------

    /**
     * SCR-PA-014 — matching, with the two-minute no-driver timeout (US-6A.11).
     *
     * Parameterised even though it precedes assignment: the ride exists from `POST /v1/rides` on,
     * and an app killed during the search has to come back to *this* ride rather than to the
     * booking form.
     */
    data class FindingDriver(val rideId: String) : PassengerRoute {
        override val path: String = "ride/$rideId/finding"

        companion object {
            const val ARG_RIDE_ID: String = "rideId"
            const val PATTERN: String = "ride/{$ARG_RIDE_ID}/finding"
        }
    }

    /**
     * SCR-PA-015 — the ride in progress, Accepted through to Completed.
     *
     * `mageride://ride/{id}` resolves here — see `PushRouter`. A **package** ride does not:
     * R-01 keeps one aggregate, but the passenger side has two distinct screens for it
     * (SCR-PA-020/021), so `mageride://package/{id}` resolves to [PackageTracking] instead.
     */
    data class ActiveRide(val rideId: String) : PassengerRoute {
        override val path: String = "ride/$rideId"

        companion object {
            const val ARG_RIDE_ID: String = "rideId"
            const val PATTERN: String = "ride/{$ARG_RIDE_ID}"
        }
    }

    /** SCR-PA-016 — Cash / Wallet / Driver QR. No surcharge on any rail (AL-57). */
    data class PaymentMethod(val rideId: String) : PassengerRoute {
        override val path: String = "ride/$rideId/payment-method"

        companion object {
            const val ARG_RIDE_ID: String = "rideId"
            const val PATTERN: String = "ride/{$ARG_RIDE_ID}/payment-method"
        }
    }

    /** SCR-PA-017 — **the passenger scans the driver's QR**; the app renders none (AL-22, AL-47). */
    data class PayFare(val rideId: String) : PassengerRoute {
        override val path: String = "ride/$rideId/pay"

        companion object {
            const val ARG_RIDE_ID: String = "rideId"
            const val PATTERN: String = "ride/{$ARG_RIDE_ID}/pay"
        }
    }

    /** SCR-PA-018 — the receipt, and the "Pay now" CTA while payment is pending. */
    data class TripSummary(val rideId: String) : PassengerRoute {
        override val path: String = "ride/$rideId/summary"

        companion object {
            const val ARG_RIDE_ID: String = "rideId"
            const val PATTERN: String = "ride/{$ARG_RIDE_ID}/summary"
        }
    }

    /** SCR-PA-019 — stars, reason chips and an optional comment (US-18.1). */
    data class RateDriver(val rideId: String) : PassengerRoute {
        override val path: String = "ride/$rideId/rate"

        companion object {
            const val ARG_RIDE_ID: String = "rideId"
            const val PATTERN: String = "ride/{$ARG_RIDE_ID}/rate"
        }
    }

    // ---- C081 · packages and history ---------------------------------------------------

    /**
     * SCR-PA-020 **and** SCR-PA-021 — package tracking, sender or recipient.
     *
     * **One destination for two screens, because the link is the same one for both.**
     * notification-svc mints `mageride://package/{rideId}` and sends it to the sender on
     * `package_delivered` and to the recipient on `package_picked_up`; which of the two screens a
     * given handset should draw is a fact about *the ride* (`passengerId` versus
     * `recipientPhone`), not about the URI. C081 reads the ride and picks — the same shape
     * `ActiveRideScreen` uses on the driver side to hand over to `DeliveryScreen`.
     *
     * A recipient with no app gets an SMS and the SCR-WT web page instead (P-09, AL-45); nothing
     * on this route is reachable by logging in.
     */
    data class PackageTracking(val rideId: String) : PassengerRoute {
        override val path: String = "package/$rideId"

        companion object {
            const val ARG_RIDE_ID: String = "rideId"
            const val PATTERN: String = "package/{$ARG_RIDE_ID}"
        }
    }

    /** SCR-PA-022 — Past / Scheduled / Packages. Bottom-nav tab 2. */
    data object Trips : PassengerRoute {
        override val path: String = "trips"
    }

    /** SCR-PA-023 — one trip, its route and its receipt. */
    data class TripDetails(val tripId: String) : PassengerRoute {
        override val path: String = "trips/$tripId"

        companion object {
            const val ARG_TRIP_ID: String = "tripId"
            const val PATTERN: String = "trips/{$ARG_TRIP_ID}"
        }
    }

    // ---- C082 · Mode B ------------------------------------------------------------------

    /**
     * SCR-PA-024 — ask a private vehicle's owner for access (US-4.6, AL-23, AL-25).
     *
     * **The vehicle id is optional, and that is the whole of AL-23.** Opened from a Mode B marker
     * it arrives pre-filled; opened from the drawer's *"Private transport"* row there is no
     * marker to read one from and the passenger types it. A route that demanded the argument
     * would make the drawer row impossible; one that never carried it would make the marker tap
     * lose what the passenger just tapped.
     *
     * @property vehicleId The vehicle the request is about, or `null` when it is typed in.
     */
    data class ModeBRequest(val vehicleId: String? = null) : PassengerRoute {
        override val path: String =
            if (vehicleId.isNullOrBlank()) BASE else "$BASE?$ARG_VEHICLE_ID=$vehicleId"

        companion object {
            const val ARG_VEHICLE_ID: String = "vehicleId"
            private const val BASE = "private-transport"

            /**
             * The pattern the NavHost registers.
             *
             * An **optional** navigation-compose argument, spelled as a query parameter: the
             * pattern matches `private-transport` and `private-transport?vehicleId=…` alike, which
             * is what lets one destination serve both entry points. The `composable(…)` call has
             * to declare it `nullable` with a `null` default, or the argument-less form 404s
             * inside the graph.
             */
            const val PATTERN: String = "$BASE?$ARG_VEHICLE_ID={$ARG_VEHICLE_ID}"
        }
    }

    /** SCR-PA-025 — the passenger's own Mode B subscriptions, one card per vehicle. */
    data object Subscriptions : PassengerRoute {
        override val path: String = "subscriptions"
    }

    /** SCR-PA-025a — pay a subscription. `payTo` is the **owner's** account, never MageRide (AL-59). */
    data class SubscriptionPayment(val subscriptionId: String) : PassengerRoute {
        override val path: String = "subscriptions/$subscriptionId/pay"

        companion object {
            const val ARG_SUBSCRIPTION_ID: String = "subscriptionId"
            const val PATTERN: String = "subscriptions/{$ARG_SUBSCRIPTION_ID}/pay"
        }
    }

    /** SCR-PA-025b — that subscription's own statement (US-NEW.payments h). */
    data class SubscriptionPayments(val subscriptionId: String) : PassengerRoute {
        override val path: String = "subscriptions/$subscriptionId/payments"

        companion object {
            const val ARG_SUBSCRIPTION_ID: String = "subscriptionId"
            const val PATTERN: String = "subscriptions/{$ARG_SUBSCRIPTION_ID}/payments"
        }
    }

    // ---- C083 · addresses and settings --------------------------------------------------

    /** SCR-PA-026 — Home / Work / labelled addresses on an OSM pin (AL-14, US-22.1/22.2). */
    data object SavedAddresses : PassengerRoute {
        override val path: String = "addresses"
    }

    /** SCR-PA-027 — profile, language, notifications, default payment method. */
    data object Settings : PassengerRoute {
        override val path: String = "settings"
    }

    /** SCR-PA-027b — name, photo, SOS contacts. **No language selector** (AL-26). */
    data object EditProfile : PassengerRoute {
        override val path: String = "settings/profile"
    }

    // ---- C084 · comms, safety, support --------------------------------------------------

    /**
     * SCR-PA-028 — the in-app VoIP call (US-6A.16, D-24).
     *
     * Reached from SCR-PA-015a's *"Free call"*. *"Normal call"* never comes here: AL-48 removed
     * masking, so it is a direct dial to the driver's real number placed where it was chosen.
     *
     * @property rideId The ride the call belongs to; voip-svc scopes the token to it.
     */
    data class VoipCall(val rideId: String) : PassengerRoute {
        override val path: String = "call/$rideId"

        companion object {
            const val ARG_RIDE_ID: String = "rideId"
            const val PATTERN: String = "call/{$ARG_RIDE_ID}"
        }
    }

    /**
     * SCR-PA-029 — the passenger SOS (US-12.1, D-33).
     *
     * Parameterised by the ride even though `safety.yaml`'s `POST /v1/sos` marks `rideId`
     * optional — the wireframe's only door to this screen is SCR-PA-015's `⛨ SOS` button, and the
     * screen's own copy is *"Sending GPS + trip to emergency contacts"*. A trip-less passenger
     * alarm would need an entry point no wireframe draws; if one is ever added, the contract
     * already permits it and this is the type that would change.
     *
     * @property rideId The trip the alarm is raised on.
     */
    data class Sos(val rideId: String) : PassengerRoute {
        override val path: String = "sos/$rideId"

        companion object {
            const val ARG_RIDE_ID: String = "rideId"
            const val PATTERN: String = "sos/{$ARG_RIDE_ID}"
        }
    }

    /** SCR-PA-030 — FAQ, ticket list and thread. Bottom-nav tab 3. */
    data object Support : PassengerRoute {
        override val path: String = "support"
    }

    companion object {

        /**
         * Every static destination, for the NavHost to register and for the coverage test to
         * walk. The parameterised ones are absent because a route table cannot hold an instance
         * of them — the NavHost registers their `PATTERN`s instead.
         */
        val Static: List<PassengerRoute> = listOf(
            Splash,
            Onboarding,
            Login,
            ProfileSetup,
            LocationPermission,
            LiveMap,
            SearchLocation,
            RideBooking,
            ProxyRider,
            PackageBooking,
            ScheduleRide,
            Trips,
            Subscriptions,
            SavedAddresses,
            Settings,
            EditProfile,
            Support,
        )

        /**
         * The patterns the NavHost registers for the parameterised destinations.
         *
         * Listed here rather than only at the `composable(…)` call sites so `NavigationShellTest`
         * can assert that no two destinations — static or parameterised — collide. `ride/{rideId}`
         * and `ride/{rideId}/pay` are distinct patterns to navigation-compose; `ride/{rideId}` and
         * a hypothetical `ride/{id}` would not be, and that is the collision worth catching.
         */
        val Patterns: List<String> = listOf(
            ConfirmPickup.PATTERN,
            FindingDriver.PATTERN,
            ActiveRide.PATTERN,
            PaymentMethod.PATTERN,
            PayFare.PATTERN,
            TripSummary.PATTERN,
            RateDriver.PATTERN,
            PackageTracking.PATTERN,
            TripDetails.PATTERN,
            ModeBRequest.PATTERN,
            SubscriptionPayment.PATTERN,
            SubscriptionPayments.PATTERN,
            VoipCall.PATTERN,
            Sos.PATTERN,
        )
    }
}
