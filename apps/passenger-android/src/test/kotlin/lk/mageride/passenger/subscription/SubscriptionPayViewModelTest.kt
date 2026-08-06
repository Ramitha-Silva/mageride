package lk.mageride.passenger.subscription

import io.ktor.http.HttpStatusCode
import kotlinx.coroutines.runBlocking
import lk.mageride.passenger.MainDispatcher
import lk.mageride.passenger.R
import lk.mageride.passenger.await
import lk.mageride.shared.data.api.MageRideError
import lk.mageride.shared.data.models.ErrorCode
import lk.mageride.shared.data.models.ProblemDetails
import lk.mageride.shared.data.models.subscription.SubscriptionPayMethod
import lk.mageride.shared.data.models.subscription.SubscriptionPaymentStatus
import lk.mageride.shared.domain.auth.AuthSessionManager
import lk.mageride.shared.domain.subscription.ModeBPaymentStep
import kotlin.test.AfterTest
import kotlin.test.BeforeTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertIs
import kotlin.test.assertTrue

/**
 * SCR-PA-025a, and two Definition-of-Done lines: *"the pay sheet shows the correct payTo for the
 * owning org"* and *"an online-transfer payment shows Pending verification until the owner
 * confirms"*.
 *
 * The fence under both is AL-49: `payTo` is minted by `POST …/pay` from a **verified** payout
 * profile and by nothing else, so the sheet cannot show an account number before the payment
 * exists and must refuse cleanly when the fleet has no verified profile at all.
 */
class SubscriptionPayViewModelTest {

    private val main = MainDispatcher()
    private val repository = FakeSubscriptionRepository()

    @BeforeTest
    fun setUp() {
        main.install()
        repository.subscriptions = listOf(FakeSubscriptionRepository.paidSubscription())
    }

    @AfterTest
    fun tearDown() {
        main.uninstall()
    }

    @Test
    fun the_sheet_opens_on_lankaqr_and_shows_the_fare_before_anything_is_paid() = runBlocking {
        val model = viewModel()
        val state = model.state.await { !it.loading && it.subscription != null }

        assertEquals(SubscriptionPayMethod.LANKAQR_DEEPLINK, state.method, "the wireframe's pre-selected row")
        assertEquals(FakeSubscriptionRepository.MONTHLY_FARE_MINOR, state.amount?.amountMinor)
        assertTrue(state.canConfirm)
        // Nothing to show yet: `payTo` does not exist until the payment does.
        assertEquals(null, state.step)
    }

    @Test
    fun paying_by_transfer_needs_the_slip_first_and_lands_on_pending_verification() = runBlocking {
        // US-23.4. The screenshot is the evidence the owner confirms against; without it the
        // payment would sit at `initiated` with nothing for them to look at.
        val model = viewModel()
        model.state.await { !it.loading && it.subscription != null }

        model.choose(SubscriptionPayMethod.ONLINE_TRANSFER)
        assertFalse(model.state.value.canConfirm, "no slip, no confirm")

        model.attachSlip("slip.png", byteArrayOf(1, 2, 3))
        assertTrue(model.state.value.canConfirm)

        model.confirm()
        val state = model.state.await { it.settled }

        assertEquals(
            listOf(FakeSubscriptionRepository.SUBSCRIPTION_ID to SubscriptionPayMethod.ONLINE_TRANSFER),
            repository.paid,
        )
        assertEquals(1, repository.slipsUploaded.size, "the slip followed the initiation")
        assertEquals(SubscriptionPaymentStatus.PENDING_VERIFICATION, state.payment?.status)
        assertFalse(state.awaitingSlip)
    }

    @Test
    fun the_transfer_details_are_the_owners_and_arrive_with_the_payment() = runBlocking {
        // AL-49's "the pay sheet shows the correct payTo for the owning org" — the account is
        // never MageRide's, and it is never rendered from anything but the server's answer.
        val model = viewModel()
        model.state.await { !it.loading && it.subscription != null }
        model.choose(SubscriptionPayMethod.ONLINE_TRANSFER)
        model.attachSlip("slip.png", byteArrayOf(1))
        repository.slipAnswer = FakeSubscriptionRepository.payment(
            method = SubscriptionPayMethod.ONLINE_TRANSFER,
            status = SubscriptionPaymentStatus.INITIATED,
        )

        model.confirm()
        val state = model.state.await { it.payment != null }

        val step = assertIs<ModeBPaymentStep.TransferAndUploadSlip>(state.step)
        assertEquals("ABC Fleet (Pvt) Ltd", step.payTo.accountHolderName)
        assertEquals("8001234567", step.payTo.accountNo)
    }

    @Test
    fun the_scan_rail_resolves_the_owners_own_qr_image_through_the_signed_link() = runBlocking {
        // AL-49 again: the image is the fleet owner's bank-app QR, behind a signed URL. This app
        // renders no QR of its own (AL-22) — it shows theirs.
        repository.qrBytes = byteArrayOf(9, 9, 9)
        val model = viewModel()
        model.state.await { !it.loading && it.subscription != null }
        model.choose(SubscriptionPayMethod.LANKAQR_SCAN)

        model.confirm()
        val state = model.state.await { it.payment != null }

        assertIs<ModeBPaymentStep.ShowOwnerLankaQr>(state.step)
        assertEquals(
            listOf(FakeSubscriptionRepository.PAY_TO.lankaqrImageUrl),
            repository.qrLinksFetched,
            "the signed link the payment carried, not one this app built",
        )
        assertEquals(3, model.qrImage.value?.size)
    }

    @Test
    fun cash_tells_the_passenger_the_owner_has_to_record_it() = runBlocking {
        // US-23.6 — `POST …/mark-cash` is the OWNER's operation and answers 403 here. A spinner
        // waiting for a confirmation this handset can never receive would be a lie.
        val model = viewModel()
        model.state.await { !it.loading && it.subscription != null }
        model.choose(SubscriptionPayMethod.CASH)

        model.confirm()
        val state = model.state.await { it.payment != null }

        assertEquals(ModeBPaymentStep.HandToCollector, state.step)
        assertTrue(repository.slipsUploaded.isEmpty(), "cash has nothing to photograph")
    }

    @Test
    fun a_fleet_with_no_verified_payout_profile_is_refused_in_words_a_passenger_can_act_on() = runBlocking {
        // BR-31.1's 409. The money has nowhere to go, and the useful answer is "pay your
        // collector", not "something went wrong".
        val model = viewModel()
        model.state.await { !it.loading && it.subscription != null }

        // Armed after the load: `failWith` is one-shot, and the subscription read comes first.
        repository.failWith = MageRideError.Conflict(
            ProblemDetails(
                type = ErrorCode.PAYOUT_PROFILE_NOT_VERIFIED.typeUri,
                title = "Payout profile not verified",
                status = HttpStatusCode.Conflict.value,
            ),
        )

        model.confirm()
        val state = model.state.await { it.error != null }

        assertEquals(R.string.error_payout_not_verified, state.error)
        assertEquals(null, state.payment, "nothing was initiated")
    }

    @Test
    fun the_rail_cannot_change_once_the_payment_exists() = runBlocking {
        // The payment row is already typed with the method the server accepted; switching would
        // orphan it and leave the passenger looking at instructions for a rail nobody charged.
        val model = viewModel()
        model.state.await { !it.loading && it.subscription != null }

        model.confirm()
        model.state.await { it.payment != null }

        model.choose(SubscriptionPayMethod.CASH)
        assertEquals(SubscriptionPayMethod.LANKAQR_DEEPLINK, model.state.value.method)
        assertEquals(1, repository.paid.size)
    }

    // ------------------------------------------------------------------------------------------

    private fun viewModel() = main.own(
        SubscriptionPayViewModel(
            subscriptionId = FakeSubscriptionRepository.SUBSCRIPTION_ID,
            subscriptions = repository,
            sessions = session(),
            keys = { KEY },
        ),
    )

    private fun session(): AuthSessionManager = signedInSession().also { runBlocking { it.signIn() } }

    private companion object {
        const val KEY = "01JIDEMPOTENCY000000000003"
    }
}
