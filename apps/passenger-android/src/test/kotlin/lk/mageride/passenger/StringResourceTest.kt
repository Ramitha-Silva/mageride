package lk.mageride.passenger

import java.io.File
import javax.xml.parsers.DocumentBuilderFactory
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue
import kotlin.test.fail

/**
 * CLAUDE.md's trilingual rule, enforced.
 *
 * > *"All user-facing strings must support Si (Sinhala), Ta (Tamil), En (English). Use resource
 * > files, never hardcode strings."*
 *
 * The three `strings.xml` files are read off disk and compared rather than resolved through
 * `Resources`, because a local unit test has no `Resources` and Robolectric would be a large
 * dependency to answer a question that is really about the files. That is not a weaker check: what
 * can actually go wrong is a key added to one file and forgotten in the other two, a translation
 * left as its English placeholder, and a format string whose placeholders do not survive
 * translation — and all three are visible in the XML.
 *
 * **This is the shell's file today and every screen group's tomorrow.** C077–C084 add their copy
 * to all three files at once; this test is what tells them they did not.
 */
class StringResourceTest {

    private val resDir = File("src/main/res")

    private val english = parse("values")
    private val sinhala = parse("values-si")
    private val tamil = parse("values-ta")

    @Test
    fun every_key_resolves_in_all_three_locales() {
        assertTrue(english.isNotEmpty(), "no strings found — is the test running from the module directory?")

        assertEquals(english.keys, sinhala.keys, "si is missing or has extra keys")
        assertEquals(english.keys, tamil.keys, "ta is missing or has extra keys")
    }

    @Test
    fun no_translation_is_blank() {
        listOf("si" to sinhala, "ta" to tamil, "en" to english).forEach { (locale, strings) ->
            strings.forEach { (key, value) ->
                assertTrue(value.isNotBlank(), "$locale/$key is blank")
            }
        }
    }

    @Test
    fun no_translation_was_left_as_its_english_placeholder() {
        // Byte-identical to the English means a key was copied into si/ta and never translated —
        // the failure mode that produces a "trilingual" app which is English in two of its three
        // languages. Strict on purpose, and it costs something here: `app_name` is a brand and is
        // TRANSLITERATED in si/ta rather than exempted, for the reason argued in `values/strings.xml`.
        val untranslated = english.keys.filter { key ->
            val en = english.getValue(key)
            sinhala.getValue(key) == en || tamil.getValue(key) == en
        }

        assertTrue(untranslated.isEmpty(), "left in English in si or ta: $untranslated")
    }

    @Test
    fun a_format_string_keeps_its_placeholders_in_every_locale() {
        english.forEach { (key, en) ->
            val expected = placeholders(en)
            if (expected.isEmpty()) return@forEach

            assertEquals(expected, placeholders(sinhala.getValue(key)), "si/$key dropped a placeholder")
            assertEquals(expected, placeholders(tamil.getValue(key)), "ta/$key dropped a placeholder")
        }
    }

    /** `%1$d`, `%2$s` … — the positional forms Android's `getString(id, …)` fills in. */
    private fun placeholders(value: String): Set<String> =
        Regex("""%\d+\$[a-zA-Z]""").findAll(value).map { it.value }.toSet()

    private fun parse(valuesDir: String): Map<String, String> {
        val file = File(resDir, "$valuesDir/strings.xml")
        if (!file.exists()) fail("missing ${file.path}")

        val document = DocumentBuilderFactory.newInstance().newDocumentBuilder().parse(file)
        val nodes = document.getElementsByTagName("string")
        return buildMap {
            for (index in 0 until nodes.length) {
                val node = nodes.item(index)
                val name = node.attributes.getNamedItem("name").nodeValue
                put(name, node.textContent)
            }
        }
    }
}
