package lk.mageride.shared.testing.contract

import lk.mageride.shared.data.models.AccessRequestStatus
import lk.mageride.shared.data.models.FieldSource
import lk.mageride.shared.data.models.FleetRole
import lk.mageride.shared.data.models.RideVehicleType
import lk.mageride.shared.data.models.Role
import lk.mageride.shared.data.models.VehicleType
import lk.mageride.shared.data.models.VerifyStatus
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue
import kotlin.test.fail

/**
 * **Δ MCS-15 — every wire enum has exactly the members its contract declares.**
 *
 * ## Why this exists
 *
 * The same defect has now reached a driver's handset twice, from one file:
 *
 * | | the enum said | the wire says | found by |
 * |---|---|---|---|
 * | MCS-02 | `FieldSource.OCR` | `ai` | a driver |
 * | MCS-15 | no `auto_verified` | `auto_verified` | a driver |
 * | MCS-15 | `VerifyStatus.REJECTED` | nothing emits it | this test |
 *
 * These are non-nullable enum properties on a response body, so one unknown member does not degrade
 * one field — `kotlinx.serialization` throws and the WHOLE response is rejected. On SCR-DA/DI-003a
 * that reached the driver as *"The app could not read the reply from the server"* after an HTTP 200
 * carrying precisely the values they were waiting to see.
 *
 * ## Why the two guards that already existed could not catch it
 *
 * `EnumWireFormatTest` (commonTest) asserts each enum against a list **hand-copied into the test**,
 * written from the same understanding that produced the enum. It can only detect an enum drifting
 * from itself, and it was green for the entire life of the defect.
 *
 * [ContractShapeTest] validates that the synthesised FAKE responses satisfy their schema. The fakes
 * are built from the client's own DTOs, so they only ever contain members the client already knows.
 * A value the client is MISSING cannot appear in a fake, and so cannot fail that test. It catches
 * an enum with an EXTRA member; it is structurally blind to one that is short.
 *
 * Both compare the client with itself. This one compares it with `backend/contracts`, which is what
 * the services actually answer with — the root CLAUDE.md's "specs are the single source of truth",
 * enforced rather than asserted.
 *
 * ## Adding an enum
 *
 * If a new `@Serializable` enum decodes a contract field, add it to [checked]. Equality is the
 * assertion, not "contains": a member set that is a strict subset is exactly the failure that
 * reaches production, and a superset is a member nothing can ever send.
 */
class EnumMembersMatchTheContractTest {

    /**
     * Kotlin enum → (contract document, `components/schemas` name it decodes).
     *
     * Deliberately explicit rather than derived from the class name. [VehicleType] and
     * [RideVehicleType] are different schemas with overlapping members, and a convention that
     * guessed would pair one with the other and still be green — which is the failure mode this
     * test exists to end.
     *
     * **Not covered, because no contract declares them as named schemas:** `DocumentKind` and
     * `Language`. Both are decoded from wire fields and neither has a `components/schemas` entry to
     * compare against, so they carry exactly the risk this test was written for and there is
     * nothing here to assert against. That is a gap in the contracts rather than in this test, and
     * it is worth a micro-change-set.
     */
    private val checked: List<Checked> = listOf(
        Checked("VerifyStatus", SHARED, "VerifyStatus", VerifyStatus.entries.map { it.wire }),
        Checked("FieldSource", SHARED, "FieldSource", FieldSource.entries.map { it.wire }),
        Checked("VehicleType", SHARED, "VehicleType", VehicleType.entries.map { it.wire }),
        Checked("RideVehicleType", SHARED, "RideVehicleType", RideVehicleType.entries.map { it.wire }),
        Checked("Role", SHARED, "Role", Role.entries.map { it.wire }),
        Checked("FleetRole", SHARED, "FleetRole", FleetRole.entries.map { it.wire }),
        Checked(
            kotlinName = "AccessRequestStatus",
            contract = "subscription",
            schema = "AccessRequestStatus",
            members = AccessRequestStatus.entries.map { it.wire },
        ),
    )

    @Test
    fun every_checked_enum_has_exactly_the_members_its_contract_declares() {
        val contracts = OpenApi()
        val failures = mutableListOf<String>()

        checked.forEach { entry ->
            val declared = entry.declaredIn(contracts)

            if (declared == null) {
                failures += "${entry.kotlinName}: ${entry.contract} has no enum at " +
                    "components/schemas/${entry.schema}"
                return@forEach
            }

            // MISSING is the one that reaches a driver: the server emits it, the client has no
            // member for it, and the whole body fails to deserialise.
            val missing = declared - entry.members.toSet()
            if (missing.isNotEmpty()) {
                failures += "${entry.kotlinName} is MISSING $missing — the contract declares them, " +
                    "so a response carrying one fails to deserialise ENTIRELY, not partially"
            }

            // INVENTED is dead weight rather than a crash, and it is how `rejected` survived so
            // long: a member nothing sends and nothing reads, which makes the enum look considered.
            val invented = entry.members - declared.toSet()
            if (invented.isNotEmpty()) {
                failures += "${entry.kotlinName} declares $invented, which its contract does not — " +
                    "nothing can ever send them"
            }
        }

        if (failures.isNotEmpty()) {
            fail("wire enums have drifted from the contracts:\n" + failures.joinToString("\n") { "  - $it" })
        }
    }

    /**
     * The regression itself, pinned separately so a failure names the defect rather than the sweep.
     *
     * `auto_verified` is what registry-svc writes for every field extracted at or above
     * `Registry:OcrConfidenceThreshold` — the COMMON verdict on a working deployment, which is why
     * its absence stayed invisible for exactly as long as extraction stayed broken.
     */
    @Test
    fun verify_status_carries_auto_verified() {
        assertTrue(
            VerifyStatus.entries.any { it.wire == "auto_verified" },
            "VerifyStatus must carry auto_verified — it is the verdict a clean scan produces",
        )
        assertEquals(listOf("auto_verified", "confirmed", "pending"), VerifyStatus.entries.map { it.wire }.sorted())
    }

    private data class Checked(
        val kotlinName: String,
        val contract: String,
        val schema: String,
        val members: List<String>,
    ) {
        /** The enum as the contract declares it, or `null` when that schema carries none. */
        fun declaredIn(contracts: OpenApi): List<String>? =
            runCatching { contracts.resolve(contract, "#/components/schemas/$schema") }
                .getOrNull()
                ?.enum
                ?.map { it.toString() }
    }

    private companion object {
        const val SHARED = "_shared"
    }
}
