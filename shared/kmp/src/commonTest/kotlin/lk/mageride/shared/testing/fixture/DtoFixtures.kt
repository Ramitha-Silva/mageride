package lk.mageride.shared.testing.fixture

import kotlinx.serialization.ExperimentalSerializationApi
import kotlinx.serialization.KSerializer
import kotlinx.serialization.descriptors.PrimitiveKind
import kotlinx.serialization.descriptors.SerialDescriptor
import kotlinx.serialization.descriptors.SerialKind
import kotlinx.serialization.descriptors.StructureKind
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.serializer
import lk.mageride.shared.serialization.MageRideJson

/**
 * A fixture for **every** DTO in `data/models`, derived from the type rather than typed out.
 *
 * C019's deliverable asks for "fixture builders for every DTO". Two hundred and eighty-nine
 * hand-written builders would be two hundred and eighty-nine things to forget when a contract
 * changes; what this does instead is walk the `SerialDescriptor` kotlinx.serialization already
 * generates and fill in **every** field — required and optional, nested and repeated. So:
 *
 * - a field added to a DTO appears in its fixture the moment it compiles, with no edit here;
 * - a fixture is never *partially* populated, which is what makes it usable as the fake's
 *   response body and as the input to the contract checks ([ContractShapeTest] validates these
 *   exact documents against `backend/contracts/`);
 * - the values are chosen from the field's *name*, so `pickupOtp` is four digits, `driverPhone`
 *   is `+947…` and `createdAt` is an ISO instant. They satisfy the contracts' patterns because
 *   a fixture that does not is a fixture nobody can paste into a curl.
 *
 * What it is **not** is a scenario. Every field being populated makes a shape, not a story:
 * `RideDetail` comes back `Requested` with a driver attached, which no real ride ever is. When
 * the *meaning* matters, build the DTO by hand — see `testing/scenario`.
 *
 * ```kotlin
 * val detail: RideDetail = DtoFixtures.of()                       // every field populated
 * val json: JsonElement = DtoFixtures.jsonOf<RideDetail>()        // the same, as a document
 * ```
 */
@OptIn(ExperimentalSerializationApi::class)
public object DtoFixtures {

    /** A fully-populated instance of [T]. */
    public inline fun <reified T> of(): T = of(serializer<T>())

    /** A fully-populated instance for [serializer], when the type is only known dynamically. */
    public fun <T> of(serializer: KSerializer<T>): T =
        MageRideJson.decodeFromJsonElement(serializer, jsonOf(serializer.descriptor))

    /** The wire document [of] would decode — the shape a fake serves and a contract check reads. */
    public inline fun <reified T> jsonOf(): JsonElement = jsonOf(serializer<T>().descriptor)

    /** The wire document for [descriptor]. */
    public fun jsonOf(descriptor: SerialDescriptor): JsonElement = build(descriptor, field = "", path = emptyList())

    /**
     * [jsonOf] with named top-level fields replaced.
     *
     * For the common case of "the canonical shape, but this one field is the value under test".
     * Unknown names are a programming error and throw — a silently ignored override is a test
     * that asserts nothing.
     */
    public inline fun <reified T> jsonOf(vararg overrides: Pair<String, JsonElement>): JsonElement =
        patch(jsonOf<T>(), overrides.toMap())

    /** [of] with named top-level fields replaced. */
    public inline fun <reified T> of(vararg overrides: Pair<String, JsonElement>): T =
        MageRideJson.decodeFromJsonElement(serializer<T>(), jsonOf<T>(*overrides))

    /** Replaces top-level keys of a synthesised object. Public so the inline builders above can. */
    public fun patch(document: JsonElement, overrides: Map<String, JsonElement>): JsonElement {
        val obj = document as? JsonObject ?: error("only an object fixture can be patched, got $document")
        val unknown = overrides.keys - obj.keys
        require(unknown.isEmpty()) { "no such field(s) on this DTO: ${unknown.sorted()}" }
        return JsonObject(obj + overrides)
    }

    // ------------------------------------------------------------------------------------------

    private fun build(descriptor: SerialDescriptor, field: String, path: List<String>): JsonElement = when {
        descriptor.serialName.removeSuffix("?").startsWith(JSON_TREE) -> freeForm(descriptor)

        descriptor.isInline -> build(descriptor.getElementDescriptor(0), field, path)

        descriptor.kind is PrimitiveKind -> primitive(descriptor, field)

        descriptor.kind == SerialKind.ENUM -> JsonPrimitive(descriptor.getElementName(0))

        descriptor.kind == StructureKind.LIST -> JsonArray(
            listOf(build(descriptor.getElementDescriptor(0), field, path)),
        )

        descriptor.kind == StructureKind.MAP -> map(descriptor, field, path)

        descriptor.kind == StructureKind.OBJECT -> JsonObject(emptyMap())

        descriptor.kind == StructureKind.CLASS -> obj(descriptor, path)

        else -> error(
            "no fixture rule for ${descriptor.serialName} (${descriptor.kind}) — contextual and " +
                "polymorphic serializers are not used in data/models. Teach DtoFixtures about it " +
                "rather than hand-writing a fixture around it.",
        )
    }

    private fun obj(descriptor: SerialDescriptor, path: List<String>): JsonObject {
        val entries = LinkedHashMap<String, JsonElement>(descriptor.elementsCount)
        val here = path + descriptor.serialName
        for (index in 0 until descriptor.elementsCount) {
            val child = descriptor.getElementDescriptor(index)
            // A self-referential DTO would recurse forever. None exists today; if one is added,
            // the cycle is broken at the first repeat and the field is simply left out, which a
            // contract check will report as a missing required field rather than a stack overflow.
            if (child.serialName in here) continue
            entries[descriptor.getElementName(index)] = build(child, descriptor.getElementName(index), here)
        }
        return JsonObject(entries)
    }

    private fun map(descriptor: SerialDescriptor, field: String, path: List<String>): JsonObject {
        val key = build(descriptor.getElementDescriptor(0), field, path)
        val value = build(descriptor.getElementDescriptor(1), field, path)
        return JsonObject(mapOf(JsonPrimitive(key.toString().trim('"')).content to value))
    }

    private fun primitive(descriptor: SerialDescriptor, field: String): JsonPrimitive =
        when (descriptor.kind as PrimitiveKind) {
            PrimitiveKind.BOOLEAN -> JsonPrimitive(true)
            PrimitiveKind.BYTE, PrimitiveKind.SHORT, PrimitiveKind.INT -> JsonPrimitive(FixtureValues.int(field))
            PrimitiveKind.LONG -> JsonPrimitive(FixtureValues.long(field))
            PrimitiveKind.FLOAT, PrimitiveKind.DOUBLE -> JsonPrimitive(FixtureValues.double(field))
            PrimitiveKind.CHAR -> JsonPrimitive("A")
            PrimitiveKind.STRING -> JsonPrimitive(FixtureValues.string(descriptor.serialName, field))
        }

    /**
     * A field typed as raw JSON — a provider callback's `raw`, a push notification's `data`.
     *
     * Left **empty**. These are free-form by contract (`type: object` with no `properties`), so
     * there is no shape to synthesise; inventing members would put keys in a fixture that no
     * schema justifies and that no screen reads.
     */
    private fun freeForm(descriptor: SerialDescriptor): JsonElement = when (descriptor.serialName.removeSuffix("?")) {
        "$JSON_TREE.JsonArray" -> JsonArray(emptyList())
        "$JSON_TREE.JsonPrimitive" -> JsonPrimitive("fixture")
        else -> JsonObject(emptyMap())
    }

    private const val JSON_TREE = "kotlinx.serialization.json"
}
