package lk.mageride.driver.tracker

import lk.mageride.shared.data.models.Ulid

/** A well-formed IMEI. Fifteen digits, and the same one in every test in this package. */
internal const val TRACKER_IMEI: String = "861234567890123"

/** A second device, for the test that pairs two vehicles. */
internal const val OTHER_IMEI: String = "352099001761481"

/**
 * [TrackerBindingStore] in memory.
 *
 * The production one is `SharedPreferences`, whose local-unit-test stub answers a default for every
 * member — a publisher gate tested against it would report every vehicle untracked, which is the one
 * answer that makes the C074 fence look like it works when it does not.
 */
internal class FakeTrackerBindingStore(private val bindings: MutableMap<Ulid, TrackerBinding> = mutableMapOf()) :
    TrackerBindingStore {

    override fun bindingFor(vehicleId: Ulid): TrackerBinding? = bindings[vehicleId]

    override fun remember(binding: TrackerBinding) {
        bindings[binding.vehicleId] = binding
    }
}
