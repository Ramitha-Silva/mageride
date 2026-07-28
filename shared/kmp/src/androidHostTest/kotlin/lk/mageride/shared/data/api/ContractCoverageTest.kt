package lk.mageride.shared.data.api

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

/**
 * The C013 definition of done: **every contract file has a matching typed client covering all its
 * operations.**
 *
 * A review rule nobody can run is a rule that decays, so this asserts it against the contracts
 * themselves. It runs in `androidHostTest` because that is the only source set with a filesystem
 * (the same reason C012's `ModelSourceHygieneTest` lives there), and what it checks is a property
 * of the checked-in Kotlin rather than of anything observable at runtime.
 *
 * Three properties, each with a silent failure mode this catches:
 * 1. **Coverage** — an operation added to a contract does not go quietly unimplemented.
 * 2. **Verb** — an operation is called with the method the contract declares, and a POST marked
 *    `x-idempotency-exempt` goes through `apiPostExempt` rather than being given a key the gateway
 *    does not honour for it (R-19).
 * 3. **Attestation** — every operation declaring `X-Attestation` is called with `attested = true`,
 *    and no other operation is, because a missing verdict is `401 attestation-failed` at the edge
 *    and a spurious one costs a Play Integrity round trip (D-30).
 *
 * See `ContractScanner.kt` for how the documents and the client sources are read.
 */
class ContractCoverageTest {

    private val operations: List<ContractOperation> by lazy {
        CONTRACTS.flatMap { name ->
            ContractScanner.operations(name, RepoLocator.file("backend/contracts/$name.yaml").readText())
        }
    }

    private val clients: ClientSourceIndex by lazy {
        val source = RepoLocator.dir(API_SOURCE_DIR)
            .walkTopDown()
            .filter { it.isFile && it.extension == "kt" }
            .joinToString("\n") { it.readText() }
        ClientSourceIndex(source)
    }

    @Test
    fun every_contract_operation_has_a_typed_client_function() {
        val missing = operations.filterNot { clients.covers(it.operationId) }

        assertTrue(missing.isEmpty(), "no client call for: " + missing.joinToString { it.describe() })
    }

    @Test
    fun the_scan_finds_every_operation_the_sixteen_contracts_declare() {
        // A guard against the coverage test passing vacuously: if the scanner stops recognising a
        // path or a method, this fails instead of quietly checking fewer operations.
        assertEquals(
            EXPECTED_OPERATIONS,
            operations.size,
            "expected $EXPECTED_OPERATIONS operations across ${CONTRACTS.size} contracts",
        )
    }

    @Test
    fun every_operation_is_called_with_the_method_the_contract_declares() {
        val wrong = operations.mapNotNull { operation ->
            val verb = clients.verbFor(operation.operationId) ?: return@mapNotNull null
            val expected = operation.expectedVerb()
            if (verb == expected) null else "${operation.describe()}: expected $expected, called with $verb"
        }

        assertTrue(wrong.isEmpty(), wrong.joinToString("\n"))
    }

    @Test
    fun every_attested_operation_passes_attested_true() {
        val missing = operations
            .filter { it.attested }
            .filterNot { clients.callBodyFor(it.operationId)?.contains(ATTESTED_ARGUMENT) == true }

        assertTrue(missing.isEmpty(), "missing attestation: " + missing.joinToString { it.describe() })
    }

    @Test
    fun no_operation_is_attested_that_the_contract_does_not_declare() {
        val extra = operations
            .filterNot { it.attested }
            .filter { clients.callBodyFor(it.operationId)?.contains(ATTESTED_ARGUMENT) == true }

        assertTrue(extra.isEmpty(), "attested but not declared: " + extra.joinToString { it.describe() })
    }

    @Test
    fun the_sensitive_mutations_are_the_ones_d3_names() {
        // D3' §0 lists the classes: auth, payments, ride accept, wallet, SOS. Pinning the count
        // makes adding `XAttestation` to a contract a deliberate change with a matching client
        // edit, rather than something that drifts in.
        val attested = operations.filter { it.attested }

        assertEquals(
            EXPECTED_ATTESTED,
            attested.size,
            "attested operations: " + attested.joinToString { it.operationId },
        )
    }

    private companion object {
        /** The sixteen app-facing contracts C012 modelled; the five portal ones are out of scope. */
        val CONTRACTS = listOf(
            "iam", "registry", "trip-state", "ride", "dispatch", "fare", "subscription", "wallet",
            "query", "transit", "safety", "support", "content", "voip", "notification", "version-check",
        )

        const val API_SOURCE_DIR = "shared/kmp/src/commonMain/kotlin/lk/mageride/shared/data/api"
        const val ATTESTED_ARGUMENT = "attested = true"

        /**
         * Operations across the sixteen in-scope contracts.
         *
         * 176 at C013; **179** since C022 and C023 added ride-svc's three internal commands
         * (`markRideMatching`, `placeRideOffer`, `expireRideOffer`) to `ride.yaml`. Neither
         * updated the client, so this test — the wave-1 gate — was red until C025 closed it.
         */
        const val EXPECTED_OPERATIONS = 179

        /** D3' §0's sensitive mutations: auth, payments, ride accept, wallet, SOS. */
        const val EXPECTED_ATTESTED = 20
    }
}
