package lk.mageride.driver.comms

import io.ktor.http.HttpStatusCode
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flow
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import lk.mageride.driver.onboarding.MainDispatcher
import lk.mageride.driver.onboarding.await
import lk.mageride.driver.ride.ActiveRideRepository
import lk.mageride.driver.ride.RideContact
import lk.mageride.shared.data.models.CallType
import lk.mageride.shared.data.models.RideState
import lk.mageride.shared.data.models.RideVehicleType
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.comms.StartCallResponse
import lk.mageride.shared.data.models.comms.VoipSession
import lk.mageride.shared.data.models.ride.RideDetail
import lk.mageride.shared.data.models.ride.RideKind
import lk.mageride.shared.data.models.ride.RidePaymentMethod
import lk.mageride.shared.serialization.MageRideJson
import lk.mageride.shared.testing.fake.FakeApiBackend
import lk.mageride.shared.testing.fake.FakeCall
import lk.mageride.shared.testing.fake.mageRideApi
import lk.mageride.shared.testing.fixture.Fixtures
import kotlin.test.AfterTest
import kotlin.test.BeforeTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue
import kotlin.time.Duration.Companion.milliseconds
import kotlin.time.Duration.Companion.seconds

/**
 * SCR-DA-031 — the call, and AL-48's fallback.
 *
 * The fence this component was given: *"VoIP failure offers 'Call normally instead?' (direct dial).
 * The masked-SMS fallback is removed (AL-48)."* Both halves are asserted — that the prompt appears,
 * and that what it sends is a `direct_dial` call and **not** a relay of any kind.
 */
class VoipCallViewModelTest {

    private val main = MainDispatcher()
    private val backend = FakeApiBackend()

    private val callId: Ulid = "01JCALL00000000000000001"

    @BeforeTest
    fun setUp() {
        main.install()
        backend.returns("getRide", ride())
    }

    @AfterTest
    fun tearDown() {
        main.uninstall()
    }

    @Test
    fun the_call_starts_as_free_voip_against_the_rider() = runBlocking {
        backend.returns("startCall", started(session = session()))

        val model = viewModel(engine = ScriptedVoipEngine(CallLink.Connecting, CallLink.Connected))
        val state = model.state.await { it.stage == CallStage.CONNECTED }

        assertEquals(Fixtures.RIDE_ID, backend.lastCall("startCall").json["rideId"]?.toString()?.trim('"'))

        val body = MageRideJson.parseToJsonElement(backend.lastCall("startCall").body).toString()
        assertTrue(body.contains("\"callType\":\"${CallType.FREE_VOIP.wire}\""), body)
        // P-05 — the driver is connected to the RIDER, never to whoever booked the ride.
        assertTrue(body.contains("\"calleeRole\":\"passenger\""), body)
        assertEquals(RIDER_NAME, state.calleeName)
    }

    @Test
    fun a_media_failure_offers_the_direct_dial_and_logs_voip_failed() = runBlocking {
        backend.returns("startCall", started(session = session()))

        val model = viewModel(engine = ScriptedVoipEngine(CallLink.Failed(VoipFailure.MEDIA)))
        val state = model.state.await { it.stage == CallStage.FAILED }

        assertTrue(state.canDialDirectly, "AL-48's prompt is what a failure falls back to")
        val outcome = awaitCall("recordCallOutcome")
        assertTrue(outcome.body.contains("voip_failed"), outcome.body)
        assertTrue(outcome.path.endsWith("/$callId/outcome"), outcome.path)
    }

    @Test
    fun the_fallback_dials_the_real_number_and_records_a_second_direct_dial_call() = runBlocking {
        backend.returns("startCall", started(session = session()))

        val model = viewModel(engine = ScriptedVoipEngine(CallLink.Failed(VoipFailure.NO_MEDIA_CLIENT)))
        model.state.await { it.stage == CallStage.FAILED }

        model.dialDirectly()
        val state = model.state.await { it.dialNumber != null }

        assertEquals(COUNTERPARTY_PHONE, state.dialNumber, "the rider's real MSISDN (AL-48)")
        // Two rows on one ride: the free call that failed, then the dial that replaced it. That
        // pair is exactly what lets voip-svc tell a fallback from a driver who preferred to dial.
        val calls = backend.callsTo("startCall")
        assertEquals(2, calls.size)
        assertTrue(calls.last().body.contains(CallType.DIRECT_DIAL.wire), calls.last().body)
    }

    @Test
    fun a_ride_that_has_ended_offers_no_dial_at_all() = runBlocking {
        // `409 ride-terminal`, and the ride read fails with it too — there is nobody left to reach,
        // so "call normally instead?" would be wrong advice rather than a fallback.
        backend.fails("getRide", HttpStatusCode.Conflict, "ride-terminal")
        backend.fails("startCall", HttpStatusCode.Conflict, "ride-terminal")

        val model = viewModel(engine = ScriptedVoipEngine(CallLink.Connected))
        val state = model.state.await { it.stage == CallStage.FAILED }

        assertEquals(VoipFailure.SIGNALLING, state.failure)
        assertFalse(state.canDialDirectly)
    }

    @Test
    fun this_build_never_reaches_the_connected_state_and_says_so() = runBlocking {
        // The honest limit, pinned. `AbsentVoipEngine` is what `driverAppModule` binds because
        // `io.livekit:livekit-android` cannot be resolved from this repository's repositories —
        // read its KDoc. When a real engine lands, THIS is the test that should start failing.
        backend.returns("startCall", started(session = session()))

        val model = viewModel(engine = AbsentVoipEngine())
        val state = model.state.await { it.stage == CallStage.FAILED }

        assertEquals(VoipFailure.NO_MEDIA_CLIENT, state.failure)
        assertTrue(state.canDialDirectly, "and the driver still reaches the rider")
    }

    @Test
    fun hanging_up_a_connected_call_reports_it_completed() = runBlocking {
        backend.returns("startCall", started(session = session()))

        val model = viewModel(engine = ScriptedVoipEngine(CallLink.Connected))
        model.state.await { it.stage == CallStage.CONNECTED }
        model.hangUp()

        model.state.await { it.finished }
        assertTrue(awaitCall("recordCallOutcome").body.contains("completed"))
    }

    @Test
    fun hanging_up_before_it_connects_reports_it_cancelled() = runBlocking {
        backend.returns("startCall", started(session = session()))

        val engine = ScriptedVoipEngine(CallLink.Connecting)
        val model = viewModel(engine = engine)
        // Wait for the room to have been JOINED, not merely asked for: `CONNECTING` is the initial
        // state, and `join` is reached only once `POST /v1/calls/start` has answered with the
        // `callId` this outcome is reported against. Hanging up before that is a call the platform
        // has no row for — correct, and a different case from this one.
        awaitJoin(engine)
        model.hangUp()

        model.state.await { it.finished }
        assertTrue(awaitCall("recordCallOutcome").body.contains("cancelled"))
    }

    @Test
    fun the_toggles_reach_the_engine_and_the_state() = runBlocking {
        backend.returns("startCall", started(session = session()))
        val engine = ScriptedVoipEngine(CallLink.Connected)

        val model = viewModel(engine = engine)
        model.state.await { it.stage == CallStage.CONNECTED }

        model.toggleMute()
        model.toggleSpeaker()

        assertTrue(model.state.value.muted)
        assertTrue(model.state.value.speakerOn)
        assertEquals(listOf("mute:true", "speaker:true"), engine.calls)
    }

    @Test
    fun a_start_call_that_answers_without_a_session_is_a_signalling_failure() = runBlocking {
        // `StartCallResponse.session` is present ONLY for a free VoIP call, so a 200 with none is a
        // room that was never minted — there is nothing to join and nothing to wait for.
        backend.returns("startCall", started(session = null))

        val model = viewModel(engine = ScriptedVoipEngine(CallLink.Connected))
        val state = model.state.await { it.stage == CallStage.FAILED }

        assertEquals(VoipFailure.SIGNALLING, state.failure)
        assertTrue(state.canDialDirectly, "the ride read succeeded, so there is still a number")
    }

    /**
     * Waits for [operationId] to have been called, then answers it.
     *
     * The outcome report is deliberately fire-and-forget — `comms.call_log` is best-effort and a
     * driver must never wait on it — so the state reaches its next stage *before* the request
     * lands, and asserting on `lastCall` straight after would be a race the production code is
     * right to create.
     */
    private suspend fun awaitCall(operationId: String): FakeCall = withTimeout(AWAIT) {
        while (!backend.called(operationId)) delay(POLL)
        backend.lastCall(operationId)
    }

    /** Waits until [engine] has been asked to join, which is the moment the `callId` exists. */
    private suspend fun awaitJoin(engine: ScriptedVoipEngine) = withTimeout(AWAIT) {
        while (!engine.joined) delay(POLL)
    }

    private fun started(session: VoipSession?) =
        StartCallResponse(callId = callId, callType = CallType.FREE_VOIP, session = session)

    private fun session() = VoipSession(
        roomName = "ride_${Fixtures.RIDE_ID}",
        token = "livekit-jwt",
        wsUrl = "wss://voip.mageride.lk",
    )

    private fun ride() = RideDetail(
        rideId = Fixtures.RIDE_ID,
        kind = RideKind.PASSENGER,
        state = RideState.InProgress,
        version = 1,
        riderName = RIDER_NAME,
        pickup = Fixtures.PICKUP,
        dropoff = Fixtures.DROPOFF,
        vehicleType = RideVehicleType.THREE_WHEELER,
        paymentMethod = RidePaymentMethod.CASH,
        counterpartyPhone = COUNTERPARTY_PHONE,
        createdAt = Fixtures.NOW,
    )

    private fun viewModel(engine: VoipEngine): VoipCallViewModel {
        val api = backend.mageRideApi()
        return main.own(
            VoipCallViewModel(
                rideId = Fixtures.RIDE_ID,
                rides = ActiveRideRepository(ride = api.ride, fare = api.fare),
                contact = RideContact(voip = api.voip, safety = api.safety),
                engine = engine,
            ),
        )
    }

    private companion object {

        val AWAIT = 10.seconds
        val POLL = 10.milliseconds

        /** The rider's real MSISDN, which AL-48's fallback dials. */
        const val COUNTERPARTY_PHONE = Fixtures.PASSENGER_PHONE

        /** The wireframe's *"Nimal (rider)"*. */
        const val RIDER_NAME = "Nimal"
    }
}

/**
 * A [VoipEngine] that emits a scripted sequence of links.
 *
 * The production engine needs a radio and a media stack; what this class tests is the state machine
 * over it — which is exactly the split the interface exists to make possible.
 */
private class ScriptedVoipEngine(private vararg val links: CallLink) : VoipEngine {

    /** `mute:true`, `speaker:false` — in order, because the ORDER is what the toggles claim. */
    val calls: MutableList<String> = mutableListOf()

    /** Set the moment the view model asks for the room — see `awaitJoin`. */
    @Volatile
    var joined: Boolean = false

    override fun join(session: VoipSession): Flow<CallLink> {
        joined = true
        return flow { links.forEach { emit(it) } }
    }

    override fun setMicrophoneMuted(muted: Boolean) {
        calls += "mute:$muted"
    }

    override fun setSpeakerphoneOn(on: Boolean) {
        calls += "speaker:$on"
    }

    override fun leave() {
        calls += "leave"
    }
}
