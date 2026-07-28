package lk.mageride.shared.testing.contract

import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.encodeToJsonElement
import lk.mageride.shared.serialization.MageRideJson
import lk.mageride.shared.testing.fake.ApiOperations
import lk.mageride.shared.testing.fake.FakeApiBackend
import lk.mageride.shared.testing.fixture.DtoFixtures
import lk.mageride.shared.testing.scenario.RideScenarios
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

/**
 * The C019 definition of done: *"contract tests fail if a DTO drifts from its OpenAPI schema."*
 *
 * The chain each of the 176 operations is put through is:
 *
 * ```
 * the client's return type  →  its SerialDescriptor  →  a fully-populated document  →  the
 * operation's own response schema in backend/contracts/
 * ```
 *
 * Both directions of drift fall out of that one check. A field the DTO stopped sending is a
 * `required, but absent`; a field the DTO gained, or renamed, is `not declared by the schema`; a
 * type that changed is a type mismatch; an enum spelt differently is `not one of`. And because the
 * document is synthesised from the descriptor rather than typed out, there is no fixture to
 * remember to update — a DTO edited today is checked against the contract on the next build.
 *
 * This runs in `androidHostTest` rather than `commonTest` for one reason: it needs to read the
 * YAML off disk, and `commonMain` has no filesystem. The fake it validates is the same one
 * `commonTest` uses.
 */
class ContractShapeTest {

    private val api = OpenApi()
    private val routes = IN_SCOPE_CONTRACTS.flatMap { api.operations(it).values }.associateBy { it.operationId }

    @Test
    fun every_fake_response_satisfies_the_schema_its_operation_declares() {
        val backend = FakeApiBackend()
        val failures = ApiOperations.ALL.mapNotNull { operation ->
            if (!operation.hasBody) return@mapNotNull null
            val route = routes.getValue(operation.operationId)
            val schema = route.responseSchema(operation.status) ?: return@mapNotNull null
            val document = backend.defaultBodyOf(operation.operationId)
                ?: return@mapNotNull "$route: the fake served no body for a ${operation.status}"
            SchemaValidator.validate(schema, document)
                .takeIf { it.isNotEmpty() }
                ?.let { "$route\n    " + it.joinToString("\n    ") }
        }
        assertEquals(emptyList(), failures, report(failures))
    }

    @Test
    fun every_operation_with_a_json_response_is_checked_by_the_sweep_above() {
        val checked = ApiOperations.ALL
            .filter { it.hasBody }
            .count { routes.getValue(it.operationId).responseSchema(it.status) != null }

        // The only bodied operations without a JSON response schema would be ones the contract
        // declares `content`-less. There are none today, and if one appears this says so rather
        // than letting the sweep silently shrink.
        assertEquals(
            ApiOperations.ALL.count { it.hasBody },
            checked,
            "an operation whose response schema stopped resolving is an unchecked operation",
        )
    }

    @Test
    fun every_request_body_satisfies_the_schema_its_operation_declares() {
        val failures = ApiOperations.ALL.mapNotNull { operation ->
            val serializer = operation.request ?: return@mapNotNull null
            val route = routes.getValue(operation.operationId)
            val schema = route.requestSchema()
                ?: return@mapNotNull "$route: the client sends a body the contract does not declare"
            SchemaValidator.validate(schema, DtoFixtures.jsonOf(serializer.descriptor))
                .takeIf { it.isNotEmpty() }
                ?.let { "$route\n    " + it.joinToString("\n    ") }
        }
        assertEquals(emptyList(), failures, report(failures))
    }

    @Test
    fun the_canonical_scenario_bookings_satisfy_the_booking_schema() {
        val schema = requireNotNull(routes.getValue("requestRide").requestSchema())
        val failures = RideScenarios.mapNotNull { scenario ->
            val document = MageRideJson.encodeToJsonElement(scenario.request)
            SchemaValidator.validate(schema, document)
                .takeIf { it.isNotEmpty() }
                ?.let { "${scenario.name}\n    " + it.joinToString("\n    ") }
        }
        assertEquals(emptyList(), failures, report(failures))
    }

    @Test
    fun the_validator_notices_a_dto_that_has_drifted() {
        val route = routes.getValue("getRide")
        val schema = requireNotNull(route.responseSchema(HTTP_OK))
        val good = requireNotNull(FakeApiBackend().defaultBodyOf("getRide"))

        assertTrue(SchemaValidator.validate(schema, good).isEmpty(), "the fixture must start clean")

        val renamed = JsonEdits.rename(good, from = "rideId", to = "rideID")
        val errors = SchemaValidator.validate(schema, renamed)

        assertTrue(errors.any { it.contains("rideId") && it.contains("required") }, "$errors")
        assertTrue(errors.any { it.contains("rideID") && it.contains("not declared") }, "$errors")
    }

    @Test
    fun the_validator_notices_an_enum_value_that_has_drifted() {
        val route = routes.getValue("getRide")
        val schema = requireNotNull(route.responseSchema(HTTP_OK))
        val good = requireNotNull(FakeApiBackend().defaultBodyOf("getRide"))

        val misspelt = JsonEdits.replace(good, "state", "Requsted")
        val errors = SchemaValidator.validate(schema, misspelt)

        assertTrue(errors.any { it.contains("Requsted") && it.contains("not one of") }, "$errors")
    }

    private fun report(failures: List<String>): String =
        if (failures.isEmpty()) "" else "${failures.size} operation(s) drifted:\n\n" + failures.joinToString("\n\n")

    /** Edits to a synthesised document, so "the check would have caught it" is itself a test. */
    private object JsonEdits {

        fun rename(document: JsonObject, from: String, to: String): JsonObject =
            JsonObject(document.filterKeys { it != from } + (to to document.getValue(from)))

        fun replace(document: JsonObject, key: String, value: String): JsonObject =
            JsonObject(document + (key to JsonPrimitive(value)))
    }

    private companion object {
        const val HTTP_OK = 200
    }
}
