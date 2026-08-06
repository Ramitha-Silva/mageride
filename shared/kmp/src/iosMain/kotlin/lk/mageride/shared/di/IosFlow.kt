package lk.mageride.shared.di

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.launch

/**
 * A running collection of a [Flow], as something Swift can hold and cancel.
 *
 * `Job` is exported to Objective-C as an opaque protocol whose `cancel` takes a
 * `CancellationException?`; wrapping it in a class with one no-argument method is the difference
 * between a call site that reads and one that has to construct an exception to stop listening.
 */
public class FlowSubscription internal constructor(private val job: Job) {

    /** Stops collecting. Idempotent — cancelling a finished job does nothing. */
    public fun cancel() {
        job.cancel()
    }
}

/**
 * A [Flow] Swift can subscribe to.
 *
 * **Why this exists.** A Kotlin `Flow` is not usable from Swift: `collect` is a suspending function
 * whose parameter is a `FlowCollector` — a functional interface Swift cannot implement — so the
 * Objective-C export gives an app a type it can name and not a way to read it. The standard answers
 * are a plugin (KMP-NativeCoroutines) or a hand-written adapter; this is the adapter, and it is
 * thirty lines rather than a build-tool dependency for four flows.
 *
 * **Generic on purpose.** Kotlin/Native exports a generic *class* with Objective-C lightweight
 * generics, so `IosFlowWatcher<SessionEvent>` stays typed in Swift; a generic *function* would not
 * — its type parameter erases to `Any` and every call site would cast. That is the reason the
 * shape here is a class rather than an extension function on `Flow`.
 *
 * Collection runs on [Dispatchers.Main]: every consumer is a SwiftUI view model, and hopping to the
 * main actor once here is cheaper than making four call sites do it — and safer, because forgetting
 * once is a UI mutation off the main thread that usually looks like it works.
 */
public class IosFlowWatcher<T : Any> internal constructor(private val flow: Flow<T>) {

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)

    /**
     * Starts collecting, calling [onEach] for every value.
     *
     * @return the running collection. **Cancel it when the observer goes away** — nothing here is
     *   tied to a view's lifetime, so a subscription that is never cancelled keeps a closure (and
     *   whatever it captured) alive for the life of the process.
     */
    public fun watch(onEach: (T) -> Unit): FlowSubscription = FlowSubscription(
        scope.launch { flow.collect { value -> onEach(value) } },
    )
}
