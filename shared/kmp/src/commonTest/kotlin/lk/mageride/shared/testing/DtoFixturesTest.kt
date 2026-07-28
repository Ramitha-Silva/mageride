package lk.mageride.shared.testing

import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import lk.mageride.shared.data.models.Money
import lk.mageride.shared.data.models.Page
import lk.mageride.shared.data.models.PositionSample
import lk.mageride.shared.data.models.ride.RideDetail
import lk.mageride.shared.data.models.ride.RideHistoryRow
import lk.mageride.shared.data.models.wallet.Wallet
import lk.mageride.shared.serialization.MageRideJson
import lk.mageride.shared.testing.fixture.DtoFixtures
import lk.mageride.shared.testing.fixture.Fixtures
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertNotNull
import kotlin.test.assertTrue

/**
 * The descriptor-driven fixture builder.
 *
 * What is being asserted is that it fills in **everything** — the optional fields and the nullable
 * ones too. A fixture that quietly omitted a nullable field would still decode, and would still
 * look right in a screenshot; what it would not do is catch the DTO drifting away from its schema,
 * because a field that is never serialised is a field the contract checks never see.
 */
class DtoFixturesTest {

    @Test
    fun every_field_of_a_dto_is_populated_including_the_optional_ones() {
        val document = DtoFixtures.jsonOf<RideDetail>().jsonObject

        // RideDetail has twenty properties and only six of them are required. All twenty.
        assertEquals(20, document.size, document.keys.sorted().toString())
        assertTrue("packageDescription" in document, "an optional field must be populated too")
        assertTrue("counterpartyPhone" in document, "a nullable field must be populated too")
    }

    @Test
    fun a_synthesised_document_decodes_into_its_own_dto() {
        val detail: RideDetail = DtoFixtures.of()

        assertEquals(Fixtures.RIDE_ID, detail.rideId)
        assertNotNull(detail.driver)
        assertNotNull(detail.fare)
        assertEquals(Fixtures.NOW, detail.createdAt)
    }

    @Test
    fun the_values_satisfy_the_patterns_the_contracts_declare() {
        val document = DtoFixtures.jsonOf<RideDetail>().jsonObject

        assertEquals(Fixtures.PASSENGER_PHONE, document["counterpartyPhone"]?.jsonPrimitive?.content)
        assertTrue(
            document["rideId"]?.jsonPrimitive?.content.orEmpty().length == ULID_LENGTH,
            "a ULID field must be a ULID, not the string \"rideId\"",
        )
    }

    @Test
    fun money_is_minor_units_and_the_currency_is_the_contracts_const() {
        val money: Money = DtoFixtures.of()

        assertEquals(Fixtures.FARE.amountMinor, money.amountMinor)
        assertEquals(
            Fixtures.FARE.currency,
            money.currency,
            "an enum fixture takes the first entry, and LKR is the only one",
        )
    }

    @Test
    fun a_generic_envelope_carries_one_row_of_its_element_type() {
        val page: Page<RideHistoryRow> = DtoFixtures.of()

        assertEquals(1, page.items.size)
        assertEquals(Fixtures.RIDE_ID, page.items.single().rideId)
    }

    @Test
    fun an_override_replaces_a_field_and_an_unknown_name_is_refused() {
        val wallet: Wallet = DtoFixtures.of("balanceMinor" to JsonPrimitive(0L))

        assertEquals(0L, wallet.balanceMinor)
        assertFailsWith<IllegalArgumentException> {
            DtoFixtures.of<Wallet>("balance" to JsonPrimitive(0L))
        }
    }

    @Test
    fun a_fixture_survives_the_round_trip_the_platform_actually_uses() {
        val sample: PositionSample = DtoFixtures.of()
        val encoded = MageRideJson.encodeToString(sample)

        assertEquals(sample, MageRideJson.decodeFromString<PositionSample>(encoded), encoded)
    }

    @Test
    fun two_synthesises_of_the_same_type_are_identical() {
        assertEquals(
            DtoFixtures.jsonOf<RideDetail>(),
            DtoFixtures.jsonOf<RideDetail>(),
            "nothing here may be random: a fixture that changes between runs makes a golden " +
                "assertion impossible and a screenshot diff meaningless",
        )
    }

    private companion object {
        const val ULID_LENGTH = 26
    }
}
