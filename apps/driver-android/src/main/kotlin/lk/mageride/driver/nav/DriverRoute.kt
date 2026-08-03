package lk.mageride.driver.nav

/**
 * Every destination the Driver App has, as one sealed hierarchy.
 *
 * **The shell owns the route table; the screen groups own the screens.** C068–C075 each register
 * their composables against the entries below rather than inventing paths, which is what makes a
 * cross-group navigation (`Profile Setup -> Permissions -> Home`, AL-27) a compile-time reference
 * instead of a string two components have to spell the same way.
 *
 * `path` is the navigation-compose route pattern; `{}` segments are arguments. Nothing outside
 * this file writes one.
 */
internal sealed interface DriverRoute {

    /** The route pattern this destination is registered under. */
    val path: String

    // ---- C068 · auth / onboarding ------------------------------------------------------

    /** SCR-DA-001 — boot + driver-info router. The start destination; it decides where to go. */
    data object Splash : DriverRoute {
        override val path: String = "splash"
    }

    /** SCR-DA-002 — language + operating city, first run only. */
    data object LanguageCity : DriverRoute {
        override val path: String = "onboarding/lang-city"
    }

    /** SCR-DA-003 — +94 phone then SMS OTP. Phone-OTP only, no Google Sign-In (US-11.5). */
    data object Login : DriverRoute {
        override val path: String = "login"
    }

    /** SCR-DA-003a — driver profile setup. Precedes Home and needs NO vehicle (AL-27). */
    data object ProfileSetup : DriverRoute {
        override val path: String = "profile-setup"
    }

    /** SCR-DA-007 — runtime permissions, with the Settings deep link on denial. */
    data object Permissions : DriverRoute {
        override val path: String = "permissions"
    }

    // ---- C069 · vehicle onboarding -----------------------------------------------------

    /** SCR-DA-004…004c — the optional Mode-C-only four-step wizard. */
    data object VehicleOnboarding : DriverRoute {
        override val path: String = "vehicle/onboard"
    }

    /** SCR-DA-005 — camera capture with the draggable-corner crop. Shared by C068 and C069. */
    data object DocumentCapture : DriverRoute {
        override val path: String = "document/capture"
    }

    /** SCR-DA-006 — the four-document verdict list. */
    data object VehicleOnboardingStatus : DriverRoute {
        override val path: String = "vehicle/onboard/status"
    }

    /** SCR-DA-026 / 026a — My Vehicles, and its no-vehicles empty state. */
    data object Vehicles : DriverRoute {
        override val path: String = "vehicles"
    }

    // ---- C070 · dashboard / dispatch ---------------------------------------------------

    /** SCR-DA-010 — the dashboard. Bottom-nav tab 1, and where a `ride_offer` push lands. */
    data object Home : DriverRoute {
        override val path: String = "home"
    }

    /**
     * SCR-DA-013 — the Directional Travel filter (DT-01..DT-08).
     *
     * Added by C070. The wireframe draws it with a `‹` app bar over a full screen, so it is a
     * destination rather than a sheet on Home; SCR-DA-014 is the opposite and is deliberately
     * **not** here — a fifteen-second offer is a takeover the dashboard owns, which is why
     * `PushRouter` routes a `ride_offer` to [Home].
     */
    data object Directional : DriverRoute {
        override val path: String = "standby/directional"
    }

    /**
     * The ride in progress — accepted through to payment.
     *
     * @property rideId The ride. `mageride://ride/{id}` and `mageride://package/{id}` both
     *   resolve here; see `PushRouter`.
     */
    data class ActiveRide(val rideId: String) : DriverRoute {
        override val path: String = "ride/$rideId"

        companion object {
            const val ARG_RIDE_ID: String = "rideId"

            /** The pattern the NavHost registers, as distinct from one instance's concrete path. */
            const val PATTERN: String = "ride/{$ARG_RIDE_ID}"
        }
    }

    // ---- C072 · jobs / level / earnings ------------------------------------------------
    //
    // Δ C072: the shell's table had `Jobs` and nothing else, and this group is four screens. The
    // other three are pushed destinations — every one of their wireframes draws a `‹` app bar —
    // so they are registered here rather than invented at a call site, the same way C070 added
    // the four SCR-DA-036 rows it found missing.

    /** SCR-DA-017 — the Job Board. Bottom-nav tab 2, and **post-intent only** (US-6A.5). */
    data object Jobs : DriverRoute {
        override val path: String = "jobs"
    }

    /**
     * SCR-DA-018 — the driver's own upcoming scheduled rides.
     *
     * Under `jobs/` because that is what it is: the Job Board is what a driver bids on and this is
     * what came of it. US-6A.15's 30-minute reminder opens it; see `PushRouter`.
     */
    data object ScheduledRides : DriverRoute {
        override val path: String = "jobs/scheduled"
    }

    /** SCR-DA-019 — Driver Level, points and the US-6A.14 stats. Opened by SCR-DA-010's `L3` badge. */
    data object DriverLevel : DriverRoute {
        override val path: String = "driver/level"
    }

    /** SCR-DA-020 — the earnings dashboard. Opened by SCR-DA-010's *"Today: 4 trips · Rs 3,180"*. */
    data object Earnings : DriverRoute {
        override val path: String = "earnings"
    }

    // ---- C073 · wallet / daily fee -----------------------------------------------------
    //
    // Δ C073: the shell's table had `Wallet` alone and this group is five screens. SCR-DA-021 is
    // the tab; the other four are pushed destinations — every one of their wireframes draws a `‹`
    // app bar — so they are registered here rather than invented at a call site, the same way
    // C072 added its three.

    /** SCR-DA-021 — wallet, daily fee and where the other four open from. Bottom-nav tab 3, and `mageride://wallet`. */
    data object Wallet : DriverRoute {
        override val path: String = "wallet"
    }

    /** SCR-DA-022 — top up by card / OnePay wallet / LankaQR, and the bulk-voucher ladder (US-9.18/9.19). */
    data object WalletTopUp : DriverRoute {
        override val path: String = "wallet/top-up"
    }

    /** SCR-DA-023 — ask another driver for credit **by Driver ID** (US-9.10, AL-34: no QR scan). */
    data object WalletRequestCredit : DriverRoute {
        override val path: String = "wallet/request-credit"
    }

    /** SCR-DA-024 — the approval inbox and the direct send (US-9.11/9.12, US-9.20/9.21). */
    data object WalletTransfer : DriverRoute {
        override val path: String = "wallet/transfer"
    }

    /** SCR-DA-025 — the ledger, filtered, with US-9A.19's statement download. */
    data object WalletHistory : DriverRoute {
        override val path: String = "wallet/history"
    }

    // ---- C073–C075 · menu, profile, documents, support ---------------------------------

    /**
     * The Menu tab — **the navigation entry point that replaces the hamburger** (AL-31).
     *
     * Bottom-nav tab 4. Everything not reachable from the other three hangs off it.
     */
    data object Menu : DriverRoute {
        override val path: String = "menu"
    }

    /** Driver documents and their expiry state. `mageride://documents` lands here (E-03). */
    data object Documents : DriverRoute {
        override val path: String = "documents"
    }

    /** SCR-DA-029 — the driver profile, its emergency contact and log out (C074). */
    data object Profile : DriverRoute {
        override val path: String = "profile"
    }

    /** Support and safety. */
    data object Support : DriverRoute {
        override val path: String = "support"
    }

    // ---- The four SCR-DA-036 destinations the shell's table was missing (Δ C070) ---------
    //
    // AL-31 makes the Menu tab the whole navigation drawer, and D2' §SCR-DA-036 names EIGHT
    // destinations it routes to. Four of them had no entry here, so C070 added them rather than
    // pointing four drawer rows at the nearest existing screen — a row that opens the wrong
    // screen is worse than one that says which prompt owns it. Their composables belong to
    // C074–C075; the NavHost registers the one C075 still owns against the standing placeholder.

    /** SCR-DA-027 — GPS tracker pairing (C074). */
    data object TrackerPairing : DriverRoute {
        override val path: String = "vehicle/tracker"
    }

    /** SCR-DA-028 — Mode B sharing management (C074). */
    data object Sharing : DriverRoute {
        override val path: String = "sharing"
    }

    /** SCR-DA-030 — ride history, and the rate-passenger sheet on it (C074). */
    data object RideHistory : DriverRoute {
        override val path: String = "history"
    }

    /** SCR-DA-034 — the alerts list (C075). `mageride://` has no host for it; it is menu-reached. */
    data object Notifications : DriverRoute {
        override val path: String = "notifications"
    }

    companion object {

        /**
         * Every static destination, for the NavHost to register and for the coverage test to
         * walk. [ActiveRide] is absent because it is parameterised — the NavHost registers
         * [ActiveRide.PATTERN] instead.
         */
        val Static: List<DriverRoute> = listOf(
            Splash,
            LanguageCity,
            Login,
            ProfileSetup,
            Permissions,
            VehicleOnboarding,
            DocumentCapture,
            VehicleOnboardingStatus,
            Vehicles,
            Home,
            Directional,
            Jobs,
            ScheduledRides,
            DriverLevel,
            Earnings,
            Wallet,
            WalletTopUp,
            WalletRequestCredit,
            WalletTransfer,
            WalletHistory,
            Menu,
            Documents,
            Profile,
            Support,
            TrackerPairing,
            Sharing,
            RideHistory,
            Notifications,
        )
    }
}
