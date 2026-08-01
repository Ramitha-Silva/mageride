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

    /// <summary>
    /// The AL-57 card rail: a prepaid balance, spent as one balanced ledger entry.
    /// </summary>
    /// <remarks>
    /// Card acceptance did not survive as a *ride* rail — OnePay has one merchant account per
    /// merchant, so a card fare could only ever land in MageRide's own account. It survives one step
    /// earlier: the passenger tops their wallet up through wallet-svc, where MageRide legitimately
    /// is the payee, and this method spends it. There is no gateway leg, so no <c>Pending</c>.
    /// </remarks>
    public const string Wallet = "wallet";

    /// <summary>
    /// <b>RETIRED as a ride method (AL-59).</b> Historical rows only.
    /// </summary>
    /// <remarks>
    /// This pointed at <c>LankaQr:MerchantId</c> — the <em>platform's</em> merchant — so it collected
    /// fares into MageRide's account while crediting the driver nothing but a
    /// <c>fares.driver_earnings</c> read-model row. A LankaQR ride payment is now the driver's OWN
    /// bank QR and is <see cref="ScanDriverQr"/>, which settles by AL-47 attestation precisely
    /// because money moving into somebody else's bank produces no platform webhook.
    /// </remarks>
    public const string LankaQr = "lankaqr";

    /// <summary><b>RETIRED as a ride method (AL-57).</b> Historical rows only — see <see cref="Wallet"/>.</summary>
    public const string Onepay = "onepay";

    /// <summary>Package delivery, booking-time (P-08).</summary>
    public const string Cod = "cod";

    /// <summary>Settlement-time, chosen when the passenger scans the driver's own QR (AL-22/AL-47).</summary>
    public const string ScanDriverQr = "scan_driver_qr";

    /// <summary>
    /// Every value <c>fares.ride_payments.method</c> admits — <b>including the two AL-57/AL-59
    /// retired.</b>
    /// </summary>
    /// <remarks>
    /// This mirrors the database CHECK, and the CHECK still admits them deliberately: a row saying
    /// somebody paid by OnePay in July is a fact, and a CHECK cannot express "nothing writes this
    /// any more" without rewriting history. What stops a new one is that no route, no config and no
    /// contract enum can produce one — see <c>PaymentService.PayableMethods</c>.
    /// </remarks>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Cash, Wallet, LankaQr, Onepay, Cod, ScanDriverQr,
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
