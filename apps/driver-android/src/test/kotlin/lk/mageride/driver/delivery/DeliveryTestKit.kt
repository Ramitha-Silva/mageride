package lk.mageride.driver.delivery

import lk.mageride.driver.capture.DocumentCaptureCoordinator
import lk.mageride.driver.home.FakeDriverLocationSource
import lk.mageride.driver.ride.RideContact
import lk.mageride.shared.data.models.PackageSize
import lk.mageride.shared.data.models.RideState
import lk.mageride.shared.data.models.RideVehicleType
import lk.mageride.shared.data.models.ride.PackageStatus
import lk.mageride.shared.data.models.ride.RideDetail
import lk.mageride.shared.data.models.ride.RideKind
import lk.mageride.shared.data.models.ride.RidePaymentMethod
import lk.mageride.shared.data.models.ride.RideStateChange
import lk.mageride.shared.testing.fake.FakeApiBackend
import lk.mageride.shared.testing.fake.mageRideApi
import lk.mageride.shared.testing.fixture.Fixtures

/** The sender's number on these tests' delivery. Distinct from the recipient's, which is the point. */
internal const val SENDER_PHONE: String = Fixtures.PASSENGER_PHONE

/** The recipient's. */
internal const val RECIPIENT_PHONE: String = Fixtures.DRIVER_PHONE

/**
 * A package ride at [state], as `GET /v1/rides/{rideId}` answers it.
 *
 * Both numbers are present because AL-33's sheets draw a call button beside each end, and they are
 * present from `Accepted` onward on the same terms as `counterpartyPhone`.
 */
internal fun packageRide(
    state: RideState,
    version: Int = 1,
    method: RidePaymentMethod = RidePaymentMethod.COD,
    senderPhone: String? = SENDER_PHONE,
    recipientPhone: String? = RECIPIENT_PHONE,
): RideDetail = RideDetail(
    rideId = Fixtures.RIDE_ID,
    kind = RideKind.PACKAGE,
    state = state,
    version = version,
    pickup = Fixtures.PICKUP,
    dropoff = Fixtures.DROPOFF,
    vehicleType = RideVehicleType.MOTORBIKE,
    paymentMethod = method,
    packageSize = PackageSize.M,
    packageDescription = "Documents, do not fold",
    packageStatus = if (state == RideState.InProgress) PackageStatus.InTransit else PackageStatus.PickupPending,
    recipientName = "Sunethra",
    counterpartyPhone = recipientPhone,
    senderPhone = senderPhone,
    recipientPhone = recipientPhone,
    createdAt = Fixtures.NOW,
)

/** A server-confirmed move, as every ride-svc mutation answers it. */
internal fun movedTo(state: RideState, version: Int): RideStateChange =
    RideStateChange(rideId = Fixtures.RIDE_ID, state = state, version = version)

/** SCR-DA-016's view model over [backend], with the seams a delivery needs. */
internal fun deliveryViewModel(
    backend: FakeApiBackend,
    location: FakeDriverLocationSource,
    proofs: ProofUploadQueue = ProofUploadQueue(),
    captures: DocumentCaptureCoordinator = DocumentCaptureCoordinator(),
): DeliveryViewModel {
    val api = backend.mageRideApi()
    return DeliveryViewModel(
        rideId = Fixtures.RIDE_ID,
        deliveries = DeliveryRepository(ride = api.ride),
        contact = RideContact(voip = api.voip, safety = api.safety),
        location = location,
        proofs = proofs,
        captures = captures,
    )
}
