package lk.mageride.passenger.onboarding

import lk.mageride.passenger.shell.AppPreferences
import lk.mageride.shared.data.models.Language

/**
 * `AppPreferences` in memory.
 *
 * The production one is `SharedPreferences`, whose local-unit-test stub answers a default for
 * every member — a view model tested against it would report a first run that had already been
 * completed and vice versa.
 */
internal class FakeAppPreferences(
    override var language: Language? = null,
    override var languagePendingSync: Boolean = false,
    override var locationRationaleAcknowledged: Boolean = false,
    override var lastCallType: String? = null,
    override var callNumberNoticeShown: Boolean = false,
) : AppPreferences
