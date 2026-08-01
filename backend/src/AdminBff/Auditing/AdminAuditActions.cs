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

    /// <summary>A fleet organisation — <c>registry.fleets</c> (AL-03, AL-49).</summary>
    public const string FleetOrgEntity = "fleet_org";

    /// <summary>One document, whichever of the two tables holds it (AL-39).</summary>
    public const string DocumentEntity = "document";

    public const string ReportEntity = "vehicle_report";
    public const string TicketEntity = "support_ticket";
    public const string TariffEntity = "fare_tariff";
    public const string CityEntity = "operating_city";
    public const string FeatureFlagEntity = "feature_flag";
    public const string BroadcastEntity = "broadcast";
    public const string GtfsEntity = "gtfs_feed";
}
