package lk.mageride.shared.domain.fare

import lk.mageride.shared.data.models.Currency
import lk.mageride.shared.data.models.RideState
import lk.mageride.shared.data.models.RideVehicleType
import lk.mageride.shared.data.models.VehicleType
import lk.mageride.shared.data.models.query.TransportOption
import lk.mageride.shared.data.models.query.TransportOptionKind
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/**
 * The C016 fence: **Mode C tiers show price only before a driver is matched — no ETA, no
 * distance** (AL-19, BR-23.3, US-8.2c).
 *
 * The fence is structural — [ModeCTier] has no ETA field and no distance field — so most of what
 * is checked here is the projection that drops them and the one predicate that says when an
 * arrival time becomes legitimate.
 */
class ModeCTiersTest {

    private fun privateOption(type: VehicleType, fareMinor: Long? = 32_000, etaSeconds: Int? = 240) = TransportOption(
        kind = TransportOptionKind.PRIVATE,
        label = type.wire,
        vehicleType = type,
        etaSeconds = etaSeconds,
        estimatedFareMinor = fareMinor,
        currency = Currency.LKR,
    )

    @Test
    fun a_tier_carries_a_price_and_the_servers_eta_is_dropped() {
        // The server sends an ETA — it has to, since the same payload also lists public transport,
        // where a departure time is real. Nothing on `ModeCTier` can hold it: the type has no ETA
        // and no distance property, which is the fence. `MoneyDomainHygieneTest` (androidHostTest)
        // asserts that against the checked-in source, because common code has no reflection.
        val tiers = ModeCTiers.priceOnly(listOf(privateOption(VehicleType.MOTORBIKE, etaSeconds = 240)))

        val tier = tiers.single()
        assertEquals(RideVehicleType.MOTORBIKE, tier.vehicleType)
        assertEquals(32_000L, tier.priceMinor)
        assertEquals(32_000L, tier.money.amountMinor)
        assertEquals(Currency.LKR, tier.currency)
    }

    @Test
    fun public_transport_options_are_not_mode_c_tiers() {
        val options = listOf(
            privateOption(VehicleType.SEDAN),
            TransportOption(
                kind = TransportOptionKind.PUBLIC,
                label = "138",
                routeNumber = "138",
                etaSeconds = 600,
                estimatedFareMinor = 4_000,
                transfers = 0,
            ),
        )

        val tiers = ModeCTiers.priceOnly(options)

        assertEquals(listOf(RideVehicleType.SEDAN), tiers.map { it.vehicleType })
    }

    @Test
    fun a_bus_or_train_typed_private_option_is_dropped() {
        // AL-09: bus and train are Mode A only and have no `RideVehicleType`. They are not bookable
        // whatever a payload claims.
        val tiers = ModeCTiers.priceOnly(listOf(privateOption(VehicleType.BUS), privateOption(VehicleType.TRAIN)))

        assertTrue(tiers.isEmpty())
    }

    @Test
    fun an_unpriced_tier_is_dropped_rather_than_shown_at_zero() {
        // Real: truck / mini_truck have no seeded tariff until Finance configures one (C005).
        val tiers = ModeCTiers.priceOnly(listOf(privateOption(VehicleType.TRUCK, fareMinor = null)))

        assertTrue(tiers.isEmpty())
    }

    @Test
    fun an_arrival_time_appears_only_once_a_driver_is_assigned() {
        // AL-19: "ETA/distance are populated only after dispatch/accept."
        listOf(RideState.Requested, RideState.Matching, RideState.Offered).forEach {
            assertFalse(ModeCTiers.arrivalVisible(it), "$it has no matched driver")
        }
        listOf(RideState.Accepted, RideState.DriverArrived, RideState.InProgress).forEach {
            assertTrue(ModeCTiers.arrivalVisible(it), "$it has one")
        }
        assertFalse(ModeCTiers.arrivalVisible(null), "no ride at all is the tier board")
    }

    @Test
    fun an_offered_ride_still_shows_no_arrival_time() {
        // The interesting case: a driver has been reserved and their position is knowable, but they
        // have not accepted and may decline. Showing "3 minutes away" would promise a vehicle that
        // is still free to walk.
        assertFalse(ModeCTiers.arrivalVisible(RideState.Offered))
    }
}
