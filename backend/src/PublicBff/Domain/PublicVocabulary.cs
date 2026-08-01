namespace MageRide.PublicBff.Domain;

/// <summary>
/// The four <c>safety.trip_share_tokens.scope</c> values (migration 0901).
/// </summary>
/// <remarks>
/// <b>This service issues none of them and burns one.</b> notification-svc (C051) mints
/// <c>package_recipient</c>, <c>proxy_rider</c> and <c>pickup_confirm</c> and puts each straight
/// into an SMS; safety-svc (C052) issues <c>trip_view</c> and revokes every scope on trip end. What
/// is left is BR-29.1's burn, which happens here because it happens when the token is *used*.
/// </remarks>
public static class ShareTokenScopes
{
    /// <summary>D-34's trip share. <b>Not served here</b> — safety-svc's own public view answers it.</summary>
    public const string TripView = "trip_view";

    /// <summary>P-09 / AL-21: the unregistered package recipient (SCR-WT-002).</summary>
    public const string PackageRecipient = "package_recipient";

    /// <summary>AL-44 / US-8.22: the proxy rider (SCR-WT-004).</summary>
    public const string ProxyRider = "proxy_rider";

    /// <summary>AL-45: the unregistered rider being asked where they are (SCR-WT-003). TTL 300 s.</summary>
    public const string PickupConfirm = "pickup_confirm";

    /// <summary>The three scopes <c>/public/track/**</c> serves.</summary>
    public static bool IsPublicSurface(string? scope) =>
        scope is PackageRecipient or ProxyRider or PickupConfirm;
}

/// <summary>
/// The four steps SCR-WT-002 draws (D3' Δ 2026-07-05, US-20.5).
/// </summary>
/// <remarks>
/// <b>Three of the four are ride states and the fourth is a position.</b> ADD Appendix B.2
/// invariant 6 is literal — a package traverses the same eighteen states a passenger ride does and
/// adds none — so <c>PickedUp</c> and <c>InTransit</c> are both <c>InProgress</c> as far as
/// <c>rides.rides</c> is concerned. The fact that separates them is whether the vehicle has left the
/// sender, which is observable: see <c>PackageProgress</c>. Inventing a nineteenth state to carry
/// it, or reporting a step no fact supports, were the two alternatives.
/// </remarks>
public static class PackageStatuses
{
    /// <summary>The parcel is not aboard yet — everything up to and including <c>DriverArrived</c>.</summary>
    public const string PickupPending = "PickupPending";

    /// <summary>Aboard, and the vehicle is still at the sender (or its position is unknown).</summary>
    public const string PickedUp = "PickedUp";

    /// <summary>Aboard and away from the sender.</summary>
    public const string InTransit = "InTransit";

    /// <summary>The ride reached <c>Completed</c> or beyond.</summary>
    public const string Delivered = "Delivered";
}

/// <summary>
/// How a handoff was evidenced (US-25.6, SCR-WT-005).
/// </summary>
/// <remarks>
/// <b>Money outranks evidence, and a dispute outranks both.</b> P-14's uncollected-COD dispute is a
/// terminal of the ride; P-08's collection is a fact about the cash; P-10's photograph is a fact
/// about the doorstep. A COD parcel that was both photographed and paid for reports the payment,
/// because that is the question the receipt is opened to answer.
/// </remarks>
public static class ReceiptProofs
{
    /// <summary>The recipient read the delivery code out to the driver (P-07).</summary>
    public const string OtpVerified = "otp_verified";

    /// <summary>The recipient was absent and the driver photographed the handoff (P-10).</summary>
    public const string PhotoProof = "photo_proof";

    /// <summary>Cash reached the driver (P-08).</summary>
    public const string CodCollected = "cod_collected";

    /// <summary>P-14: more than 24 h uncollected, or the ride reached <c>Disputed</c> another way.</summary>
    public const string Disputed = "disputed";
}

/// <summary>
/// The <c>rides.rides.state</c> values this surface treats as the end of the journey.
/// </summary>
public static class RideStates
{
    public const string Accepted = "Accepted";
    public const string DriverArrived = "DriverArrived";
    public const string InProgress = "InProgress";
    public const string Completed = "Completed";
    public const string PaymentPending = "PaymentPending";
    public const string Paid = "Paid";
    public const string CashSettled = "CashSettled";
    public const string CashOnDeliveryCollected = "CashOnDeliveryCollected";
    public const string Disputed = "Disputed";

    /// <summary>
    /// The states in which the parcel is aboard or delivered.
    /// </summary>
    /// <remarks>
    /// <c>Completed</c> is included and is <em>not</em> a claim that the ride is over: C004's note
    /// (b) and ride-svc's own rule are that the ride passes through it to <c>PaymentPending</c> in
    /// one transaction. For a recipient watching a parcel, "the driver has handed it over" is
    /// exactly what that instant means.
    /// </remarks>
    public static bool ParcelDelivered(string? state) =>
        state is Completed or PaymentPending or Paid or CashSettled or CashOnDeliveryCollected or Disputed;

    /// <summary>The parcel is aboard: the pickup code has been consumed and the ride is running.</summary>
    public static bool ParcelAboard(string? state) => state is InProgress;

    /// <summary>
    /// The terminals a receipt exists for.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately narrower than "terminal".</b> D5' §6 has ten terminal states and six of them
    /// are cancellations and no-shows — a journey that did not happen has no receipt, and answering
    /// one with <c>proof: otp_verified</c> would be a record of a handoff that never took place.
    /// SCR-WT-005 is headed "Delivered / Trip Summary" for the same reason. Asking earlier is
    /// <c>409 receipt-not-ready</c>; a cancelled ride's token is closed by safety-svc's trip-end
    /// hook, so in practice the page reaches SCR-WT-006 instead.
    /// </remarks>
    public static bool Receiptable(string? state) =>
        state is Paid or CashSettled or CashOnDeliveryCollected or Disputed;

    /// <summary>
    /// <c>PaymentPending</c> onwards — the window in which the *ride* is over even though the money
    /// may not be.
    /// </summary>
    public static bool JourneyOver(string? state) =>
        state is PaymentPending or Paid or CashSettled or CashOnDeliveryCollected or Disputed;
}

/// <summary>Who settles a proxy ride's fare — the only thing a rider is told about the money (P-09).</summary>
public static class FarePayers
{
    /// <summary>The booker's instrument settles it. <b>Which instrument is never said.</b></summary>
    public const string Booker = "booker";

    /// <summary>US-8.21: the booker chose cash, so the rider owes the driver at the end.</summary>
    public const string CashDue = "cash_due";
}
