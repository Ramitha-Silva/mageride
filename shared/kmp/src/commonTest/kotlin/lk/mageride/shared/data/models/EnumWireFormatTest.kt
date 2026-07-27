package lk.mageride.shared.data.models

import kotlinx.serialization.encodeToString
import lk.mageride.shared.serialization.MageRideJson
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/**
 * The C012 fence: **enums are exhaustive and match the DB CHECK domains exactly — no
 * client-invented values.**
 *
 * The two state machines below are asserted against the CHECK constraints C004 and C005 landed,
 * spelled out here so a typo, an omission or a stray extra value fails the build rather than a
 * `400` in the field. The remaining enums are asserted to serialise to the wire spellings their
 * contracts and CHECK constraints print.
 */
class EnumWireFormatTest {

    private inline fun <reified T> wireOf(value: T): String = MageRideJson.encodeToString(value).trim('"')

    // ---- the two state machines (C012 DoD) ---------------------------------------------------

    @Test
    fun ride_state_enumerates_exactly_the_ck_rides_state_values() {
        // db/migrations/0601__rides_rides.sql — ck_rides_state, D5' §6 / ADD Appendix B.2.
        val checkConstraint = listOf(
            "Requested", "Matching", "Offered", "Accepted", "DriverArrived", "InProgress",
            "Completed", "PaymentPending", "Paid", "CashSettled", "CashOnDeliveryCollected",
            "Disputed", "CancelledByRiderBeforeAccept", "CancelledByRiderAfterAccept",
            "CancelledByDriver", "ExpiredNoDriver", "NoShowRider", "NoShowDriver",
        )

        assertEquals(18, checkConstraint.size, "D5' §6 names eighteen ride states")
        assertEquals(checkConstraint.sorted(), RideState.entries.map { wireOf(it) }.sorted())
    }

    @Test
    fun payment_state_enumerates_exactly_the_ck_ride_payments_state_values() {
        // db/migrations/1002__fares_ride_payments.sql — ck_ride_payments_state. The landed CHECK
        // is the UNION of the base §9 DDL and the AL-47 rewrite: the rewrite added the two
        // driver-QR states and silently dropped PartiallyRefunded, which §19, ADD §9.1 and
        // fares.refunds.kind='partial' all still require (C005 note (b)).
        val checkConstraint = listOf(
            "Initiated", "Pending", "Succeeded", "Failed", "Retried", "FellBackToCash",
            "CashOnDelivery", "CashOnDeliveryCollected", "Overpaid", "Refunded",
            "PartiallyRefunded", "Disputed", "QrClaimedByPassenger", "DriverConfirmedQR",
        )

        assertEquals(14, checkConstraint.size)
        assertEquals(checkConstraint.sorted(), PaymentState.entries.map { wireOf(it) }.sorted())
        assertTrue(PaymentState.QrClaimedByPassenger in PaymentState.entries)
        assertTrue(PaymentState.DriverConfirmedQR in PaymentState.entries)
    }

    @Test
    fun the_ride_terminals_are_the_ten_the_state_machine_diagram_marks_terminal() {
        val terminal = RideState.entries.filter { it.isTerminal }.map { wireOf(it) }

        assertEquals(
            listOf(
                "CancelledByDriver", "CancelledByRiderAfterAccept", "CancelledByRiderBeforeAccept",
                "CashOnDeliveryCollected", "CashSettled", "Disputed", "ExpiredNoDriver",
                "NoShowDriver", "NoShowRider", "Paid",
            ),
            terminal.sorted(),
        )
        assertFalse(RideState.PaymentPending.isTerminal)
        assertFalse(RideState.Completed.isTerminal)
    }

    @Test
    fun the_payment_terminals_are_the_states_that_release_the_driver_earning() {
        // R-05: the earning posts only on a terminal money state.
        val terminal = PaymentState.entries.filter { it.isTerminal }.map { wireOf(it) }

        assertEquals(
            listOf(
                "CashOnDeliveryCollected",
                "Disputed",
                "DriverConfirmedQR",
                "FellBackToCash",
                "PartiallyRefunded",
                "Refunded",
                "Succeeded",
            ),
            terminal.sorted(),
        )
        assertFalse(PaymentState.Initiated.isTerminal)
        assertFalse(PaymentState.QrClaimedByPassenger.isTerminal)
    }

    @Test
    fun a_driver_may_hold_only_one_ride_in_the_four_exclusive_states() {
        // ux_rides_driver_busy (C004) / ADD Appendix B.2 invariant 2.
        assertEquals(
            listOf("Accepted", "DriverArrived", "InProgress", "PaymentPending"),
            RideState.DRIVER_EXCLUSIVE.map { wireOf(it) }.sorted(),
        )
    }

    // ---- vehicle types (AL-09) ---------------------------------------------------------------

    @Test
    fun vehicle_type_is_the_ten_canonical_values_and_there_is_no_car() {
        // registry.vehicles.vehicle_type CHECK (C003) — "car" maps to "sedan".
        assertEquals(
            listOf(
                "bus", "flex", "mini_truck", "mini_van", "motorbike", "sedan", "three_wheeler",
                "train", "truck", "van",
            ),
            VehicleType.entries.map { wireOf(it) }.sorted(),
        )
        assertFalse(VehicleType.entries.any { it.wire == "car" })
        VehicleType.entries.forEach { assertEquals(it.wire, wireOf(it)) }
    }

    @Test
    fun bus_and_train_are_the_only_types_that_cannot_be_booked_as_a_ride() {
        assertEquals(
            listOf("bus", "train"),
            VehicleType.entries.filterNot { it.isRideBookable }.map { it.wire }.sorted(),
        )
        assertEquals(
            VehicleType.entries.count { it.isRideBookable },
            RideVehicleType.entries.size,
        )
        RideVehicleType.entries.forEach { rideType ->
            assertEquals(rideType.wire, wireOf(rideType))
            assertEquals(rideType, RideVehicleType.from(rideType.toVehicleType()))
        }
        assertEquals(null, RideVehicleType.from(VehicleType.BUS))
        assertEquals(null, RideVehicleType.from(VehicleType.TRAIN))
    }

    @Test
    fun truck_and_mini_truck_are_the_delivery_only_ride_types() {
        assertEquals(
            listOf("mini_truck", "truck"),
            RideVehicleType.entries.filter { it.isDeliveryOnly }.map { it.wire }.sorted(),
        )
    }

    // ---- identity, roles, language -----------------------------------------------------------

    @Test
    fun role_is_the_nine_canonical_values_and_there_is_no_reseller() {
        // iam.users.role CHECK (C003), AL-06. AL-01 makes bulk credit a capability, not a role.
        assertEquals(
            listOf(
                "admin", "auditor", "driver", "finance_officer", "fleet_owner", "passenger",
                "super_admin", "support_csr", "verification_officer",
            ),
            Role.entries.map { wireOf(it) }.sorted(),
        )
        assertFalse(Role.entries.any { it.wire == "reseller" })
        assertEquals(
            listOf("driver", "fleet_owner", "passenger"),
            Role.entries.filterNot { it.isInternal }.map { it.wire }.sorted(),
        )
    }

    @Test
    fun fleet_role_matches_the_fleet_members_check() {
        assertEquals(listOf("manager", "owner", "viewer"), FleetRole.entries.map { wireOf(it) }.sorted())
    }

    @Test
    fun language_is_sinhala_tamil_english_and_falls_back_to_english() {
        // iam.users.language CHECK (C003), D-26.
        assertEquals(listOf("en", "si", "ta"), Language.entries.map { wireOf(it) }.sorted())
        assertEquals(Language.EN, Language.FALLBACK)
        assertEquals(Language.SI, Language.fromWire("si"))
        assertEquals(null, Language.fromWire("fr"))
    }

    @Test
    fun service_mode_is_the_three_operating_modes_and_only_a_and_b_take_a_session() {
        assertEquals(listOf("A", "B", "C"), ServiceMode.entries.map { wireOf(it) }.sorted())
        assertTrue(ServiceMode.A.isTrackingSessionMode)
        assertTrue(ServiceMode.B.isTrackingSessionMode)
        assertFalse(ServiceMode.C.isTrackingSessionMode, "R-01: Mode C is a ride, not a session")
    }

    // ---- documents, packages, calls ----------------------------------------------------------

    @Test
    fun document_kind_carries_revenue_license() {
        // registry.documents.kind CHECK — server_db_schema §2 omits revenue_license, D4' §2 has
        // it, and C003 took D4' because the AL-10 approval gate needs it.
        assertEquals(
            listOf("driving_license", "insurance", "permit", "registration", "revenue_license"),
            DocumentKind.entries.map { wireOf(it) }.sorted(),
        )
        assertEquals(
            listOf("EXPIRED", "EXPIRING", "REJECTED", "VALID"),
            DocumentStatus.entries.map { wireOf(it) }.sorted(),
        )
    }

    @Test
    fun package_size_is_the_three_bands_the_check_allows() {
        assertEquals(listOf("L", "M", "S"), PackageSize.entries.map { wireOf(it) }.sorted())
    }

    @Test
    fun call_type_is_free_voip_and_direct_dial_only() {
        // comms.call_log.call_type CHECK (C005). AL-48 removed `normal_masked` with the whole
        // masked PSTN bridge; it can never appear.
        assertEquals(listOf("direct_dial", "free_voip"), CallType.entries.map { wireOf(it) }.sorted())
        assertFalse(CallType.entries.any { it.wire == "normal_masked" })
    }

    @Test
    fun the_edge_and_session_enums_carry_their_lowercase_wire_forms() {
        assertEquals(listOf("android", "ios"), ClientPlatform.entries.map { wireOf(it) }.sorted())
        assertEquals(listOf("driver", "passenger"), AppSurface.entries.map { wireOf(it) }.sorted())
    }

    // ---- verification ------------------------------------------------------------------------

    @Test
    fun the_verification_enums_match_their_onboarding_checks() {
        assertEquals(
            listOf("confirmed", "pending", "rejected"),
            VerifyStatus.entries.map { wireOf(it) }.sorted(),
        )
        assertEquals(listOf("manual", "ocr"), FieldSource.entries.map { wireOf(it) }.sorted())
        assertEquals(
            listOf("accepted", "pending", "rejected"),
            AccessRequestStatus.entries.map { wireOf(it) }.sorted(),
        )
        assertEquals(
            listOf("FAILED", "PENDING", "SUCCESS"),
            ProviderCallbackStatus.entries.map { wireOf(it) }.sorted(),
        )
    }

    @Test
    fun currency_is_lkr_and_nothing_else() {
        assertEquals(listOf("LKR"), Currency.entries.map { wireOf(it) })
    }

    // ---- every enum that publishes a `wire` property agrees with its serial name --------------

    @Test
    fun every_wire_property_matches_the_serialised_form() {
        // The `wire` accessors exist so non-serialisation code (path segments, query strings,
        // SQLDelight columns in C018) can reach the same spelling. They must not drift.
        val checks: List<Pair<String, String>> = buildList {
            VehicleType.entries.forEach { add(it.wire to wireOf(it)) }
            RideVehicleType.entries.forEach { add(it.wire to wireOf(it)) }
            Role.entries.forEach { add(it.wire to wireOf(it)) }
            FleetRole.entries.forEach { add(it.wire to wireOf(it)) }
            Language.entries.forEach { add(it.wire to wireOf(it)) }
            DocumentKind.entries.forEach { add(it.wire to wireOf(it)) }
            CallType.entries.forEach { add(it.wire to wireOf(it)) }
            ClientPlatform.entries.forEach { add(it.wire to wireOf(it)) }
            AppSurface.entries.forEach { add(it.wire to wireOf(it)) }
            VerifyStatus.entries.forEach { add(it.wire to wireOf(it)) }
            FieldSource.entries.forEach { add(it.wire to wireOf(it)) }
            AccessRequestStatus.entries.forEach { add(it.wire to wireOf(it)) }
        }

        checks.forEach { (wire, serialised) -> assertEquals(wire, serialised) }
        assertTrue(checks.size > 50, "the sweep should cover every canonical enum entry")
    }
}
