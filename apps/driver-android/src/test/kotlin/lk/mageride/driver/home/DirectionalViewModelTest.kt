package lk.mageride.driver.home

import io.ktor.http.HttpStatusCode
import kotlinx.coroutines.runBlocking
import lk.mageride.driver.R
import lk.mageride.driver.onboarding.MainDispatcher
import lk.mageride.driver.onboarding.await
import lk.mageride.shared.data.models.GeoPoint
import lk.mageride.shared.data.models.dispatch.DirectionalFilterCleared
import lk.mageride.shared.data.models.dispatch.DirectionalFilterCreated
import lk.mageride.shared.data.models.dispatch.DirectionalFilterState
import lk.mageride.shared.data.models.iam.SavedAddressListResponse
import lk.mageride.shared.testing.fake.FakeApiBackend
import lk.mageride.shared.testing.fake.mageRideApi
import lk.mageride.shared.testing.fixture.Fixtures
import kotlin.test.AfterTest
import kotlin.test.BeforeTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue
import kotlin.time.Duration.Companion.hours
import kotlin.time.ExperimentalTime

/**
 * SCR-DA-013 — the daily budget, and the rule that makes it a budget.
 *
 * **Turning a filter off still spends its use** (DT-03, US-6A.19). Without it the whole feature is
 * free: a driver flicks the filter on for the one offer they want and off again, all day, on two
 * activations. `DELETE /v1/standby/directional` answers with the same `usesRemaining` it had
 * before, and this asserts the screen shows exactly that.
 */
@OptIn(ExperimentalTime::class)
class DirectionalViewModelTest {

    private val main = MainDispatcher()
    private val backend = FakeApiBackend()
    private val location = FakeDriverLocationSource()

    @BeforeTest
    fun setUp() {
        main.install()
    }

    @AfterTest
    fun tearDown() {
        main.uninstall()
    }

    @Test
    fun setting_a_direction_consumes_a_use_and_starts_the_countdown() = runBlocking {
        backend.returns("getDirectionalFilter", inactive(usesRemaining = 2))
        backend.returns("listSavedAddresses", SavedAddressListResponse(emptyList()))
        backend.returns(
            "setDirectionalFilter",
            DirectionalFilterCreated(
                filterId = Fixtures.TRIP_ID,
                expiresAt = Fixtures.NOW + 2.hours,
                usesRemaining = 1,
                maxDurationSec = 2.hours.inWholeSeconds.toInt(),
            ),
        )

        val model = viewModel()
        model.state.await { it.filter != null }

        assertFalse(model.state.value.canSet, "no destination chosen yet")
        model.choose(nugegoda())
        assertTrue(model.state.value.canSet)

        model.setDirection()
        model.state.await { it.isActive }

        assertEquals(1, model.state.value.usesRemaining)
        assertEquals(2.hours, model.state.value.maxDuration, "the only place a client learns the ceiling")
        assertFalse(model.state.value.canSet, "a filter is already running")
    }

    @Test
    fun turning_it_off_early_does_not_give_the_use_back() = runBlocking {
        // US-6A.19, the anti-gaming rule. The server answers with the count unchanged and so does
        // the card — a driver who thought they were getting it back would spend both before lunch.
        backend.returns("getDirectionalFilter", active(usesRemaining = 1))
        backend.returns("listSavedAddresses", SavedAddressListResponse(emptyList()))
        backend.returns("clearDirectionalFilter", DirectionalFilterCleared(active = false, usesRemaining = 1))

        val model = viewModel()
        model.state.await { it.isActive }

        model.turnOff()
        model.state.await { !it.isActive }

        assertEquals(1, model.state.value.usesRemaining, "the same count it had before")
        assertTrue(backend.called("clearDirectionalFilter"))
    }

    @Test
    fun an_exhausted_budget_disables_set_direction() = runBlocking {
        // The wireframe's "uses exhausted → Set disabled". The server would answer
        // `409 directional-limit-reached`; refusing locally saves the driver the round trip.
        backend.returns("getDirectionalFilter", inactive(usesRemaining = 0))
        backend.returns("listSavedAddresses", SavedAddressListResponse(emptyList()))

        val model = viewModel()
        model.state.await { it.filter != null }
        model.choose(nugegoda())

        assertFalse(model.state.value.canSet)
    }

    @Test
    fun setting_one_while_offline_is_the_servers_refusal_rendered_as_copy() = runBlocking {
        // `dispatch.yaml` carries no presence read, so the client cannot know it is offline. The
        // honest behaviour is to send and to render `403 not-online` in the driver's own language
        // (D-26) rather than to grey a control out on a guess.
        backend.returns("getDirectionalFilter", inactive(usesRemaining = 2))
        backend.returns("listSavedAddresses", SavedAddressListResponse(emptyList()))
        backend.fails("setDirectionalFilter", HttpStatusCode.Forbidden, "not-online")

        val model = viewModel()
        model.state.await { it.filter != null }
        model.choose(nugegoda())

        model.setDirection()
        val state = model.state.await { it.error != null }

        assertEquals(R.string.error_not_online, state.error)
        assertFalse(state.isActive)
        assertFalse(state.busy)
    }

    private fun inactive(usesRemaining: Int) =
        DirectionalFilterState(active = false, timeRemainingSec = 0, usesRemaining = usesRemaining)

    private fun active(usesRemaining: Int) = DirectionalFilterState(
        active = true,
        destination = GeoPoint(lat = Fixtures.DROPOFF.lat, lng = Fixtures.DROPOFF.lng),
        label = "Nugegoda",
        expiresAt = Fixtures.NOW + 2.hours,
        timeRemainingSec = 2.hours.inWholeSeconds.toInt(),
        usesRemaining = usesRemaining,
    )

    private fun nugegoda() = DirectionalDestination(
        label = "Nugegoda",
        point = GeoPoint(lat = Fixtures.DROPOFF.lat, lng = Fixtures.DROPOFF.lng),
    )

    private fun viewModel(): DirectionalViewModel {
        val api = backend.mageRideApi()
        return DirectionalViewModel(
            standby = StandbyRepository(
                dispatch = api.dispatch,
                wallet = api.wallet,
                subscription = api.subscription,
                query = api.query,
            ),
            query = api.query,
            iam = api.iam,
            location = location,
        )
    }
}
