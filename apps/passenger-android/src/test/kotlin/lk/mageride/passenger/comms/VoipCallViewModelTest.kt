package lk.mageride.passenger.comms

import io.ktor.http.HttpStatusCode
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flowOf
import kotlinx.coroutines.runBlocking
import lk.mageride.passenger.MainDispatcher
import lk.mageride.passenger.await
import lk.mageride.passenger.ride.FakeRideRepository
import lk.mageride.shared.data.api.MageRideError
import lk.mageride.shared.data.models.CallType
import lk.mageride.shared.data.models.ErrorCode
import lk.mageride.shared.data.models.ProblemDetails
import lk.mageride.shared.data.models.comms.CallOutcome
import lk.mageride.shared.data.models.comms.VoipSession
import kotlin.test.AfterTest
import kotlin.test.BeforeTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/**
 * SCR-PA-028, and the Definition-of-Done line *"VoIP call screen with connecting/active/failed
 * states and the direct-dial fallback prompt"*.
 *
 * The fence this class exists to hold is AL-48's: **a VoIP failure offers a direct dial and nothing
 * else.** There is no masked path to fall back to and no masked-SMS relay, so what has to be true is
 * that a failure (a) reports `voip_failed` so the platform can see it, (b) offers the real number
 * only when the ride actually carries one, and (c) logs the `direct_dial` that follows so a fallback
 * is distinguishable from a passenger who simply preferred to dial.
 */
class VoipCallViewModelTest {

    private val main = MainDispatcher()
    private val rides = FakeRideRepository()

    @BeforeTest
    fun setUp() {
        main.install()
    }

    @AfterTest
    fun tearDown() {
        main.uninstall()
    }

    @Test
    fun a_free_call_starts_one_call_log_row_and_joins_the_room_it_answers_with() = runBlocking {
        rides.rideAnswer = FakeRideRepository.accepted()
        val engine = RecordingVoipEngine(flowOf(CallLink.Connecting, CallLink.Connected))

        val model = viewModel(engine)
        val state = model.state.await { it.stage == CallStage.CONNECTED }

        assertEquals(listOf(CallType.FREE_VOIP), rides.calls, "one row per call, and the chooser wrote none")
        assertEquals(listOf(rides.callSession), engine.joined, "the room voip-svc minted, not a second one")
        assertEquals("K. Fernando", state.calleeName)
    }

    @Test
    fun a_session_less_answer_is_a_signalling_failure_and_is_reported_as_voip_failed() = runBlocking {
        // `POST /v1/calls/start` answering 200 with no `session` is the case voip.yaml describes for
        // a direct dial; for a free call it means there is nothing to join.
        rides.rideAnswer = FakeRideRepository.accepted()
        rides.callSession = null

        val model = viewModel(RecordingVoipEngine(flowOf(CallLink.Connected)))
        val state = model.state.await { it.stage == CallStage.FAILED }

        assertEquals(VoipFailure.SIGNALLING, state.failure)
        assertEquals(
            listOf(FakeRideRepository.CALL_ID to CallOutcome.VOIP_FAILED),
            rides.outcomes,
            "Δ C055 — the only way voip-svc sees a call that never connected",
        )
    }

    @Test
    fun the_engine_this_build_ships_fails_and_the_screen_offers_the_real_number() = runBlocking {
        // `AbsentVoipEngine` is what `passengerAppModule` binds: the signalling half is real and the
        // media half is a dependency wall. That is precisely the condition AL-48 legislates for.
        rides.rideAnswer = FakeRideRepository.accepted()

        val model = viewModel(AbsentVoipEngine())
        val state = model.state.await { it.stage == CallStage.FAILED }

        assertEquals(VoipFailure.NO_MEDIA_CLIENT, state.failure)
        assertTrue(state.canDialDirectly, "AL-48's fallback is the whole of what a failure offers")
        assertEquals("+94771234567", state.counterpartyPhone)
    }

    @Test
    fun a_failure_on_a_ride_with_no_number_offers_no_dial() = runBlocking {
        // `RideDetail.counterpartyPhone` is carried from `Accepted` onward, and a terminal ride
        // answers `409 ride-terminal` and carries none. Offering a dial there would be advice to
        // call somebody who is not there.
        rides.rideAnswer = FakeRideRepository.ride()

        val model = viewModel(AbsentVoipEngine())
        val state = model.state.await { it.stage == CallStage.FAILED }

        assertFalse(state.canDialDirectly)
    }

    @Test
    fun the_fallback_dial_writes_a_second_direct_dial_row_against_the_same_ride() = runBlocking {
        rides.rideAnswer = FakeRideRepository.accepted()
        val model = viewModel(AbsentVoipEngine())
        model.state.await { it.canDialDirectly }

        model.dialDirectly()
        val state = model.state.await { it.dialNumber != null }

        assertEquals("+94771234567", state.dialNumber, "the screen hands this to ACTION_DIAL")
        assertEquals(
            listOf(CallType.FREE_VOIP, CallType.DIRECT_DIAL),
            rides.calls,
            "a direct_dial row after a voip_failed one IS the fallback being taken",
        )
    }

    @Test
    fun hanging_up_a_connected_call_completes_it_and_an_unanswered_one_cancels_it() = runBlocking {
        rides.rideAnswer = FakeRideRepository.accepted()

        val connected = viewModel(RecordingVoipEngine(flowOf(CallLink.Connected)))
        connected.state.await { it.stage == CallStage.CONNECTED }
        connected.hangUp()
        connected.state.await { it.finished }

        assertEquals(CallOutcome.COMPLETED, rides.outcomes.single().second)

        rides.outcomes.clear()
        val ringing = viewModel(RecordingVoipEngine(flowOf(CallLink.Connecting)))
        ringing.state.await { it.stage == CallStage.CONNECTING }
        ringing.hangUp()
        ringing.state.await { it.finished }

        assertEquals(CallOutcome.CANCELLED, rides.outcomes.single().second, "the caller's own word for it")
    }

    @Test
    fun a_failed_outcome_report_never_reaches_the_passenger() = runBlocking {
        // `comms.call_log` is documented best-effort. A passenger trying to reach their driver must
        // not be shown a failure about a log row — the fallback prompt is what they need to see.
        rides.rideAnswer = FakeRideRepository.accepted()
        rides.outcomeFails = IllegalStateException("outbox is down")

        val model = viewModel(MediaFailureEngine())
        val state = model.state.await { it.stage == CallStage.FAILED }

        assertEquals(VoipFailure.MEDIA, state.failure)
        assertTrue(state.canDialDirectly, "the fallback survives a log row that did not write")
    }

    @Test
    fun a_start_call_that_throws_still_reaches_the_failure_state() = runBlocking {
        // `409 ride-terminal` — the ride ended between the tap and the request.
        rides.rideAnswer = FakeRideRepository.accepted()
        rides.callFails = MageRideError.Conflict(
            ProblemDetails(
                type = ErrorCode.RIDE_TERMINAL.typeUri,
                title = "Ride terminal",
                status = HttpStatusCode.Conflict.value,
            ),
        )

        val model = viewModel(AbsentVoipEngine())
        val state = model.state.await { it.stage == CallStage.FAILED }

        assertEquals(VoipFailure.SIGNALLING, state.failure)
        assertTrue(rides.outcomes.isEmpty(), "there is no call id to close — the row was never opened")
    }

    // ------------------------------------------------------------------------------------------

    private fun viewModel(engine: VoipEngine) =
        main.own(VoipCallViewModel(rideId = FakeRideRepository.RIDE_ID, rides = rides, engine = engine))

    /** A [VoipEngine] that replays a scripted link and records what it was asked to join. */
    private class RecordingVoipEngine(private val script: Flow<CallLink>) : VoipEngine {

        val joined = mutableListOf<VoipSession>()

        override fun join(session: VoipSession): Flow<CallLink> {
            joined += session
            return script
        }

        override fun setMicrophoneMuted(muted: Boolean) = Unit
        override fun setSpeakerphoneOn(on: Boolean) = Unit
        override fun leave() = Unit
    }

    /** Joins, then loses the media path — the [VoipFailure.MEDIA] arm a real engine would report. */
    private class MediaFailureEngine : VoipEngine {
        override fun join(session: VoipSession): Flow<CallLink> =
            flowOf(CallLink.Connecting, CallLink.Failed(VoipFailure.MEDIA))

        override fun setMicrophoneMuted(muted: Boolean) = Unit
        override fun setSpeakerphoneOn(on: Boolean) = Unit
        override fun leave() = Unit
    }
}
