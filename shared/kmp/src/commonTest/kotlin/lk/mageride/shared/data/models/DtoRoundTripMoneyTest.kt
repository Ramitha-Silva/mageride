package lk.mageride.shared.data.models

import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import lk.mageride.shared.data.models.subscription.AccessRequest
import lk.mageride.shared.data.models.subscription.AccessRequestAccepted
import lk.mageride.shared.data.models.subscription.ChargeDailyFeeRequest
import lk.mageride.shared.data.models.subscription.CreditTransfer
import lk.mageride.shared.data.models.subscription.CreditTransferDirection
import lk.mageride.shared.data.models.subscription.CreditTransferStatus
import lk.mageride.shared.data.models.subscription.DailyFeeCharge
import lk.mageride.shared.data.models.subscription.DailyFeeChargeStatus
import lk.mageride.shared.data.models.subscription.DailyFeeDayStatus
import lk.mageride.shared.data.models.subscription.DailyFeeRate
import lk.mageride.shared.data.models.subscription.DailyFeeRateList
import lk.mageride.shared.data.models.subscription.MarkSubscriberCashPaidRequest
import lk.mageride.shared.data.models.subscription.PayModeBSubscriptionRequest
import lk.mageride.shared.data.models.subscription.PayTo
import lk.mageride.shared.data.models.subscription.PurchaseVoucherRequest
import lk.mageride.shared.data.models.subscription.RejectAccessRequest
import lk.mageride.shared.data.models.subscription.RequestCreditTransferRequest
import lk.mageride.shared.data.models.subscription.RequestModeBAccessRequest
import lk.mageride.shared.data.models.subscription.SendCreditToDriverRequest
import lk.mageride.shared.data.models.subscription.SetSubscriberFareRequest
import lk.mageride.shared.data.models.subscription.SubscriberMonthStatus
import lk.mageride.shared.data.models.subscription.SubscriberRow
import lk.mageride.shared.data.models.subscription.SubscriberStatus
import lk.mageride.shared.data.models.subscription.Subscription
import lk.mageride.shared.data.models.subscription.SubscriptionBilling
import lk.mageride.shared.data.models.subscription.SubscriptionCycle
import lk.mageride.shared.data.models.subscription.SubscriptionPayMethod
import lk.mageride.shared.data.models.subscription.SubscriptionPayment
import lk.mageride.shared.data.models.subscription.SubscriptionPaymentStatus
import lk.mageride.shared.data.models.subscription.SubscriptionProviderCallback
import lk.mageride.shared.data.models.subscription.SubscriptionStatus
import lk.mageride.shared.data.models.subscription.TodaysDailyFee
import lk.mageride.shared.data.models.subscription.VoucherDiscountTier
import lk.mageride.shared.data.models.subscription.VoucherDiscountTierList
import lk.mageride.shared.data.models.subscription.VoucherPayMethod
import lk.mageride.shared.data.models.subscription.VoucherPurchase
import lk.mageride.shared.data.models.wallet.InitiateWalletCreditTransferRequest
import lk.mageride.shared.data.models.wallet.LankaqrTopupRequest
import lk.mageride.shared.data.models.wallet.OnepayTopupRequest
import lk.mageride.shared.data.models.wallet.Topup
import lk.mageride.shared.data.models.wallet.TopupCallback
import lk.mageride.shared.data.models.wallet.TopupState
import lk.mageride.shared.data.models.wallet.TransferDirection
import lk.mageride.shared.data.models.wallet.TransferRow
import lk.mageride.shared.data.models.wallet.TransferStatus
import lk.mageride.shared.data.models.wallet.VoucherDiscountTierUsage
import lk.mageride.shared.data.models.wallet.VoucherDiscountTierUsageList
import lk.mageride.shared.data.models.wallet.Wallet
import lk.mageride.shared.data.models.wallet.WalletTransaction
import kotlin.test.Test
import kotlin.test.assertEquals

/**
 * Round-trips every subscription-svc and wallet-svc DTO — everything that carries money.
 *
 * See [assertRoundTrips]. Each amount here is integer minor units; a `Double` anywhere in this
 * file would be a bug (C012 fence).
 */
class DtoRoundTripMoneyTest {

    // ---- subscription.yaml — daily fee -------------------------------------------------------

    @Test
    fun the_daily_fee_dtos_round_trip() {
        val rate = DailyFeeRate(
            vehicleType = VehicleType.THREE_WHEELER,
            dailyFeeMinor = 10_000,
            mode = ServiceMode.C,
            currency = Currency.LKR,
        )
        assertRoundTrips(rate)
        assertRoundTrips(DailyFeeRateList(listOf(rate)))
        assertRoundTrips(
            TodaysDailyFee(
                vehicleType = VehicleType.FLEX,
                dailyRateMinor = 15_000,
                status = DailyFeeDayStatus.UNPAID,
                deductedMinor = 0,
                tripsToday = 1,
                firstTripFree = true,
                feeDate = Sample.DAY,
                feeDateTzAt = Sample.AT,
            ),
        )
        assertRoundTrips(
            DailyFeeCharge(
                driverId = Sample.ULID_A,
                vehicleId = Sample.ULID_B,
                feeDate = Sample.DAY,
                feeDateTzAt = Sample.AT,
                amountMinor = 0,
                currency = Currency.LKR,
                tripsThatDay = 1,
                status = DailyFeeChargeStatus.WAIVED_FIRST_TRIP,
                chargedAt = Sample.AT,
            ),
        )
        assertRoundTrips(ChargeDailyFeeRequest(Sample.ULID_A, Sample.ULID_B))
    }

    // ---- subscription.yaml — credit and vouchers (AL-01) -------------------------------------

    @Test
    fun the_credit_transfer_dtos_round_trip() {
        val transfer = CreditTransfer(
            transferId = Sample.ULID_A,
            senderDriverId = Sample.ULID_B,
            recipientDriverId = Sample.ULID_C,
            amountMinor = 100_000,
            currency = Currency.LKR,
            direction = CreditTransferDirection.REQUESTED,
            status = CreditTransferStatus.PENDING,
            createdAt = Sample.AT,
        )
        assertRoundTrips(transfer)
        assertEquals(Money.ofMinor(100_000), transfer.money)
        assertRoundTrips(RequestCreditTransferRequest(Sample.ULID_B, 100_000))
        assertRoundTrips(SendCreditToDriverRequest(Sample.ULID_C, 100_000))
    }

    @Test
    fun the_voucher_dtos_round_trip_and_credit_the_full_face_value() {
        val tier = VoucherDiscountTier(
            denominationMinor = 100_000,
            discountBps = 1_000,
            active = true,
            updatedAt = Sample.AT,
        )
        assertRoundTrips(tier)
        assertRoundTrips(VoucherDiscountTierList(listOf(tier)))
        assertRoundTrips(PurchaseVoucherRequest(100_000, VoucherPayMethod.LANKAQR))

        // ck_voucher_credit_full (C005): the discount lives entirely in paidMinor.
        val purchase = VoucherPurchase(
            purchaseId = Sample.ULID_A,
            denominationMinor = 100_000,
            discountBpsApplied = 1_000,
            paidMinor = 90_000,
            creditedMinor = 100_000,
            currency = Currency.LKR,
            redirectUrl = Sample.URL,
            qrPayload = "00020101021230",
        )
        assertRoundTrips(purchase)
        assertEquals(purchase.denominationMinor, purchase.creditedMinor)
        assertEquals(Money.ofMinor(90_000), purchase.money)
    }

    // ---- subscription.yaml — Mode B (Epic 23) ------------------------------------------------

    @Test
    fun the_mode_b_access_dtos_round_trip() {
        assertRoundTrips(
            AccessRequest(
                requestId = Sample.ULID_A,
                vehicleId = Sample.ULID_B,
                passengerId = Sample.ULID_C,
                passengerName = "Kamala",
                passengerMobileMasked = Sample.PHONE_MASKED,
                status = AccessRequestStatus.ACCEPTED,
                createdAt = Sample.AT,
            ),
        )
        assertRoundTrips(RequestModeBAccessRequest("I take this van every morning"))
        assertRoundTrips(RejectAccessRequest("Vehicle is full"))
        assertRoundTrips(AccessRequestAccepted(Sample.ULID_A, Sample.ULID_B, Sample.ULID_C))
    }

    @Test
    fun the_mode_b_subscription_dtos_round_trip() {
        assertRoundTrips(
            Subscription(
                subscriptionId = Sample.ULID_A,
                vehicleId = Sample.ULID_B,
                passengerId = Sample.ULID_C,
                billing = SubscriptionBilling.PAID,
                monthlyFareMinor = 250_000,
                currency = Currency.LKR,
                cycle = SubscriptionCycle.JOIN_ANNIVERSARY,
                joinDay = 14,
                nextDue = Sample.MONTH,
                nextDueTzAt = Sample.AT,
                status = SubscriptionStatus.ACTIVE,
            ),
        )
        assertRoundTrips(
            SubscriberRow(
                subscriberId = Sample.ULID_A,
                passengerId = Sample.ULID_B,
                name = "Kamala",
                mobileMasked = Sample.PHONE_MASKED,
                billing = SubscriptionBilling.PAID,
                monthlyFareMinor = 250_000,
                currency = Currency.LKR,
                cycle = SubscriptionCycle.MONTH_FIRST,
                thisMonthStatus = SubscriberMonthStatus.PENDING_VERIFICATION,
                muted = true,
                status = SubscriberStatus.UNSUBSCRIBED,
            ),
        )
        assertRoundTrips(SetSubscriberFareRequest(250_000))
        assertRoundTrips(MarkSubscriberCashPaidRequest(250_000, Sample.MONTH))
    }

    @Test
    fun the_mode_b_payment_dtos_round_trip() {
        val payTo = PayTo(
            lankaqrImageUrl = Sample.URL,
            bank = "Commercial Bank",
            branch = "Kollupitiya",
            accountNo = "1000123456",
            accountHolderName = "Sunrise Transport",
        )
        assertRoundTrips(payTo)
        assertRoundTrips(
            SubscriptionPayment(
                paymentId = Sample.ULID_A,
                subscriptionId = Sample.ULID_B,
                method = SubscriptionPayMethod.LANKAQR_DEEPLINK,
                amountMinor = 250_000,
                currency = Currency.LKR,
                status = SubscriptionPaymentStatus.PAID,
                periodMonth = Sample.MONTH,
                periodMonthTzAt = Sample.AT,
                payTo = payTo,
                redirectUrl = Sample.URL,
                qrPayload = "00020101021230",
                slipUrl = Sample.URL,
                paidAt = Sample.LATER,
            ),
        )
        assertRoundTrips(
            PayModeBSubscriptionRequest(SubscriptionPayMethod.ONLINE_TRANSFER, Sample.MONTH),
        )
        assertRoundTrips(
            SubscriptionProviderCallback(
                providerTransactionId = "OP-99120",
                paymentId = Sample.ULID_A,
                status = ProviderCallbackStatus.PENDING,
                amountMinor = 250_000,
                currency = Currency.LKR,
                raw = JsonObject(mapOf("gatewayRef" to JsonPrimitive("OP-99120"))),
            ),
        )
    }

    // ---- wallet.yaml -------------------------------------------------------------------------

    @Test
    fun the_wallet_read_dtos_round_trip() {
        assertRoundTrips(
            Wallet(
                userId = Sample.ULID_A,
                balanceMinor = 120_000,
                availableMinor = 115_000,
                outstandingDebtMinor = 5_000,
                currency = Currency.LKR,
                updatedAt = Sample.AT,
            ),
        )
        assertRoundTrips(
            WalletTransaction(
                transactionId = Sample.ULID_A,
                entryId = Sample.ULID_B,
                kind = "daily_fee",
                amountMinor = -10_000,
                currency = Currency.LKR,
                balanceAfterMinor = 110_000,
                reference = "ride:${Sample.ULID_C}",
                occurredAt = Sample.AT,
            ),
        )
        assertRoundTrips(
            TransferRow(
                transferId = Sample.ULID_A,
                counterpartyDriverId = Sample.ULID_B,
                counterpartyName = "Sunil",
                amountMinor = 100_000,
                currency = Currency.LKR,
                direction = TransferDirection.RECEIVED,
                status = TransferStatus.APPROVED,
                createdAt = Sample.AT,
            ),
        )
        assertRoundTrips(InitiateWalletCreditTransferRequest(Sample.ULID_B, 100_000))
    }

    @Test
    fun the_topup_dtos_round_trip() {
        assertRoundTrips(OnepayTopupRequest(200_000, "mageride://wallet/return"))
        assertRoundTrips(LankaqrTopupRequest(200_000))
        assertRoundTrips(
            Topup(
                topupId = Sample.ULID_A,
                state = TopupState.Pending,
                amountMinor = 200_000,
                currency = Currency.LKR,
                redirectUrl = Sample.URL,
                sessionToken = "sess_abc",
                paymentLink = "lankaqr://pay?ref=abc",
                qrPayload = "00020101021230",
            ),
        )
        assertRoundTrips(
            TopupCallback(
                providerTransactionId = "OP-4411",
                topupId = Sample.ULID_A,
                status = ProviderCallbackStatus.FAILED,
                amountMinor = 200_000,
                currency = Currency.LKR,
                raw = JsonObject(mapOf("reason" to JsonPrimitive("declined"))),
            ),
        )
    }

    @Test
    fun the_admin_voucher_tier_usage_dtos_round_trip() {
        val usage = VoucherDiscountTierUsage(
            denominationMinor = 100_000,
            discountBps = 1_000,
            active = true,
            updatedAt = Sample.AT,
            purchaseCount = 42,
            purchasedValueMinor = 4_200_000,
        )
        assertRoundTrips(usage)
        assertRoundTrips(VoucherDiscountTierUsageList(listOf(usage)))
        assertEquals(
            VoucherDiscountTier(100_000, 1_000, active = true, updatedAt = Sample.AT),
            usage.toTier(),
        )
    }
}
