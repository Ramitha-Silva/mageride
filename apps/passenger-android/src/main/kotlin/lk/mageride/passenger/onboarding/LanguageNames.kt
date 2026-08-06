package lk.mageride.passenger.onboarding

import lk.mageride.shared.data.models.Language

/**
 * SCR-PA-002's three rows, in the order US-1.3 fixes them.
 *
 * *"ordered Sinhala (first) → Tamil → English (default highlight = Sinhala)"* — not the enum's own
 * declaration order by accident, but the story's order on purpose. `OnboardingScreenTest` pins it.
 */
internal val LanguageChoices: List<Language> = listOf(Language.SI, Language.TA, Language.EN)

/**
 * The language's own name in its own script — `සිංහල`, `தமிழ்`, `English`.
 *
 * **Deliberately not a string resource.** An endonym is the same string in all three locales, so
 * the three `strings.xml` files would carry three identical values — which `StringResourceTest`
 * correctly rejects as "a key copied into si and ta and never translated". A proper noun is
 * **data**, not copy. Whoever is looking for their language is looking for the script they read.
 */
internal val Language.endonym: String
    get() = when (this) {
        Language.SI -> "සිංහල"
        Language.TA -> "தமிழ்"
        Language.EN -> "English"
    }

/**
 * The English gloss beside it, so a passenger who reads neither other script can still navigate.
 *
 * `null` for English, where the gloss would repeat the endonym — which is exactly what the
 * wireframe draws: `සිංහල · Sinhala`, `தமிழ் · Tamil`, and a bare `English`.
 */
internal val Language.englishName: String?
    get() = when (this) {
        Language.SI -> "Sinhala"
        Language.TA -> "Tamil"
        Language.EN -> null
    }
