package lk.mageride.shared.testing.contract

import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive

/**
 * Checks a JSON document against an OpenAPI schema, and reports **every** disagreement.
 *
 * Scoped to what makes a *client* wrong. A DTO drifts from its contract in four ways and this
 * catches all four:
 *
 * - it stops sending a field the schema requires (the field is missing);
 * - it sends a field the schema does not declare (a rename, or a field invented client-side);
 * - it sends the wrong JSON type for one (a `Long` where the contract says string, the classic
 *   money-as-a-number bug);
 * - it spells an enum value differently from the contract — which is the drift most likely to
 *   reach production, because it compiles, serialises and only fails against a real server.
 *
 * **Undeclared properties are errors here, which is stricter than OpenAPI itself.** A schema
 * without `additionalProperties: false` technically permits anything; but the documents being
 * validated are generated *from the DTOs*, so an undeclared property means the DTO has a field the
 * contract does not — exactly the drift these checks exist to find. A schema that genuinely is
 * open says so with `additionalProperties`, and that is honoured.
 *
 * Deliberately **not** checked: `pattern`, `minLength`/`maxLength`, `minimum`/`maximum` and
 * `format`. Those constrain values a *server* must reject, not shapes a client must match; a
 * fixture that violated one would be a fixture worth improving, not a contract violation. The
 * fixture values satisfy them anyway — see `FixtureValues`.
 */
// A tree walk answers "this branch is settled" by returning, and rewriting each of these as a
// single expression would put the interesting case at the bottom of a `when` chain. The early
// returns ARE the structure here.
@Suppress("ReturnCount")
internal object SchemaValidator {

    /** Every way [value] disagrees with [schema]. Empty means it agrees. */
    fun validate(schema: Schema, value: JsonElement, path: String = "$"): List<String> {
        val resolved = schema.deref()

        resolved.list("oneOf").takeIf { it.isNotEmpty() }?.let { return alternatives(it, value, path) }
        resolved.list("anyOf").takeIf { it.isNotEmpty() }?.let { return alternatives(it, value, path) }

        val shape = objectShape(resolved)
        if (value is JsonNull) {
            return if (resolved.nullable || (resolved.type == null && shape == null)) {
                emptyList()
            } else {
                listOf("$path: null is not allowed")
            }
        }

        enumError(resolved, value, path)?.let { return listOf(it) }
        if (shape != null) return obj(shape, value, path)

        // An untyped schema with no properties constrains nothing — a `description`-only node, or
        // a `$ref` whose target this walk has already unwrapped.
        val type = resolved.type ?: return emptyList()
        return if (type == "array") array(resolved, value, path) else primitive(type, value, path)
    }

    /**
     * A `oneOf`/`anyOf` passes when any branch does.
     *
     * Reporting every branch's failures on a miss is deliberate: the commonest shape in these
     * contracts is `oneOf: [Timestamp, {type: 'null'}]`, and "expected string, got an object" from
     * the first branch is far more useful than "matched no branch".
     */
    private fun alternatives(branches: List<Schema>, value: JsonElement, path: String): List<String> {
        val attempts = branches.map { validate(it, value, path) }
        if (attempts.any { it.isEmpty() }) return emptyList()
        return listOf("$path: matched none of ${branches.size} alternatives — ${attempts.flatten()}")
    }

    /**
     * The object this schema describes, `allOf` branches folded in — or `null` if it is not one.
     *
     * The contracts use `allOf` for two things: mixing a shared envelope into a response
     * (`allOf: [CursorPage, { items: [...] }]`) and hanging a description off a `$ref` without
     * altering it. Both are the union of properties and of `required`, which is exactly what the
     * Kotlin side does — kotlinx.serialization cannot compose, so C012 flattens each `allOf` into
     * one data class. Folding here is what keeps the two readings the same.
     *
     * Each property keeps the [Schema] it came from, so a `$ref` inside a branch of `ride.yaml`
     * still resolves against `ride.yaml` after being merged with one from `_shared.yaml`.
     */
    private fun objectShape(schema: Schema): ObjectShape? {
        val parts = schema.list("allOf").map { it.deref() }.ifEmpty { listOf(schema) }
        val isObject = parts.any { it.type == "object" || it.node["properties"] != null }
        if (!isObject) return null

        val properties = LinkedHashMap<String, Schema>()
        val required = LinkedHashSet<String>()
        var extras = false
        parts.forEach { part ->
            // Only recurse when the part *is* another allOf; calling objectShape unconditionally
            // would re-enter with the same schema, since a part with no allOf folds to itself.
            val nested = if (part.list("allOf").isNotEmpty()) objectShape(part) else null
            if (nested != null) {
                properties += nested.properties
                required += nested.required
                extras = extras || nested.allowsExtras
            } else {
                properties += part.properties()
                required += part.required
                extras = extras || part.allowsExtras()
            }
        }
        return ObjectShape(properties, required, extras, parts.any { it.node["properties"] != null })
    }

    private class ObjectShape(
        val properties: Map<String, Schema>,
        val required: Set<String>,
        val allowsExtras: Boolean,
        val declaresProperties: Boolean,
    )

    private fun enumError(schema: Schema, value: JsonElement, path: String): String? {
        val allowed = schema.enum ?: return null
        val actual = (value as? JsonPrimitive)?.content ?: return "$path: expected one of $allowed, got $value"
        return if (allowed.any { it.toString() == actual }) null else "$path: '$actual' is not one of $allowed"
    }

    private fun obj(shape: ObjectShape, value: JsonElement, path: String): List<String> {
        val document = value as? JsonObject ?: return listOf("$path: expected an object, got ${kindOf(value)}")
        // `type: object` with no properties at all is a free-form map — a translation bundle, a
        // provider payload. Nothing to check but the fact that it is an object.
        if (!shape.declaresProperties) return emptyList()

        val errors = mutableListOf<String>()
        shape.required.filterNot { it in document }.forEach { errors += "$path.$it: required, but absent" }
        if (!shape.allowsExtras) {
            document.keys
                .filterNot { it in shape.properties }
                .forEach { errors += "$path.$it: not declared by the schema" }
        }
        shape.properties.forEach { (name, property) ->
            document[name]?.let { errors += validate(property, it, "$path.$name") }
        }
        return errors
    }

    private fun array(schema: Schema, value: JsonElement, path: String): List<String> {
        val items = value as? JsonArray ?: return listOf("$path: expected an array, got ${kindOf(value)}")
        val element = schema.at("items") ?: return emptyList()
        return items.flatMapIndexed { index, entry -> validate(element, entry, "$path[$index]") }
    }

    private fun primitive(type: String, value: JsonElement, path: String): List<String> {
        val primitive = value as? JsonPrimitive ?: return listOf("$path: expected $type, got ${kindOf(value)}")
        val matches = when (type) {
            "string" -> primitive.isString
            "boolean" -> !primitive.isString && primitive.content.toBooleanStrictOrNull() != null
            "integer" -> !primitive.isString && primitive.content.toLongOrNull() != null
            "number" -> !primitive.isString && primitive.content.toDoubleOrNull() != null
            else -> true
        }
        return if (matches) emptyList() else listOf("$path: expected $type, got ${kindOf(value)} ($primitive)")
    }

    private fun kindOf(value: JsonElement): String = when (value) {
        is JsonObject -> "an object"
        is JsonArray -> "an array"
        JsonNull -> "null"
        is JsonPrimitive -> if (value.isString) "a string" else "a number or boolean"
    }
}
