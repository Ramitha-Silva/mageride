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
 * **All three are asserted over the APP-FACING surface** — see [ContractSurface] for why that is
 * the rule rather than every route in the file, and for the ratchet that keeps the `/v1/internal`
 * and `/v1/admin` clients C013 already wrote from being deleted.
 *
 * See `ContractScanner.kt` for how the documents and the client sources are read.
 */
class ContractCoverageTest {

    private val operations: List<ContractOperation> by lazy {
        CONTRACTS.flatMap { name ->
            ContractScanner.operations(name, RepoLocator.file("backend/contracts/$name.yaml").readText())
        }
    }

    /** The half of the contracts a passenger or driver app can actually call. */
    private val appFacing: List<ContractOperation> by lazy { operations.filter(ContractOperation::appFacing) }

    private val clients: ClientSourceIndex by lazy {
        val source = RepoLocator.dir(API_SOURCE_DIR)
            .walkTopDown()
            .filter { it.isFile && it.extension == "kt" }
            .joinToString("\n") { it.readText() }
        ClientSourceIndex(source)
    }

    @Test
    fun every_app_facing_operation_has_a_typed_client_function() {
        val missing = appFacing.filterNot { clients.covers(it.operationId) }

        assertTrue(missing.isEmpty(), "no client call for: " + missing.joinToString { it.describe() })
    }

    @Test
    fun the_internal_and_admin_clients_that_exist_are_not_quietly_deleted() {
        // The ratchet half of ContractSurface's rule. `/v1/internal` and `/v1/admin` are out of
        // the coverage requirement, but the twenty-six that C013–C066 did implement are real
        // capability — `expireRideOffer` and `markRideMatching` are what the e2e harness drives
        // the ride machine with — and nothing else would notice them going.
        val covered = operations
            .filterNot(ContractOperation::appFacing)
            .filter { clients.covers(it.operationId) }
            .map { it.operationId }
            .toSortedSet()

        assertEquals(
            ContractSurface.COVERED_BEYOND_APP_SURFACE.toSortedSet(),
            covered,
            "an internal/admin client was added or removed without updating ContractSurface",
        )
    }

    @Test
    fun the_scan_finds_every_operation_the_sixteen_contracts_declare() {
        // A guard against the coverage test passing vacuously: if the scanner stops recognising a
        // path or a method, this fails instead of quietly checking fewer operations. Pinned over
        // EVERY route, app-facing or not, because what it guards is the scanner and not the rule.
        assertEquals(
            EXPECTED_OPERATIONS,
            operations.size,
            "expected $EXPECTED_OPERATIONS operations across ${CONTRACTS.size} contracts",
        )
    }

    @Test
    fun the_app_facing_surface_is_the_size_it_should_be() {
        // The same guard, one level in: a path-prefix rule that stopped matching would silently
        // move operations between the two halves and shrink what the checks below run over.
        assertEquals(
            EXPECTED_APP_FACING,
            appFacing.size,
            "app-facing operations (everything outside /v1/internal and /v1/admin)",
        )
    }

    @Test
    fun every_operation_is_called_with_the_method_the_contract_declares() {
        val wrong = appFacing.mapNotNull { operation ->
            val verb = clients.verbFor(operation.operationId) ?: return@mapNotNull null
            val expected = operation.expectedVerb()
            if (verb == expected) null else "${operation.describe()}: expected $expected, called with $verb"
        }

        assertTrue(wrong.isEmpty(), wrong.joinToString("\n"))
    }

    @Test
    fun every_attested_operation_passes_attested_true() {
        val missing = appFacing
            .filter { it.attested }
            .filterNot { clients.callBodyFor(it.operationId)?.contains(ATTESTED_ARGUMENT) == true }

        assertTrue(missing.isEmpty(), "missing attestation: " + missing.joinToString { it.describe() })
    }

    @Test
    fun no_operation_is_attested_that_the_contract_does_not_declare() {
        val extra = appFacing
            .filterNot { it.attested }
            .filter { clients.callBodyFor(it.operationId)?.contains(ATTESTED_ARGUMENT) == true }

        assertTrue(extra.isEmpty(), "attested but not declared: " + extra.joinToString { it.describe() })
    }

    @Test
    fun nothing_behind_the_app_surface_declares_attestation() {
        // D-30 is a *device* verdict — Play Integrity on a handset, App Attest on an iPhone. An
        // mTLS caller and a portal session have neither, so `X-Attestation` on an internal or
        // admin route would be a header nobody can produce. Asserting it here is what lets the
        // count below be the app-facing count without hiding one.
        val behind = operations.filterNot(ContractOperation::appFacing).filter { it.attested }

        assertTrue(behind.isEmpty(), "attested behind the app surface: " + behind.joinToString { it.describe() })
    }

    @Test
    fun the_sensitive_mutations_are_the_ones_d3_names() {
        // D3' §0 lists the classes: auth, payments, ride accept, wallet, SOS. Pinning the count
        // makes adding `XAttestation` to a contract a deliberate change with a matching client
        // edit, rather than something that drifts in.
        val attested = appFacing.filter { it.attested }

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
         * Every operation across the sixteen in-scope contracts, app-facing or not.
         *
         * 176 at C013; **179** once C022/C023 added ride-svc's three internal commands; **241**
         * today. The jump is wave 2 and wave 3 filling in the service-to-service and portal halves
         * of contracts this module only ever calls the front of — C027's `/v1/admin/rbac`, C046's
         * eight `/v1/internal/wallet` postings, C053's support queue, C060's fleet billing. See
         * [ContractSurface] for why that no longer means 62 missing clients (Δ C076a).
         *
         * **242** since MCS-05 added `GET /v1/drivers/profile` — the read SCR-DA/DI-001 needs to
         * tell "this driver has done Profile Setup" from "this person has a name in `iam.users`".
         *
         * **243** since MCS-25 added `GET /v1/drivers/{driverId}/profile-photo`, which serves the
         * bytes behind the avatar both driver headers draw. The column that read used to hand back
         * held an `s3://` pointer, which no image loader can follow.
         *
         * **245** since MCS-28 added the driver's own document list and the image behind each row —
         * the gap that let SCR-DA/DI-026 tell a driver their insurance had expired and not show it.
         */
        const val EXPECTED_OPERATIONS = 245

        /**
         * The half of those an app can reach — everything outside `/v1/internal` and `/v1/admin`.
         *
         * All 179 have a typed client today, which is the property this file exists to keep true.
         * **178** since MCS-05's `getDriverProfile`; **179** since MCS-25's `getDriverProfilePhoto`;
         * **181** since MCS-28's `listDriverDocuments` and `getDriverDocumentImage`.
         */
        const val EXPECTED_APP_FACING = 181

        /**
         * D3' §0's sensitive mutations: auth, payments, ride accept, wallet, SOS.
         *
         * 20 at C013; **23** since C046 put the passenger-wallet rails behind attestation
         * (AL-57/AL-58). The behaviour was never wrong — `every_attested_operation_passes_attested_true`
         * has passed throughout, so the clients send the header — only this pin was stale.
         */
        const val EXPECTED_ATTESTED = 23
    }
}
