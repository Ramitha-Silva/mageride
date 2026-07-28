package lk.mageride.shared.testing.contract

import lk.mageride.shared.data.api.RepoLocator
import org.yaml.snakeyaml.Yaml

// The OpenAPI half of the C019 contract checks: read `backend/contracts/*.yaml` properly enough to
// answer "what shape does this operation's response have", `$ref`s and `allOf`s included.
//
// C013's ContractScanner deliberately does not parse — it only has to find operation ids, and a
// line scan is the right tool for that. This does have to parse: a schema is a tree with
// cross-file references, and "the DTO agrees with the contract" is a statement about the tree.

/**
 * One `$ref`-resolvable schema node, and the document it was written in.
 *
 * The owning document matters because a `$ref` is relative to it: `'#/components/schemas/RideKind'`
 * inside `ride.yaml` and inside `dispatch.yaml` name different things, and a schema pulled out of
 * `_shared.yaml` resolves its own refs against `_shared.yaml`.
 */
internal class Schema(val contract: String, val node: Map<*, *>, private val catalog: OpenApi) {

    /** Follows a `$ref` chain to the schema it names. */
    fun deref(): Schema {
        val ref = node["\$ref"] as String? ?: return this
        return catalog.resolve(contract, ref).deref()
    }

    /** A child node, kept in this schema's document so its own refs still resolve. */
    fun at(key: String): Schema? = (node[key] as? Map<*, *>)?.let { Schema(contract, it, catalog) }

    /** A list-valued keyword's entries — `allOf`, `oneOf`, `anyOf`. */
    fun list(key: String): List<Schema> =
        (node[key] as? List<*>).orEmpty().filterIsInstance<Map<*, *>>().map { Schema(contract, it, catalog) }

    val type: String? get() = node["type"] as? String
    val enum: List<Any?>? get() = node["enum"] as? List<*>
    val required: List<String> get() = (node["required"] as? List<*>).orEmpty().map { it.toString() }
    val nullable: Boolean get() = node["nullable"] == true || type == "null"

    /** Declared properties, with each value kept in this document. */
    fun properties(): Map<String, Schema> = (node["properties"] as? Map<*, *>).orEmpty()
        .entries
        .filter { it.value is Map<*, *> }
        .associate { it.key.toString() to Schema(contract, it.value as Map<*, *>, catalog) }

    /**
     * Whether a member the schema does not declare is allowed.
     *
     * OpenAPI's own answer is "yes, unless `additionalProperties: false`". The contract checks
     * take the opposite default on purpose — see [SchemaValidator].
     */
    fun allowsExtras(): Boolean = node["additionalProperties"].let { it != null && it != false }

    override fun toString(): String = "$contract:${node.keys.joinToString(",")}"
}

/** The `backend/contracts` directory, loaded once and resolvable across files. */
internal class OpenApi {

    private val directory = RepoLocator.dir("backend/contracts")
    private val documents = mutableMapOf<String, Map<*, *>>()

    /** A whole document, by its file name without the extension. */
    fun document(contract: String): Map<*, *> = documents.getOrPut(contract) {
        Yaml().load<Map<*, *>>(directory.resolve("$contract.yaml").readText())
    }

    /**
     * Resolves a `$ref` written inside [contract].
     *
     * Two forms appear: `'#/components/schemas/X'` (same file) and
     * `'./_shared.yaml#/components/schemas/X'` (the shared component library). There are no remote
     * refs and no refs into a document that is not in this directory.
     */
    fun resolve(contract: String, ref: String): Schema {
        val file = ref.substringBefore("#").removePrefix("./").removeSuffix(".yaml").ifEmpty { contract }
        val pointer = ref.substringAfter("#").trim('/').split("/")
        var node: Any? = document(file)
        for (part in pointer) {
            node = (node as? Map<*, *>)?.get(part)
                ?: error("$contract: cannot resolve '$ref' — nothing at /$part")
        }
        return Schema(file, node as Map<*, *>, this)
    }

    /** Every operation in [contract], indexed by `operationId`. */
    fun operations(contract: String): Map<String, ContractRoute> {
        val paths = document(contract)["paths"] as? Map<*, *> ?: return emptyMap()
        val found = mutableMapOf<String, ContractRoute>()
        paths.forEach { (path, item) ->
            (item as? Map<*, *>)?.forEach { (method, operation) ->
                val body = operation as? Map<*, *> ?: return@forEach
                val id = body["operationId"] as? String ?: return@forEach
                found[id] = ContractRoute(
                    operationId = id,
                    contract = contract,
                    method = method.toString().uppercase(),
                    path = path.toString(),
                    operation = Schema(contract, body, this),
                )
            }
        }
        return found
    }
}

/**
 * One operation as the contract declares it.
 *
 * @property operation The whole operation object, so a caller can reach `responses`, `requestBody`
 *   or an `x-` extension without this class having to grow an accessor for each.
 */
internal class ContractRoute(
    val operationId: String,
    val contract: String,
    val method: String,
    val path: String,
    val operation: Schema,
) {
    /** Every status this operation declares, as strings — `'200'`, `'409'`, `'default'`. */
    fun statuses(): List<String> = (operation.node["responses"] as? Map<*, *>).orEmpty().keys.map { it.toString() }

    /** The success status the contract declares — the lowest `2xx`, or `302` for a redirect. */
    fun successStatus(): Int? = statuses()
        .mapNotNull { it.toIntOrNull() }
        .filter { it in SUCCESS_RANGE }
        .minOrNull()

    /** The `application/json` schema of [status]'s body, or `null` when it declares none. */
    fun responseSchema(status: Int): Schema? = operation.at("responses")
        ?.at(status.toString())
        ?.at("content")
        ?.at(JSON_MEDIA_TYPE)
        ?.at("schema")

    /** The `application/json` schema of the request body, or `null`. */
    fun requestSchema(): Schema? = operation.at("requestBody")
        ?.at("content")
        ?.at(JSON_MEDIA_TYPE)
        ?.at("schema")

    override fun toString(): String = "$contract $method $path ($operationId)"

    private companion object {
        val SUCCESS_RANGE = 200..399
        const val JSON_MEDIA_TYPE = "application/json"
    }
}

/** The sixteen contracts the four apps are allowed to reach — `ApiService`'s own list (C012). */
internal val IN_SCOPE_CONTRACTS: List<String> = listOf(
    "iam", "registry", "trip-state", "ride", "dispatch", "fare", "subscription", "wallet",
    "query", "transit", "safety", "support", "content", "voip", "notification", "version-check",
)
