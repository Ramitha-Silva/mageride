package lk.mageride.shared.data.models

import java.io.File
import kotlin.test.Test
import kotlin.test.assertTrue
import kotlin.test.fail

/**
 * The C012 definition of done: **no user-facing string literals are embedded in the models** —
 * trilingual resources live in the apps (D-26, CLAUDE.md "Trilingual resources").
 *
 * A review rule nobody can run is a rule that decays, so this asserts it against the source tree
 * instead. It runs in `androidHostTest` because that is the only source set with a filesystem;
 * `commonTest` has none, and the property being checked is about the checked-in Kotlin rather
 * than about anything observable at runtime.
 *
 * Comments are stripped before the check — a KDoc may legitimately *quote* a rendered amount when
 * explaining minor units. What must not appear is a literal a screen could display.
 */
class ModelSourceHygieneTest {

    private val sinhala = '඀'..'෿'
    private val tamil = '஀'..'௿'

    /**
     * The models package, whether the test runs from Gradle or from an IDE.
     *
     * Gradle sets the test task's working directory to the module (`shared/kmp`); an IDE often
     * sets it to the repository root instead. Walking up and trying both spellings covers both
     * without pinning the check to one runner.
     */
    private fun modelsDir(): File {
        val relative = "src/commonMain/kotlin/lk/mageride/shared/data/models"
        val workingDir = requireNotNull(System.getProperty("user.dir")) { "user.dir is unset" }
        var dir: File? = File(workingDir).absoluteFile
        while (dir != null) {
            val candidate = File(dir, relative)
            if (candidate.isDirectory) return candidate
            val nested = File(dir, "shared/kmp/$relative")
            if (nested.isDirectory) return nested
            dir = dir.parentFile
        }
        fail("could not locate $relative from $workingDir")
    }

    private fun sources(): List<File> = modelsDir().walkTopDown().filter { it.isFile && it.extension == "kt" }.toList()

    /** Splits a Kotlin source into its comment-free code and the string literals it contains. */
    private fun scan(source: String): Scanned = Scanner(source).run()

    /** A source file split into comment-free code and the string literals it contains. */
    private data class Scanned(val code: String, val literals: List<String>)

    /**
     * A character-level scanner over one Kotlin file.
     *
     * A regex cannot do this job: `"https://mageride.lk/errors/"` contains `//`, so stripping
     * line comments first would eat the rest of the file, and stripping literals first would eat
     * the `*` in a KDoc. The scanner therefore walks the characters in one of four states,
     * emitting comment characters as blanks so line and column offsets stay usable.
     */
    private class Scanner(private val source: String) {
        private val code = StringBuilder()
        private val literals = mutableListOf<String>()
        private val literal = StringBuilder()
        private var index = 0
        private var inString = false
        private var inLineComment = false
        private var blockDepth = 0

        private val next: Char? get() = source.getOrNull(index + 1)

        fun run(): Scanned {
            while (index < source.length) {
                val ch = source[index]
                when {
                    inLineComment -> lineComment(ch)
                    blockDepth > 0 -> blockComment(ch)
                    inString -> string(ch)
                    else -> outside(ch)
                }
                index++
            }
            return Scanned(code = code.toString(), literals = literals)
        }

        private fun lineComment(ch: Char) {
            if (ch == '\n') inLineComment = false
            blank(ch)
        }

        private fun blockComment(ch: Char) {
            // Kotlin block comments nest, so the depth matters.
            if (ch == '/' && next == '*') {
                open()
            } else if (ch == '*' && next == '/') {
                close()
            }
            blank(ch)
        }

        private fun string(ch: Char) {
            when {
                ch == '\\' -> {
                    emit(ch)
                    emit(next ?: ' ')
                    index++
                }

                ch == '"' -> {
                    inString = false
                    literals += literal.toString()
                    literal.clear()
                    code.append('"')
                }

                else -> emit(ch)
            }
        }

        private fun outside(ch: Char) {
            when {
                ch == '/' && next == '/' -> {
                    inLineComment = true
                    skipTwo()
                }

                ch == '/' && next == '*' -> {
                    blockDepth = 1
                    skipTwo()
                }

                ch == '"' -> {
                    inString = true
                    code.append('"')
                }

                else -> code.append(ch)
            }
        }

        private fun open() {
            blockDepth++
            index++
        }

        private fun close() {
            blockDepth--
            index++
        }

        private fun skipTwo() {
            index++
            code.append("  ")
        }

        private fun emit(ch: Char) {
            literal.append(ch)
            code.append(ch)
        }

        /** Comment characters become blanks, so offsets in the stripped code still line up. */
        private fun blank(ch: Char) {
            code.append(if (ch == '\n') ch else ' ')
        }
    }

    @Test
    fun the_models_are_a_non_trivial_source_tree() {
        val files = sources()

        assertTrue(files.size >= 20, "expected the DTO tree, found ${files.size} files")
        assertTrue(
            files.any { it.name == "Money.kt" } && files.any { it.name == "RideState.kt" },
            "the models directory did not resolve to the C012 sources",
        )
    }

    @Test
    fun no_model_embeds_sinhala_or_tamil_text() {
        // The single sharpest signal of localised copy: the models are wire shapes, and a Si or Ta
        // code point in one can only be display text that belongs in an app resource file.
        val offenders = sources().mapNotNull { file ->
            val bad = scan(file.readText()).code.filter { it in sinhala || it in tamil }
            if (bad.isEmpty()) null else "${file.name}: $bad"
        }

        assertTrue(offenders.isEmpty(), "localised text in the model sources: $offenders")
    }

    @Test
    fun no_model_formats_money_for_display() {
        // Rendering "Rs 480.00" needs a locale and a currency symbol in three languages. Money is
        // a value type over minor units and deliberately has no formatter (C012 fence).
        val markers = listOf("Rs ", "Rs.", "₨", "LKR ")
        val offenders = sources().mapNotNull { file ->
            val hits = scan(file.readText()).literals
                .filter { literal -> markers.any { literal.contains(it) } }
            if (hits.isEmpty()) null else "${file.name}: $hits"
        }

        assertTrue(offenders.isEmpty(), "formatted money in the model sources: $offenders")
    }

    @Test
    fun every_serial_name_is_a_machine_key_rather_than_a_label() {
        // A wire key is kebab, snake or camel and never contains a space or sentence punctuation.
        // Anything else in a @SerialName is display copy that has drifted into the contract layer.
        val offenders = sources().flatMap { file ->
            Regex("""@SerialName\("([^"]*)"\)""")
                .findAll(scan(file.readText()).code)
                .map { it.groupValues[1] }
                .filterNot { it.matches(Regex("^[A-Za-z0-9][A-Za-z0-9_.:-]*$")) }
                .map { "${file.name}: $it" }
        }

        assertTrue(offenders.isEmpty(), "non-machine serial names: $offenders")
    }

    @Test
    fun the_only_multi_word_literals_are_the_documented_programming_error_messages() {
        // Everything else must be a machine key (a wire value, a URI prefix, a template key). The
        // three allowed messages are `require` guards on programming errors — they can only fire
        // on a caller mistake, are never rendered, and are named here so a fourth cannot be added
        // without a deliberate edit to this test.
        val allowed = setOf(
            "Money operands must share a currency",
            "limit must be between 1 and 100",
            "Unknown PositionSource code: ",
        )

        val offenders = sources().flatMap { file ->
            scan(file.readText()).literals
                .filter { it.contains(' ') }
                .filterNot { literal -> allowed.any { literal.startsWith(it) } }
                .map { "${file.name}: \"$it\"" }
        }

        assertTrue(offenders.isEmpty(), "unexpected prose in the model sources: $offenders")
    }
}
