using MageRide.AdminBff.Auditing;
using MageRide.AdminBff.Domain;
using MageRide.AdminBff.Endpoints;
using MageRide.AdminBff.Persistence;
using MageRide.AdminBff.Verification;
using MageRide.Shared.Errors;
using MageRide.Shared.Primitives;

namespace MageRide.AdminBff.Directories;

/// <summary>
/// SCR-AP-010…015 — the passenger, driver and vehicle directories (AL-40/41/42, US-24.9/10/11).
/// </summary>
/// <remarks>
/// <para>
/// <b>Read-only, and that is a fence rather than a description.</b> BR-28.8: "All are read-only:
/// refunds route to Finance (US-14.13) and wallet reversals stay Finance-only (US-9A.15)." So there
/// is no verb here but GET, no repository method that writes a directory table, and the one row this
/// component writes on a directory request is its own <c>audit.events</c> row. The reversal an
/// operator reaches from a driver's wallet tab is C065's
/// <c>POST /v1/admin/drivers/wallet/{driverId}/reverse-fee</c>, gated on the Finance row.
/// </para>
/// <para>
/// <b>Read models, never a service's write path.</b> The queries join tables owned by ride-svc,
/// trip-state-svc, fare-svc, wallet-svc, subscription-svc, safety-svc, support-svc and registry-svc
/// (I-28.6 names four of them). None of those services exposes a "give me this person's whole
/// history" route, and asking eight of them per detail open would make one screen eight failure
/// modes — so the join happens here, against the same Postgres, and writes nothing.
/// </para>
/// <para>
/// <b>One detail open is exactly one <c>PII_READ</c> row.</b> The handler records once, on the way
/// out of a successful read, and the D-35 interceptor writes it — the same mechanism AL-39's
/// <c>DOC_VIEW</c> uses. A 404 records nothing (there was no subject to look at), a 403 never
/// reaches the handler, and the row carries whether the caller actually saw the contact details,
/// because "who has seen this person's number" is the question the log exists to answer.
/// </para>
/// <para>
/// <b>The vehicle detail writes one too.</b> `server_db_schema.md` §23 introduces <c>PII_READ</c> as
/// "passenger/driver directory detail opened" and D3' marks only those two routes, but URD §2.3's
/// privacy clause is broader in as many words — "All passenger/driver/**vehicle** directory lookups
/// … write a read-access audit entry (actor, target, timestamp)" — and a vehicle detail resolves to
/// a named owner and an organisation. Inventing a second action for it would split one auditor
/// question across two filters; leaving it unaudited would drop a row the URD asks for. Recorded as
/// a micro-change-set.
/// </para>
/// </remarks>
public interface IDirectoryService
{
    Task<CursorPage<PassengerRowResponse>> SearchPassengersAsync(
        string? name, string? mobile, Guid? id, string? email, PageRequest page, CancellationToken cancellationToken);

    Task<PassengerDetailResponse> PassengerDetailAsync(
        Guid passengerId, HttpContext context, CancellationToken cancellationToken);

    Task<CursorPage<DriverRowResponse>> SearchDriversAsync(
        string? name,
        string? mobile,
        Guid? id,
        string? nic,
        string? regNo,
        int? level,
        string status,
        PageRequest page,
        CancellationToken cancellationToken);

    Task<DriverDetailResponse> DriverDetailAsync(
        Guid driverId, HttpContext context, CancellationToken cancellationToken);

    Task<CursorPage<VehicleRowResponse>> SearchVehiclesAsync(
        string? regNo,
        Guid? id,
        string? type,
        string? mode,
        string? ownerMobile,
        string? fleetOrg,
        string? status,
        PageRequest page,
        CancellationToken cancellationToken);

    Task<AdminVehicleDetailResponse> VehicleDetailAsync(
        Guid vehicleId, HttpContext context, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IDirectoryService"/>
internal sealed class DirectoryService(
    IDirectoryRepository directories,
    IDocumentLinks links,
    IPiiPolicy pii,
    IAdminAuditContext audit,
    TimeProvider clock) : IDirectoryService
{
    /// <summary>
    /// How many rows each tab of a detail carries.
    /// </summary>
    /// <remarks>
    /// <b>No spec, and not a setting.</b> D3' gives the tabs no pagination and SCR-AP-011/013/015
    /// draw them as panels rather than as pages, so the choice is between "the recent ones" and
    /// "all of them" — and a driver three years into the platform has tens of thousands of wallet
    /// rows, which is a detail read that times out and a screen nobody can scroll. Fifty is a panel
    /// somebody reads. An operator who needs the whole ledger has the audit log and, for money, the
    /// Finance surface; a knob would be a promise this component could serve an unbounded read.
    /// </remarks>
    private const int TabRows = 50;

    /// <summary>
    /// How long a tracker may be silent and still count as online (US-3.13).
    /// </summary>
    /// <remarks>
    /// The same 30 minutes fleet-health (C044) defaults <c>Health:OfflineAfter</c> to, and not a
    /// second knob: a vehicle that the fleet-health screen calls offline and the vehicle directory
    /// calls online would be two answers to one question, and an operator comparing the two screens
    /// would have no way to know which is lying.
    /// </remarks>
    private static readonly TimeSpan TrackerOfflineAfter = TimeSpan.FromMinutes(30);

    // ---------------------------------------------------------------------------------------
    // Passengers (AL-40)
    // ---------------------------------------------------------------------------------------

    public async Task<CursorPage<PassengerRowResponse>> SearchPassengersAsync(
        string? name, string? mobile, Guid? id, string? email, PageRequest page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);

        var (cursorAt, cursorId) = QueueCursors.Decode(page.Cursor);

        var rows = await directories.SearchPassengersAsync(
            new PassengerSearchQuery(id, name, mobile, email, cursorAt, cursorId, page.OverfetchLimit),
            cancellationToken);

        return CursorPage<PassengerRow>
            .FromOverfetch(rows, page.Limit, row => QueueCursors.Encode(row.JoinedAt, row.PassengerId))
            .Select(row => new PassengerRowResponse(
                row.PassengerId,
                row.Name,
                // Masked for every caller. The clear number is only ever handed out by the audited
                // detail read — see IPiiPolicy.
                PiiView.MaskMsisdn(row.Mobile),
                row.Trips,
                row.JoinedAt,
                row.Status));
    }

    public async Task<PassengerDetailResponse> PassengerDetailAsync(
        Guid passengerId, HttpContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var profile = await directories.FindPassengerAsync(passengerId, cancellationToken)
            ?? throw new MageRideException(MageRideErrors.NotFound, $"No passenger {passengerId}.");

        var tabs = await directories.PassengerTabsAsync(passengerId, TabRows, cancellationToken);
        var view = pii.For(context.User);

        Record(passengerId, AdminAuditActions.PassengerEntity, view, tabs.Trips.Count + tabs.Packages.Count);

        return new PassengerDetailResponse(
            new PassengerProfileResponse(
                profile.PassengerId,
                profile.Name,
                view.Mobile(profile.Mobile),
                view.Email(profile.Email),
                profile.JoinedAt,
                profile.Rating,
                profile.DefaultPay,
                profile.Status,
                [
                    // An emergency contact is somebody else's number on this person's account
                    // (AL-13, P-02) and is masked by the same rule: an operator who may not see the
                    // passenger's own number has no better claim on their next of kin's.
                    .. tabs.SosContacts.Select(contact =>
                        new SosContactResponse(contact.Name, view.Mobile(contact.Phone))),
                ]),
            [.. tabs.Trips.Select(Trip)],
            [
                .. tabs.Payments.Select(payment => new PaymentResponse(
                    payment.PaymentId,
                    payment.RideId,
                    payment.Method,
                    payment.State,
                    payment.AmountMinor,
                    payment.SurchargeMinor,
                    payment.TipMinor,
                    payment.Currency,
                    payment.AttemptNo,
                    payment.CreatedAt)),
            ],
            [
                .. tabs.Packages.Select(package => new PackageResponse(
                    package.RideId,
                    package.State,
                    package.PackageSize,
                    package.Description,
                    package.RecipientName,
                    view.Mobile(package.RecipientPhone),
                    package.FareMinor,
                    package.Currency,
                    package.CreatedAt,
                    package.TerminalAt)),
            ],
            [
                .. tabs.Disputes.Select(dispute => new DisputeResponse(
                    dispute.TicketId,
                    dispute.Category,
                    dispute.Status,
                    dispute.Description,
                    dispute.Response,
                    dispute.RideId,
                    dispute.CreatedAt,
                    dispute.UpdatedAt)),
            ]);
    }

    // ---------------------------------------------------------------------------------------
    // Drivers (AL-41)
    // ---------------------------------------------------------------------------------------

    public async Task<CursorPage<DriverRowResponse>> SearchDriversAsync(
        string? name,
        string? mobile,
        Guid? id,
        string? nic,
        string? regNo,
        int? level,
        string status,
        PageRequest page,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);

        var (cursorAt, cursorId) = QueueCursors.Decode(page.Cursor);

        var rows = await directories.SearchDriversAsync(
            new DriverSearchQuery(
                id, name, mobile, nic, regNo, level, status, cursorAt, cursorId, page.OverfetchLimit),
            cancellationToken);

        return CursorPage<DriverRow>
            .FromOverfetch(rows, page.Limit, row => QueueCursors.Encode(row.JoinedAt, row.DriverId))
            .Select(row => new DriverRowResponse(
                row.DriverId,
                row.Name,
                PiiView.MaskMsisdn(row.Mobile),
                row.Vehicles ?? [],
                row.Level,
                row.Trips,
                row.Status));
    }

    public async Task<DriverDetailResponse> DriverDetailAsync(
        Guid driverId, HttpContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // `not-found` and not `driver-not-found`, because `admin-bff.yaml#getDriverDetail` declares
        // that code and the contract wins. The distinct code exists for a route that takes a driver
        // *and* a vehicle (US-13.2's assignment) and has to say which half was wrong; on a path
        // whose only parameter is the driver there is nothing to disambiguate.
        var profile = await directories.FindDriverAsync(driverId, cancellationToken)
            ?? throw new MageRideException(MageRideErrors.NotFound, $"No driver {driverId}.");

        var tabs = await directories.DriverTabsAsync(driverId, TabRows, cancellationToken);
        var view = pii.For(context.User);

        Record(driverId, AdminAuditActions.DriverEntity, view, tabs.Trips.Count);

        return new DriverDetailResponse(
            new DriverProfileResponse(
                profile.DriverId,
                profile.Name,
                view.Mobile(profile.Mobile),
                view.Nic(profile.Nic),
                profile.JoinedAt,
                profile.Rating,
                profile.WalletMinor,
                profile.Currency,
                profile.Level,
                profile.Points,
                profile.Status,
                profile.VerifiedAt),
            [
                .. tabs.Vehicles.Select(vehicle => new LinkedVehicleResponse(
                    vehicle.VehicleId,
                    vehicle.RegNo,
                    vehicle.Type,
                    vehicle.Mode,
                    vehicle.Status,
                    vehicle.DispatchState,
                    vehicle.Owned,
                    // SCR-AP-013's chips "jump to the vehicle detail" (Scenario 100). The link is
                    // built here rather than assembled by the portal, so one component decides what
                    // a directory path looks like.
                    $"{AdminEndpoints.Prefix}/vehicles/{vehicle.VehicleId:D}")),
            ],
            [.. tabs.Trips.Select(Trip)],
            [
                .. tabs.WalletLedger.Select(entry => new WalletLedgerResponse(
                    entry.EntryNo,
                    entry.Kind,
                    entry.AmountMinor,
                    entry.BalanceAfterMinor,
                    entry.Description,
                    entry.Ts)),
            ],
            [.. tabs.DailyFee.Select(DailyFee)],
            [
                .. tabs.CreditTransfers.Select(transfer => new CreditTransferResponse(
                    transfer.TransferId,
                    transfer.Direction,
                    transfer.Initiation,
                    transfer.CounterpartyId,
                    transfer.CounterpartyName,
                    transfer.AmountMinor,
                    transfer.Currency,
                    transfer.Status,
                    transfer.CreatedAt)),
            ],
            [.. tabs.Reports.Select(Report)]);
    }

    // ---------------------------------------------------------------------------------------
    // Vehicles (AL-42)
    // ---------------------------------------------------------------------------------------

    public async Task<CursorPage<VehicleRowResponse>> SearchVehiclesAsync(
        string? regNo,
        Guid? id,
        string? type,
        string? mode,
        string? ownerMobile,
        string? fleetOrg,
        string? status,
        PageRequest page,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);

        var (cursorAt, cursorId) = QueueCursors.Decode(page.Cursor);

        var rows = await directories.SearchVehiclesAsync(
            new VehicleSearchQuery(
                id, regNo, type, mode, ownerMobile, fleetOrg, status, cursorAt, cursorId, page.OverfetchLimit),
            cancellationToken);

        return CursorPage<VehicleDirectoryRow>
            .FromOverfetch(rows, page.Limit, row => QueueCursors.Encode(row.RegisteredAt, row.VehicleId))
            .Select(row => new VehicleRowResponse(
                row.VehicleId,
                row.Type,
                row.Mode,
                row.Owner,
                row.FleetOrg,
                row.RegNo,
                row.Trips,
                row.Status));
    }

    public async Task<AdminVehicleDetailResponse> VehicleDetailAsync(
        Guid vehicleId, HttpContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var info = await directories.FindVehicleAsync(vehicleId, cancellationToken)
            ?? throw new MageRideException(MageRideErrors.VehicleNotFound, $"No vehicle {vehicleId}.");

        var tabs = await directories.VehicleTabsAsync(vehicleId, TabRows, cancellationToken);
        var view = pii.For(context.User);

        Record(vehicleId, AdminAuditActions.VehicleEntity, view, tabs.Trips.Count);

        return new AdminVehicleDetailResponse(
            new VehicleInfoResponse(
                info.VehicleId,
                info.Type,
                info.RegNo,
                info.Mode,
                info.OwnerId,
                info.Owner,
                info.FleetId,
                info.FleetOrg,
                info.Status,
                info.DispatchState,
                info.OnboardingStatus,
                info.InsuranceExpiry,
                info.RevenueLicenceExpiry,
                info.RegisteredAt,
                Tracker(tabs.Tracker)),
            [
                // The same audited links C063 mints: `thumbUrl`/`fullUrl` point at
                // GET /v1/admin/documents/{docId}, which writes the DOC_VIEW row and only then
                // redirects to the signed object URL. A directory that handed out bucket URLs
                // would be a second door onto the same documents with no row behind it (AL-39).
                .. tabs.Documents.Select(document => new DocumentRefResponse(
                    document.DocId,
                    document.Kind,
                    links.Create(document.DocId, DocumentVariants.Thumb),
                    links.Create(document.DocId, DocumentVariants.Full),
                    document.CapturedVia)),
            ],
            [.. tabs.Trips.Select(Trip)],
            [
                .. tabs.Earnings.Select(day => new VehicleEarningsResponse(
                    day.EarnDate, day.Trips, day.GrossMinor, day.Currency)),
            ],
            [.. tabs.DailyFee.Select(DailyFee)],
            [.. tabs.Reports.Select(Report)]);
    }

    // ---------------------------------------------------------------------------------------
    // Shared projections
    // ---------------------------------------------------------------------------------------

    private static TripResponse Trip(TripRow row) => new(
        row.TripId,
        row.Kind,
        row.State,
        row.VehicleType,
        row.VehicleId,
        row.RegNo,
        row.CounterpartyId,
        row.CounterpartyName,
        row.FareMinor,
        row.Currency,
        row.StartedAt,
        row.EndedAt);

    private static DailyFeeResponse DailyFee(DailyFeeRow row) => new(
        row.FeeDate,
        row.DriverId,
        row.VehicleId,
        row.RegNo,
        row.AmountMinor,
        row.Currency,
        row.TripsThatDay,
        row.Status,
        row.ChargedAt);

    private static VehicleReportResponse Report(VehicleReportRow row) => new(
        row.ReportId, row.VehicleId, row.RegNo, row.Reason, row.Status, row.CreatedAt);

    private TrackerResponse? Tracker(TrackerRow? row) =>
        row is null
            ? null
            : new TrackerResponse(
                row.Imei,
                row.State == "ACTIVE" &&
                row.LastSeenAt is { } seen &&
                clock.GetUtcNow() - seen < TrackerOfflineAfter,
                row.State,
                row.LastSeenAt);

    /// <summary>
    /// The one <c>PII_READ</c> row a detail open writes (I-28.6, BR-28.8, D-35).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>piiRevealed</c> is the field that makes the row worth keeping.</b> Every permitted role
    /// can open every detail, so "who opened this record" alone does not answer the question a
    /// privacy investigation asks — which is who actually saw the number. The masking decision is
    /// already made by the time this is recorded, so it is a fact rather than an inference.
    /// </para>
    /// <para>
    /// Recorded, not written: the D-35 interceptor writes it after the response is known to be a
    /// success, which is what keeps a failed read out of the trail.
    /// </para>
    /// </remarks>
    private void Record(Guid subjectId, string entityType, PiiView view, int rows) =>
        audit.Record(
            subjectId,
            after: new
            {
                subject = entityType,
                piiRevealed = view.Clear,
                tabRows = rows,
            },
            action: AdminAuditActions.PiiRead,
            entityType: entityType);
}
