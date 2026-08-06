package lk.mageride.shared.testing.contract

import lk.mageride.shared.data.api.ContractSurface
import lk.mageride.shared.testing.fake.ApiOperations
import kotlin.test.Test
import kotlin.test.assertEquals

/**
 * `ApiOperations` against the YAML it claims to mirror.
 *
 * The table is hand-maintained Kotlin — it has to be, because the fake needs a *compile-time*
 * serializer per operation and `commonTest` cannot read a file. So the risk it carries is the
 * ordinary one for any transcribed table: the contract moves and the copy does not. This is what
 * makes that a build failure.
 *
 * It is the same argument C013's `ContractCoverageTest` makes about the clients, one layer up: an
 * operation added to a contract and not to this table is an operation the fake cannot answer, and
 * the first thing to notice would otherwise be a screen that mysteriously gets a `404`.
 *
 * **Scoped to the same surface that test is** (Δ C076a): the fake exists to answer the calls the
 * apps make, so a `/v1/internal` posting no Kotlin client calls has nothing to fake. See
 * [ContractSurface] — the internal and admin operations that *do* have clients are in the table
 * exactly because they have them.
 */
class ApiOperationTableTest {

    private val api = OpenApi()
    private val declared = IN_SCOPE_CONTRACTS
        .flatMap { api.operations(it).values }
        .associateBy { it.operationId }

    /** What the table must hold: the app-facing surface, plus the clients that reach behind it. */
    private val expected = declared
        .filterValues {
            ContractSurface.isAppFacing(it.path) ||
                it.operationId in ContractSurface.COVERED_BEYOND_APP_SURFACE
        }
        .keys

    @Test
    fun the_table_lists_exactly_the_operations_the_in_scope_contracts_declare() {
        val tabled = ApiOperations.ALL.map { it.operationId }.toSortedSet()
        assertEquals(expected.toSortedSet(), tabled)
    }

    @Test
    fun every_row_carries_the_verb_and_path_its_contract_declares() {
        val drift = ApiOperations.ALL.mapNotNull { row ->
            val route = declared.getValue(row.operationId)
            val expected = "${route.contract} ${route.method} ${route.path}"
            val actual = "${row.service.id} ${row.method} ${row.path}"
            "$expected != $actual".takeIf { expected != actual }
        }
        assertEquals(emptyList(), drift, "a route in the table must be the route in the YAML")
    }

    @Test
    fun every_row_carries_the_success_status_its_contract_declares() {
        val drift = ApiOperations.ALL.mapNotNull { row ->
            val expected = declared.getValue(row.operationId).successStatus()
            "$row: contract says $expected, table says ${row.status}".takeIf { expected != row.status }
        }
        assertEquals(emptyList(), drift)
    }

    @Test
    fun a_row_carries_a_response_body_exactly_when_its_contract_declares_one() {
        val drift = ApiOperations.ALL.mapNotNull { row ->
            val declaresBody = declared.getValue(row.operationId).responseSchema(row.status) != null
            "$row: contract body=$declaresBody, table body=${row.hasBody}".takeIf { declaresBody != row.hasBody }
        }
        assertEquals(emptyList(), drift)
    }

    @Test
    fun a_row_carries_a_request_body_exactly_when_its_contract_declares_one() {
        val drift = ApiOperations.ALL.mapNotNull { row ->
            val declaresBody = declared.getValue(row.operationId).requestSchema() != null
            val sendsBody = row.request != null
            "$row: contract body=$declaresBody, client sends=$sendsBody".takeIf { declaresBody != sendsBody }
        }
        assertEquals(emptyList(), drift, "a body the contract declares and the client never sends is dead weight")
    }
}
