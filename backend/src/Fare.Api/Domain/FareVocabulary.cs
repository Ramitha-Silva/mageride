namespace MageRide.Fare.Domain;

/// <summary>
/// <c>fares.ride_payments.state</c> (migration 1002) — D-10's machine, in full.
/// </summary>
/// <remarks>
/// <b>C049 writes exactly one of these and reads the rest.</b> Every transition out of
/// <see cref="Initiated"/> is C050's; the names are here because the row this component creates has
/// to name its own state, and a service that spelled it as a literal would be one refactor away
/// from writing a state the CHECK refuses.
/// </remarks>
public static class RidePaymentStates
{
    /// <summary>The only state C049 writes: a fare has been computed and is owed.</summary>
    public const string Initiated = "Initiated";

    public const string Pending = "Pending";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
    public const string Retried = "Retried";
    public const string FellBackToCash = "FellBackToCash";
    public const string CashOnDelivery = "CashOnDelivery";
    public const string CashOnDeliveryCollected = "CashOnDeliveryCollected";
    public const string Overpaid = "Overpaid";
    public const string Refunded = "Refunded";
    public const string PartiallyRefunded = "PartiallyRefunded";
    public const string Disputed = "Disputed";
    public const string QrClaimedByPassenger = "QrClaimedByPassenger";
    public const string DriverConfirmedQR = "DriverConfirmedQR";

    /// <summary>
    /// The states R-05 counts as terminal — the ones on which a driver's earning posts.
    /// </summary>
    /// <remarks>
    /// <c>Disputed</c> is a terminal of the <em>ride</em> and not of the money (ride-svc's C037 note
    /// says the same about <c>earningPayable</c>), so it is absent: a disputed fare has not been
    /// earned until Finance says it has.
    /// </remarks>
    public static readonly IReadOnlySet<string> Terminal = new HashSet<string>(StringComparer.Ordinal)
    {
        Succeeded, FellBackToCash, CashOnDeliveryCollected, DriverConfirmedQR,
    };
}

/// <summary><c>fares.ride_payments.method</c> (migration 1002, AL-22).</summary>
public static class RidePaymentMethods
{
    public const string Cash = "cash";
    public const string LankaQr = "lankaqr";
    public const string Onepay = "onepay";

    /// <summary>Package delivery, booking-time (P-08).</summary>
    public const string Cod = "cod";

    /// <summary>Settlement-time, chosen when the passenger scans the driver's own QR (AL-22/AL-47).</summary>
    public const string ScanDriverQr = "scan_driver_qr";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Cash, LankaQr, Onepay, Cod, ScanDriverQr,
    };
}

/// <summary><c>fares.ride_payments.payer_role</c> — P-04's proxy routing.</summary>
/// <remarks>
/// <b>Cash is always paid by the rider; LankaQR and OnePay are always charged to the booker.</b>
/// That is C050's fence and it is quoted here because C049 writes the column: on a proxy booking the
/// person in the car and the person paying are different people, and the row has to say which.
/// </remarks>
public static class PayerRoles
{
    public const string Rider = "rider";
    public const string Booker = "booker";
}

/// <summary>The <c>rides.rides.state</c> values this service reacts to (migration 0601).</summary>
public static class RideStates
{
    public const string InProgress = "InProgress";
    public const string Completed = "Completed";
    public const string PaymentPending = "PaymentPending";

    /// <summary>
    /// The states a fare may be computed from.
    /// </summary>
    /// <remarks>
    /// ride-svc moves a ride through <c>Completed</c> to <c>PaymentPending</c> inside one
    /// transaction (its C032 note: "Completed is not terminal"), so a ride reaching this service is
    /// normally <c>PaymentPending</c>. Both are admitted because which one a caller observes depends
    /// on whether it read the ride before or after that commit, and neither is wrong.
    /// </remarks>
    public static readonly IReadOnlySet<string> Priceable = new HashSet<string>(StringComparer.Ordinal)
    {
        Completed, PaymentPending,
    };
}

/// <summary><c>rides.rides.kind</c> (migration 0601) — 0 passenger, 1 proxy, 2 package.</summary>
public static class RideKinds
{
    public const short Passenger = 0;
    public const short Proxy = 1;
    public const short Package = 2;

    /// <summary>The <c>kind</c> query parameter <c>GET /v1/fare/estimate</c> takes.</summary>
    public const string PassengerQuote = "passenger";
    public const string PackageQuote = "package";
}
