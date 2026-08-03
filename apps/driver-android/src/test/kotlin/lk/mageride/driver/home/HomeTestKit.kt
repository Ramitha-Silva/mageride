package lk.mageride.driver.home

import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.asSharedFlow
import lk.mageride.driver.location.DriverLocationSource
import lk.mageride.driver.location.Fix
import lk.mageride.driver.location.PositionPublisher
import lk.mageride.shared.data.models.AppSurface
import lk.mageride.shared.data.models.ServiceMode
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.VehicleType
import lk.mageride.shared.data.models.registry.OnboardingStatus
import lk.mageride.shared.data.models.registry.RegistrationStatus
import lk.mageride.shared.data.models.registry.VehicleSummary
import lk.mageride.shared.domain.auth.AuthConfig
import lk.mageride.shared.domain.auth.AuthSessionManager
import lk.mageride.shared.domain.auth.AuthSessionStore
import lk.mageride.shared.domain.auth.SessionState
import lk.mageride.shared.testing.fake.FakeApiBackend
import lk.mageride.shared.testing.fake.InMemorySecureStore
import lk.mageride.shared.testing.fake.mageRideApi
import lk.mageride.shared.testing.fixture.Fixtures
import kotlin.time.ExperimentalTime

/** The vehicle C070's tests go online with. */
internal const val HOME_VEHICLE_ID: Ulid = "01JVEHICLE0000000000000010"

/**
 * A vehicle that is eligible to go live, unless [approved] says otherwise.
 *
 * A Mode C vehicle needs `status = APPROVED` **and** `onboardingStatus = APPROVED` (AL-30); a Mode
 * A/B one carries no onboarding of its own because the Fleet Portal approved it, which is the other
 * half of `VehicleSummary.canGoLive` and the thing that decides which dashboard Home draws.
 */
internal fun liveVehicle(
    vehicleId: Ulid = HOME_VEHICLE_ID,
    mode: ServiceMode = ServiceMode.C,
    vehicleType: VehicleType = VehicleType.THREE_WHEELER,
    approved: Boolean = true,
): VehicleSummary = VehicleSummary(
    vehicleId = vehicleId,
    registrationNumber = "ABC-1234",
    vehicleType = vehicleType,
    mode = mode,
    status = if (approved) RegistrationStatus.APPROVED else RegistrationStatus.PENDING,
    onboardingStatus = if (approved) OnboardingStatus.APPROVED else OnboardingStatus.INCOMPLETE,
)

/** A fix at Colombo Fort, which is where every position in these tests is. */
@OptIn(ExperimentalTime::class)
internal fun fix(lat: Double = Fixtures.PICKUP.lat, lng: Double = Fixtures.PICKUP.lng): Fix =
    Fix(lat = lat, lng = lng, sampleTs = Fixtures.NOW)

/**
 * [DriverLocationSource] a test can push fixes into.
 *
 * A `SharedFlow` rather than a `StateFlow`: the production source is cold and emits nothing until
 * GNSS answers, and *"the driver has no position yet"* is the state the go-online gate has to
 * handle. Starting with a value would test the wrong thing.
 */
internal class FakeDriverLocationSource : DriverLocationSource {

    private val emitted = MutableSharedFlow<Fix>(replay = 1)

    override val fixes: Flow<Fix> = emitted.asSharedFlow()

    /** Delivers a fix, as the fused provider would. */
    suspend fun emit(fix: Fix) {
        emitted.emit(fix)
    }
}

/** [PositionPublisher] that records the calls, because their ORDER is the rule being tested. */
internal class FakePositionPublisher : PositionPublisher {

    /** `start:{vehicleId}` and `stop`, in order. */
    val calls: MutableList<String> = mutableListOf()

    override fun start(vehicleId: Ulid) {
        calls += "start:$vehicleId"
    }

    override fun stop() {
        calls += "stop"
    }
}

/** [JourneyPreferences] in memory — the production one is `SharedPreferences` (AL-32's record). */
internal class FakeJourneyPreferences(
    override var startedSessionId: Ulid? = null,
    override var routeId: String? = null,
) : JourneyPreferences

/**
 * An [AuthSessionManager] already signed in, over [backend].
 *
 * The OTP round trip is run for real against the fake so the manager reaches
 * [SessionState.SignedIn] the way it does in the app — there is no back door onto a session, and
 * C014 is explicit that tokens leave that class through exactly one door.
 */
internal suspend fun signedInSessions(backend: FakeApiBackend): AuthSessionManager {
    val api = backend.mageRideApi()
    val config = AuthConfig(app = AppSurface.DRIVER)
    val sessions = AuthSessionManager(
        api = { api.iam },
        store = AuthSessionStore(InMemorySecureStore(), config),
        config = config,
    )
    sessions.requestOtp(Fixtures.DRIVER_PHONE)
    sessions.verifyOtp(Fixtures.OTP)
    return sessions
}
