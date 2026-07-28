package lk.mageride.e2e

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.apache.kafka.clients.consumer.ConsumerConfig
import org.apache.kafka.clients.consumer.KafkaConsumer
import org.apache.kafka.common.errors.WakeupException
import org.apache.kafka.common.serialization.StringDeserializer
import java.time.Duration
import java.util.Properties
import java.util.UUID
import java.util.concurrent.ConcurrentLinkedQueue
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import kotlin.concurrent.thread

/** One `offer.created` envelope off `dispatch.events` (D6' §2.2). */
internal data class Offer(val rideId: String, val offerId: String, val driverId: String, val expiresAt: String?)

/**
 * Watches `dispatch.events` for the offers dispatch-svc commits (R-13).
 *
 * **Why a Kafka consumer is in an e2e harness.** A driver accepts with
 * `POST /v1/rides/{rideId}/offer/{driverId}/accept`, whose body requires the `offerId` — and no
 * REST response anywhere returns it. `RideDetail` carries `offerExpiresAt` but not the id, and
 * `RideStateSnapshot` the same. In the finished platform the id reaches the handset on the
 * `offer.created` push: dispatch-svc's outbox → `dispatch.events` → notification-svc (C051) as FCM,
 * and → fanout-svc (C041) as a socket event. Neither exists yet, so the harness reads the topic
 * those two will read. **This is a real contract gap, not a shortcut** — recorded in the C025
 * handoff, because a driver app that missed the push today could never accept the offer.
 *
 * Reading it here also proves R-13 incidentally: the event is on the topic because a transaction
 * committed, not because dispatch decided to send one.
 */
internal class OfferWatcher(bootstrapServers: String) {

    private val offers = ConcurrentLinkedQueue<Offer>()

    @Volatile
    private var running = true

    private val consumer = KafkaConsumer<String, String>(
        Properties().apply {
            put(ConsumerConfig.BOOTSTRAP_SERVERS_CONFIG, bootstrapServers)
            // A group of its own per run, so a previous run's committed offsets cannot hide this
            // run's offers.
            put(ConsumerConfig.GROUP_ID_CONFIG, "e2e-offer-watcher-${UUID.randomUUID()}")
            // Latest: the topic outlives the stack, and an offer from a previous run is not this
            // run's. Started before the ride is booked, so nothing of ours is missed.
            put(ConsumerConfig.AUTO_OFFSET_RESET_CONFIG, "latest")
            put(ConsumerConfig.ENABLE_AUTO_COMMIT_CONFIG, "false")
            put(ConsumerConfig.KEY_DESERIALIZER_CLASS_CONFIG, StringDeserializer::class.java.name)
            put(ConsumerConfig.VALUE_DESERIALIZER_CLASS_CONFIG, StringDeserializer::class.java.name)
        },
    )

    /**
     * Starts the poll loop and returns once the consumer holds a partition assignment.
     *
     * **Everything that touches [consumer] happens on the poll thread.** `KafkaConsumer` throws
     * `ConcurrentModificationException` on any access from a second thread — including
     * `assignment()` — and the only exception is `wakeup()`. So the readiness check runs inside
     * the loop and this method waits on a latch rather than asking the consumer anything.
     */
    fun start() {
        val assigned = CountDownLatch(1)

        thread(name = "offer-watcher", isDaemon = true) {
            consumer.subscribe(listOf(TOPIC))

            try {
                while (running) {
                    val batch = consumer.poll(Duration.ofMillis(POLL_MS))
                    batch.forEach { record -> parse(record.value())?.let(offers::add) }

                    // `subscribe` is lazy: the first poll is what joins the group and is assigned
                    // partitions. Signalling before that would let the ride be booked against a
                    // consumer that is not listening yet, and with `auto.offset.reset=latest` the
                    // offer would simply never be seen.
                    if (consumer.assignment().isNotEmpty()) assigned.countDown()
                }
            } catch (_: WakeupException) {
                // close() asked us to stop; the only thread-safe thing it can do.
            } finally {
                runCatching { consumer.close() }
                assigned.countDown()
            }
        }

        check(assigned.await(ASSIGNMENT_TIMEOUT_MS, TimeUnit.MILLISECONDS)) {
            "The dispatch.events consumer never received a partition assignment."
        }
    }

    /** The next offer for [rideId] that this watcher has not already handed out. */
    fun awaitOffer(rideId: String, timeoutMs: Long): Offer? {
        val deadline = System.currentTimeMillis() + timeoutMs

        while (System.currentTimeMillis() < deadline) {
            val match = offers.firstOrNull { it.rideId == rideId }
            if (match != null) {
                offers.remove(match)
                return match
            }
            Thread.sleep(POLL_MS)
        }

        return null
    }

    fun close() {
        running = false
        runCatching { consumer.wakeup() }
    }

    private fun parse(payload: String?): Offer? {
        if (payload.isNullOrBlank()) return null

        val root = runCatching { Json.parseToJsonElement(payload) as? JsonObject }.getOrNull() ?: return null
        if (root["eventType"]?.jsonPrimitive?.content != OFFER_CREATED) return null

        val rideId = root["rideId"]?.jsonPrimitive?.content ?: return null
        val offerId = root["offerId"]?.jsonPrimitive?.content ?: return null
        val driverId = root["driverId"]?.jsonPrimitive?.content ?: return null

        return Offer(rideId, offerId, driverId, root["expiresAt"]?.jsonPrimitive?.content)
    }

    private companion object {
        const val TOPIC = "dispatch.events"
        const val OFFER_CREATED = "offer.created"
        const val POLL_MS = 200L
        const val ASSIGNMENT_TIMEOUT_MS = 30_000L
    }
}
