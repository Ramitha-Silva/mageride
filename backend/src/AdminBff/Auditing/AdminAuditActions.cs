namespace MageRide.AdminBff.Auditing;

/// <summary>
/// The <c>audit.events</c> vocabulary of the Admin Portal (D-35, US-19.3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Screaming snake, because that is the spelling the spec already uses.</b>
/// <c>server_db_schema.md</c> §23 names the two read-access actions <c>DOC_VIEW</c> and
/// <c>PII_READ</c>; every action here follows them so an auditor filtering
/// <c>GET /v1/admin/audit-log?action=…</c> has one convention to learn. The dotted form
/// (<c>mqtt.rate_violation</c>) is the *topic* vocabulary of D6' §2.1 and belongs to facts
/// devices produce, not to admin decisions.
/// </para>
/// <para>
/// <b>Named constants and not free text at the call site</b>, because an action string that
/// differs by one character between the route that writes it and the screen that filters on it is
/// a gap in an immutable log nobody notices until somebody goes looking for the row.
/// </para>
/// </remarks>
public static class AdminAuditActions
{
    // -------------------------------------------------------------------------------------------
    // Moderation
    // -------------------------------------------------------------------------------------------

    /// <summary>A vehicle was taken out of dispatch and off the map (US-14.3).</summary>
    public const string VehicleSuspended = "VEHICLE_SUSPENDED";

    /// <summary>A driver was blocked and their live session ended (US-14.3).</summary>
    public const string DriverSuspended = "DRIVER_SUSPENDED";

    /// <summary>A passenger report was upheld. The third one delists the vehicle (US-12.6).</summary>
    public const string ReportConfirmed = "REPORT_CONFIRMED";

    /// <summary>A passenger report was found unsubstantiated (US-12.6).</summary>
    public const string ReportDismissed = "REPORT_DISMISSED";

    /// <summary>A support ticket was answered and closed (US-16.3).</summary>
    public const string TicketResolved = "TICKET_RESOLVED";

    // -------------------------------------------------------------------------------------------
    // Verification (AL-39, C063)
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// A document was opened in the full-size viewer (AL-39, US-24.8, SCR-AP-003b).
    /// </summary>
    /// <remarks>
    /// <b>The one action on this surface a GET writes.</b> <c>server_db_schema.md</c> §23 names it,
    /// and looking at somebody's licence is itself the auditable act — which is why the viewer route
    /// is what mints the signed object URL rather than the detail read handing one out.
    /// </remarks>
    public const string DocumentViewed = "DOC_VIEW";

    /// <summary>A flagged field was confirmed as read, or corrected and confirmed (US-2.4a/2.10a).</summary>
    public const string FieldConfirmed = "VERIFICATION_FIELD_CONFIRMED";

    /// <summary>A driver, vehicle or fleet organisation passed verification (US-2.9, US-13.A7).</summary>
    public const string VerificationApproved = "VERIFICATION_APPROVED";

    /// <summary>A driver, vehicle or fleet organisation was refused, with a reason (US-2.15).</summary>
    public const string VerificationRejected = "VERIFICATION_REJECTED";

    /// <summary>
    /// An officer withdrew their own refusal so a resubmission could be judged again.
    /// </summary>
    /// <remarks>
    /// Its own action rather than part of the approval that follows it. registry-svc will not
    /// auto-approve a REJECTED vehicle — "a Verification Officer's decision that four green steps do
    /// not overturn" — so reopening is a separate decision, and recording it separately is what
    /// keeps the trail honest when the approval that follows is itself refused by AL-10.
    /// </remarks>
    public const string VerificationReopened = "VERIFICATION_REOPENED";

    /// <summary>
    /// A driver's bank &amp; payout profile approved (AL-58) — <b>a different fact from
    /// <see cref="VerificationApproved"/> on the same id</b>.
    /// </summary>
    /// <remarks>
    /// Its own action because it is its own claim. VERIFICATION_APPROVED on a driver says an officer
    /// checked a licence; this says they checked a bank statement against an account number and
    /// authorised money to be sent there. An auditor asking "who approved paying this driver into
    /// this account" must not have to infer it from a decision about a photograph.
    /// </remarks>
    public const string PayoutProfileApproved = "PAYOUT_PROFILE_APPROVED";

    /// <summary>A driver's bank &amp; payout profile refused, with the reason they are shown.</summary>
    public const string PayoutProfileRejected = "PAYOUT_PROFILE_REJECTED";

    // -------------------------------------------------------------------------------------------
    // Directories (AL-40/41/42, C064)
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// A directory detail was opened (AL-40/41/42, US-24.9/10/11, I-28.6).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The second action on this surface a GET writes</b>, and `server_db_schema.md` §23 names it
    /// alongside <see cref="DocumentViewed"/> for exactly that reason: opening somebody's record is
    /// itself the auditable act, whether or not anything changed.
    /// </para>
    /// <para>
    /// <b>One action for all three directories.</b> §23 introduces it as "passenger/driver directory
    /// detail opened" and D3' marks the two people routes, but URD §2.3's privacy clause requires a
    /// read-access entry for "all passenger/driver/**vehicle** directory lookups" — and a vehicle
    /// detail resolves to a named owner and an organisation. A second action for the third subject
    /// would split one auditor question across two filters; <c>entity_type</c> already says which
    /// kind of record was opened. Recorded as a micro-change-set.
    /// </para>
    /// </remarks>
    public const string PiiRead = "PII_READ";

    // -------------------------------------------------------------------------------------------
    // Finance (E-05, R-19, US-14.11, C065)
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// A daily-fee deduction was reversed onto a driver's wallet (US-14.11).
    /// </summary>
    /// <remarks>
    /// <b>Its own action, and not <see cref="RefundIssued"/>.</b> A refund gives a passenger back
    /// money they paid a gateway; a reversal puts credit onto a driver's prepaid balance because
    /// MageRide charged them a fee it should not have. Different payer, different rail, different
    /// URD §2.3 row — and an auditor asking "how much did we hand back to drivers last month" must
    /// not have to subtract one from the other.
    /// </remarks>
    public const string FeeReversed = "WALLET_FEE_REVERSED";

    /// <summary>A full, partial or overpaid-reversal refund was raised through fare-svc (E-05).</summary>
    /// <remarks>
    /// The route-level fact. fare-svc writes the <c>fares.refunds</c> row and the balanced
    /// <c>payment_refund</c> / <c>overpaid_reversal</c> journal entry inside its own transaction —
    /// two rows for one action, which is the right failure: this one survives the refund row being
    /// purged, that one survives this route being renamed.
    /// </remarks>
    public const string RefundIssued = "REFUND_ISSUED";

    // -------------------------------------------------------------------------------------------
    // PDPA (E-06, C065)
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// A data subject asked for an export or an erasure (E-06, US-1.8).
    /// </summary>
    /// <remarks>
    /// <b>The one action on this surface an end user's own request writes.</b> Every other row in
    /// <c>audit.events</c> is an operator's decision, and D-35 is about operator decisions — but a
    /// statutory 30-day clock that starts when somebody presses a button in the Passenger App needs
    /// the button press to be in the same immutable log the fulfilment lands in, or the platform can
    /// prove it acted and cannot prove when it was asked. <c>actor_role</c> is therefore
    /// <c>passenger</c> or <c>driver</c> on these rows and nothing else on the surface.
    /// </remarks>
    public const string PdpaRequested = "PDPA_REQUESTED";

    /// <summary>An export was delivered, or an erasure carried out (E-06).</summary>
    /// <remarks>
    /// One action for both outcomes — <c>Fulfilled</c> and <c>FulfilledHold</c> — because they are
    /// the same decision and the <c>after</c> image carries which one it was together with the
    /// statutory holds that were honoured. That is the opposite of the report queue's
    /// confirm/dismiss split, and for the opposite reason: there the two verdicts are what an
    /// auditor counts, here the question is "was the obligation met", and a hold is a fulfilment.
    /// </remarks>
    public const string PdpaFulfilled = "PDPA_FULFILLED";

    /// <summary>A request was refused outright, with the reason the subject is shown (E-06).</summary>
    public const string PdpaRejected = "PDPA_REJECTED";

    // -------------------------------------------------------------------------------------------
    // Configuration
    // -------------------------------------------------------------------------------------------

    /// <summary>A new Mode C tariff version was published (US-14.4).</summary>
    public const string TariffsPublished = "TARIFFS_PUBLISHED";

    /// <summary>A launch city was added (AL-27).</summary>
    public const string CityCreated = "CITY_CREATED";

    /// <summary>A launch city was edited or deactivated (AL-27).</summary>
    public const string CityUpdated = "CITY_UPDATED";

    /// <summary>A platform feature flag was set (US-14.12).</summary>
    public const string FeatureFlagSet = "FEATURE_FLAG_SET";

    /// <summary>A train was registered — admin-only Mode A (US-2.17).</summary>
    public const string TrainCreated = "TRAIN_CREATED";

    /// <summary>A train's details changed (US-2.18).</summary>
    public const string TrainUpdated = "TRAIN_UPDATED";

    /// <summary>A train was retired. Soft — historical trips keep their reference (US-2.18).</summary>
    public const string TrainRetired = "TRAIN_RETIRED";

    /// <summary>A broadcast announcement went out (US-14.8).</summary>
    public const string AnnouncementPublished = "ANNOUNCEMENT_PUBLISHED";

    /// <summary>A GTFS Dataset Manager call was forwarded to transit-svc (AL-54).</summary>
    /// <remarks>
    /// The route-level fact. transit-svc writes the dataset-level ones
    /// (<c>GTFS_FEED_UPLOADED</c> / <c>_VALIDATED</c> / <c>_ACTIVATED</c>) inside the transaction
    /// that changes the feed — two rows for one action, which is the right failure: this one
    /// survives the feed being deleted, that one survives the route being renamed.
    /// </remarks>
    public const string GtfsProxied = "GTFS_PROXIED";

    // -------------------------------------------------------------------------------------------
    // Entity types
    // -------------------------------------------------------------------------------------------

    public const string VehicleEntity = "vehicle";
    public const string DriverEntity = "driver";

    /// <summary>
    /// An end-user account read through the passenger directory (AL-40).
    /// </summary>
    /// <remarks>
    /// Not <see cref="DriverEntity"/> and not a bare "user": the two people-directories are two
    /// screens over two populations, and an auditor asking "who has been reading riders' numbers"
    /// must be able to ask it without also getting every driver lookup.
    /// </remarks>
    public const string PassengerEntity = "passenger";

    /// <summary>A fleet organisation — <c>registry.fleets</c> (AL-03, AL-49).</summary>
    public const string FleetOrgEntity = "fleet_org";

    /// <summary>One document, whichever of the two tables holds it (AL-39).</summary>
    public const string DocumentEntity = "document";

    /// <summary>
    /// <c>registry.driver_payout_profiles</c>. Not <see cref="DriverEntity"/>: the row that changed
    /// is the payout profile, and an audit trail that called both "driver" would make the two
    /// decisions on one id indistinguishable when read back.
    /// </summary>
    public const string PayoutProfileEntity = "driver_payout_profile";

    /// <summary>
    /// A driver's wallet — <c>billing.accounts</c> with <c>owner_type='driver'</c>.
    /// </summary>
    /// <remarks>
    /// Not <see cref="DriverEntity"/>: a fee reversal is a fact about the balance, and an audit
    /// trail that called it "driver" would make it indistinguishable from a suspension of the same
    /// person. The id recorded is still the driver's, because that is the only id the operator, the
    /// route and the ledger all agree on — <c>billing.accounts.id</c> is wallet-svc's and appears on
    /// no screen.
    /// </remarks>
    public const string WalletEntity = "driver_wallet";

    /// <summary>One <c>fares.ride_payments</c> attempt — what a refund is raised against (E-05).</summary>
    public const string PaymentEntity = "ride_payment";

    /// <summary><c>pdpa.requests</c> — one data-subject request (E-06).</summary>
    public const string PdpaRequestEntity = "pdpa_request";

    public const string ReportEntity = "vehicle_report";
    public const string TicketEntity = "support_ticket";
    public const string TariffEntity = "fare_tariff";
    public const string CityEntity = "operating_city";
    public const string FeatureFlagEntity = "feature_flag";
    public const string BroadcastEntity = "broadcast";
    public const string GtfsEntity = "gtfs_feed";
}
