package lk.mageride.shared.data.api

import java.io.File

// The scanning half of ContractCoverageTest: read the OpenAPI documents and the client sources,
// and answer three questions about each operation. Kept out of the test class so the assertions
// there read as assertions.

/** One operation as `backend/contracts/{contract}.yaml` declares it. */
internal class ContractOperation(
    val contract: String,
    val path: String,
    val method: String,
    val operationId: String,
    val attested: Boolean,
    val idempotencyExempt: Boolean,
) {
    fun describe(): String = "$contract ${method.uppercase()} $path ($operationId)"

    /** The `ApiTransport` helper this operation must be called through. */
    fun expectedVerb(): String = when {
        method == "post" && idempotencyExempt -> "apiPostExempt"
        method == "post" -> "apiPost"
        else -> "api" + method.replaceFirstChar { it.uppercase() }
    }
}

/**
 * Pulls the operations out of an OpenAPI document by scanning, not parsing.
 *
 * `androidHostTest` has no YAML library, the contracts have one regular shape, and the three facts
 * needed here are each a single-line marker. `spectral` in `backend/contracts` is what validates
 * the documents themselves; this only has to find them.
 *
 * The shape relied on: a path key at two spaces of indent, a method key at four, and the
 * operation's own keys below that. No in-scope contract declares path-level `parameters`.
 */
internal object ContractScanner {

    private const val PATH_INDENT = 2
    private const val METHOD_INDENT = 4
    private val HTTP_METHODS = setOf("get", "post", "put", "patch", "delete")

    fun operations(contract: String, yaml: String): List<ContractOperation> =
        methodBlocks(yaml).mapNotNull { block -> block.toOperation(contract) }

    private fun methodBlocks(yaml: String): List<MethodBlock> {
        val blocks = mutableListOf<MethodBlock>()
        var path = ""
        var current: MethodBlock? = null
        for (raw in yaml.lines()) {
            val line = raw.trim()
            if (line.isEmpty() || line.startsWith("#")) continue
            val indent = raw.length - raw.trimStart().length
            val key = line.removeSuffix(":")
            when {
                indent == PATH_INDENT && line.startsWith("/") && line.endsWith(":") -> {
                    path = key
                    current = null
                }

                indent == METHOD_INDENT && line.endsWith(":") && key in HTTP_METHODS -> {
                    current = MethodBlock(path, key).also { blocks += it }
                }

                else -> current?.lines?.add(line)
            }
        }
        return blocks
    }

    private class MethodBlock(val path: String, val method: String) {
        val lines: MutableList<String> = mutableListOf()

        fun toOperation(contract: String): ContractOperation? {
            val operationId = lines.firstOrNull { it.startsWith("operationId:") }
                ?.substringAfter(":")
                ?.trim()
                ?: return null
            return ContractOperation(
                contract = contract,
                path = path,
                method = method,
                operationId = operationId,
                attested = lines.any { it.contains("parameters/XAttestation") },
                idempotencyExempt = lines.any { it.startsWith("x-idempotency-exempt:") },
            )
        }
    }
}

/**
 * The `data/api` source tree, indexed so a test can ask how an operation is actually called.
 *
 * Looks for the operation's id *inside* a `transport.apiXxx(` call, which is what makes "the
 * client covers this operation" mean "something calls it" rather than "the string appears".
 */
internal class ClientSourceIndex(private val source: String) {

    /** Whether any `transport.apiXxx(` call names [operationId]. */
    fun covers(operationId: String): Boolean = callIndexFor(operationId) != null

    /** The transport helper the call goes through, or `null` when nothing calls it. */
    fun verbFor(operationId: String): String? {
        val index = callIndexFor(operationId) ?: return null
        val before = source.substring((index - LOOKBEHIND).coerceAtLeast(0), index)
        return VERBS
            .mapNotNull { verb -> before.lastIndexOf("transport.$verb(").takeIf { it >= 0 }?.let { verb to it } }
            .maxByOrNull { it.second }
            ?.first
    }

    /** The source from the operation's id to the start of the next `override`, or `null`. */
    fun callBodyFor(operationId: String): String? {
        val index = callIndexFor(operationId) ?: return null
        val window = source.substring(index, (index + LOOKAHEAD).coerceAtMost(source.length))
        val end = window.indexOf("override suspend")
        return if (end >= 0) window.take(end) else window
    }

    private fun callIndexFor(operationId: String): Int? {
        val literal = "\"$operationId\""
        var from = 0
        while (true) {
            val index = source.indexOf(literal, from)
            if (index < 0) return null
            val before = source.substring((index - LOOKBEHIND).coerceAtLeast(0), index)
            if (VERBS.any { before.contains("transport.$it(") }) return index
            from = index + literal.length
        }
    }

    private companion object {
        const val LOOKBEHIND = 400
        const val LOOKAHEAD = 700
        val VERBS = listOf("apiPostExempt", "apiPost", "apiGet", "apiPut", "apiDelete")
    }
}

/** Finds repository paths whether the test runs from Gradle (module dir) or an IDE (repo root). */
internal object RepoLocator {

    fun file(relative: String): File = locate(relative) { it.isFile }

    fun dir(relative: String): File = locate(relative) { it.isDirectory }

    private fun locate(relative: String, accept: (File) -> Boolean): File {
        val workingDir = requireNotNull(System.getProperty("user.dir")) { "user.dir is unset" }
        var dir: File? = File(workingDir).absoluteFile
        while (dir != null) {
            val candidate = File(dir, relative)
            if (accept(candidate)) return candidate
            dir = dir.parentFile
        }
        error("could not locate $relative from $workingDir")
    }
}
