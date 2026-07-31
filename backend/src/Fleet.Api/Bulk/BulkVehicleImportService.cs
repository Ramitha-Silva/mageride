using System.Globalization;
using System.Text;
using Dapper;
using MageRide.Fleet.Configuration;
using MageRide.Fleet.Domain;
using MageRide.Fleet.Persistence;
using MageRide.Fleet.Vehicles;
using MageRide.Shared.Errors;
using MageRide.Shared.Persistence;
using Microsoft.Extensions.Options;
using Npgsql;

namespace MageRide.Fleet.Bulk;

/// <summary>What a finished import produced, as the 202 renders it.</summary>
public sealed record BulkImportResult(BulkVehicleJob Job, string? ErrorReportUrl);

/// <summary>US-13.1's bulk CSV onboarding, and the per-row error report it answers with.</summary>
public interface IBulkVehicleImportService
{
    Task<BulkImportResult> ImportAsync(
        Guid fleetId, Guid requestedBy, string csv, CancellationToken cancellationToken);

    /// <summary>The job as it stands, for the poll the 202's <c>Location</c> points at.</summary>
    Task<BulkImportResult> GetAsync(Guid fleetId, Guid jobId, CancellationToken cancellationToken);

    /// <summary>The downloadable error report, as CSV text.</summary>
    Task<string> BuildErrorReportAsync(Guid fleetId, Guid jobId, CancellationToken cancellationToken);
}

/// <summary>
/// <inheritdoc cref="IBulkVehicleImportService"/>
/// </summary>
/// <remarks>
/// <para>
/// <b>Every row is imported or reported, and the whole file commits at once.</b> A row that fails
/// is rolled back to its own savepoint and recorded as <c>FAILED</c> with the same kebab error code
/// the single-vehicle <c>POST</c> would have raised, so the report and the API speak one
/// vocabulary. Without the savepoint the first duplicate plate would abort the transaction and take
/// the 4,999 good rows with it — which is the failure US-13.1's "imports the valid rows" rules out.
/// </para>
/// <para>
/// <b>There is no worker and no <c>PENDING</c> row state.</b> provisioning-svc's bulk drains its
/// rows afterwards because each has a credential to mint against a CA; a vehicle row is an
/// <c>INSERT</c>, so validating and importing together is both simpler and stronger — a poll can
/// never observe a job that is still growing.
/// </para>
/// <para>
/// <b>Every imported vehicle starts <c>docs_pending</c>.</b> AL-50 says so outright: "bulk CSV rows
/// are created <c>docs_pending</c>; documents arrive per vehicle via the endpoint above". Nothing
/// here writes that state, because it is derived from the documents the vehicle does not yet have.
/// </para>
/// </remarks>
internal sealed class BulkVehicleImportService(
    IUnitOfWorkFactory unitOfWorkFactory,
    IFleetScopedReader scopedReader,
    IFleetRepository fleets,
    IFleetVehicleRepository vehicles,
    IFleetBulkJobRepository jobs,
    IErrorReportLinks links,
    IOptions<FleetOptions> options,
    ILogger<BulkVehicleImportService> logger) : IBulkVehicleImportService
{
    private readonly FleetOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<BulkImportResult> ImportAsync(
        Guid fleetId, Guid requestedBy, string csv, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(csv);

        var lines = BulkVehicleCsv.Parse(csv);

        if (lines.Count == 0)
        {
            throw new MageRideException(
                MageRideErrors.CsvInvalid,
                "The upload has no rows. Expected registrationNumber,vehicleType,mode per line "
                + "(a header row is optional).");
        }

        if (lines.Count > _options.BulkMaxRows)
        {
            throw new MageRideException(
                MageRideErrors.TooManyRows,
                $"The upload has {lines.Count} rows; at most {_options.BulkMaxRows} may be onboarded at once.");
        }

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var fleet = await fleets.FindAsync(
            unitOfWork.Connection, unitOfWork.Transaction, fleetId, cancellationToken)
            ?? throw new MageRideException(FleetErrors.FleetNotFound, "No such fleet organisation.");

        Guid jobId;

        try
        {
            jobId = await jobs.CreateAsync(
                unitOfWork.Connection, unitOfWork.Transaction, fleetId, requestedBy, lines.Count, cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // ux_fleet_bulk_jobs_in_flight. Two Fleet Portal tabs submitting at once is the race a
            // SELECT-then-INSERT loses, and the second upload would import a duplicate of every row
            // in the first.
            throw new MageRideException(
                MageRideErrors.BulkInProgress,
                "A bulk vehicle import is already running for this organisation. Wait for it to finish.");
        }

        // Plates already claimed inside this very file. The database catches a collision with an
        // existing vehicle; it cannot catch two rows of one upload naming the same plate, because
        // the first of them is a legitimate insert. Reporting the second as `registration-exists`
        // is the same answer an operator would get by uploading the file twice.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<BulkVehicleRow>(lines.Count);

        foreach (var line in lines)
        {
            rows.Add(await ImportRowAsync(unitOfWork, fleetId, fleet.OwnerId, fleet.Name, line, seen, cancellationToken));
        }

        var imported = rows.Count(row => string.Equals(row.Status, BulkRowStatuses.Imported, StringComparison.Ordinal));

        await jobs.AddRowsAsync(unitOfWork.Connection, unitOfWork.Transaction, jobId, rows, cancellationToken);

        // COMPLETED even when rows failed. FAILED is reserved for a job that could not be processed
        // at all; "nine of ten imported" is a completed job with a report, and a client branching on
        // the status would otherwise discard nine good vehicles.
        await jobs.FinishAsync(
            unitOfWork.Connection,
            unitOfWork.Transaction,
            jobId,
            BulkJobStatuses.Completed,
            imported,
            rows.Count - imported,
            cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Fleet {FleetId} bulk-imported {Imported} of {Total} vehicles (job {JobId}); every one starts "
            + "docs_pending and cannot be approved until its AL-50 slots are verified.",
            fleetId,
            imported,
            rows.Count,
            jobId);

        return await GetAsync(fleetId, jobId, cancellationToken);
    }

    public Task<BulkImportResult> GetAsync(Guid fleetId, Guid jobId, CancellationToken cancellationToken) =>
        scopedReader.ReadAsync(
            fleetId,
            async (connection, transaction) =>
            {
                var job = await jobs.FindAsync(connection, transaction, fleetId, jobId, cancellationToken)
                    ?? throw new MageRideException(MageRideErrors.NotFound, "No such bulk import job.");

                // The link is minted only when there is something behind it. A URL that downloads an
                // empty report is a button an operator presses and learns nothing from.
                return new BulkImportResult(job, job.FailedRows > 0 ? links.Create(fleetId, jobId) : null);
            },
            cancellationToken);

    public Task<string> BuildErrorReportAsync(Guid fleetId, Guid jobId, CancellationToken cancellationToken) =>
        scopedReader.ReadAsync(
            fleetId,
            async (connection, transaction) =>
            {
                var failed = await jobs.ListFailedRowsAsync(
                    connection, transaction, fleetId, jobId, cancellationToken);

                var report = new StringBuilder(BulkVehicleCsv.ReportHeader).Append('\n');

                foreach (var row in failed)
                {
                    report
                        .Append(BulkVehicleCsv.Line(row.RowNumber)).Append(',')
                        .Append(BulkVehicleCsv.Quote(row.RegistrationNumber)).Append(',')
                        .Append(BulkVehicleCsv.Quote(row.VehicleType)).Append(',')
                        .Append(BulkVehicleCsv.Quote(row.Mode)).Append(',')
                        .Append(BulkVehicleCsv.Quote(row.ErrorCode)).Append(',')
                        .Append(BulkVehicleCsv.Quote(row.ErrorDetail)).Append('\n');
                }

                return report.ToString();
            },
            cancellationToken);

    /// <summary>
    /// One CSV line, imported or reported.
    /// </summary>
    /// <remarks>
    /// The savepoint is what makes a bad row cost only itself. Postgres aborts the whole
    /// transaction on a constraint violation, so without one the first duplicate plate would take
    /// every later row with it — and the operator would be told nothing imported when 4,999 rows
    /// were fine.
    /// </remarks>
    private async Task<BulkVehicleRow> ImportRowAsync(
        IUnitOfWork unitOfWork,
        Guid fleetId,
        Guid ownerId,
        string fleetName,
        BulkVehicleCsvLine line,
        HashSet<string> seen,
        CancellationToken cancellationToken)
    {
        if (line.Error is { Length: > 0 } shape)
        {
            return Failed(line, MageRideErrors.CsvInvalid.Code, shape);
        }

        string registration;
        int? fare;

        try
        {
            registration = FleetVehicleService.RequireRegistration(line.RegistrationNumber);
            FleetVehicleService.RequireOnboardableType(line.VehicleType);
            FleetVehicleService.RequireFleetMode(line.Mode);
            fare = ParseFare(line);
        }
        catch (MageRideValidationException exception)
        {
            // The validator's own message, flattened. Its field names are the CSV's column names,
            // so "registrationNumber: … may contain only letters, digits, spaces and hyphens" reads
            // correctly against a spreadsheet column.
            return Failed(
                line,
                MageRideErrors.ValidationFailed.Code,
                string.Join("; ", exception.Errors.SelectMany(
                    entry => entry.Value.Select(message => $"{entry.Key}: {message}"))));
        }
        catch (MageRideException exception)
        {
            return Failed(line, exception.Error.Code, exception.Detail ?? exception.Error.Title);
        }

        var billing = NormaliseBilling(line.ModeBBilling);

        if (billing is not null && !ModeBBilling.All.Contains(billing))
        {
            return Failed(
                line,
                MageRideErrors.ValidationFailed.Code,
                "modeBBilling: must be 'free' or 'paid' (\"Service payment\" in the UI, AL-51).");
        }

        // Service payment is Mode B's alone. A Mode A row carrying one is a spreadsheet mistake and
        // is reported rather than silently dropped — `registry.vehicles.mode_b_billing` is NULL for
        // Mode A by design (AL-24), so accepting it would store nothing and tell nobody.
        if (billing is not null && !string.Equals(line.Mode, FleetModes.Private, StringComparison.Ordinal))
        {
            return Failed(
                line,
                MageRideErrors.ValidationFailed.Code,
                "modeBBilling: Service payment applies to Mode B vehicles only.");
        }

        if (string.Equals(billing, ModeBBilling.Paid, StringComparison.Ordinal) && fare is null or <= 0)
        {
            return Failed(
                line,
                MageRideErrors.ValidationFailed.Code,
                "defaultMonthlyFareMinor: a Paid vehicle needs a default monthly fare in cents, greater than zero.");
        }

        if (!seen.Add(registration))
        {
            return Failed(
                line,
                MageRideErrors.RegistrationExists.Code,
                $"{registration} appears more than once in this file.");
        }

        const string savepoint = "bulk_row";

        await unitOfWork.Connection.ExecuteAsync(new CommandDefinition(
            $"SAVEPOINT {savepoint};", transaction: unitOfWork.Transaction, cancellationToken: cancellationToken));

        try
        {
            var added = await vehicles.AddAsync(
                unitOfWork.Connection,
                unitOfWork.Transaction,
                fleetId,
                ownerId,
                registration,
                line.VehicleType,
                line.Mode,
                fleetName,
                cancellationToken);

            // The classification rides the same statement rather than the gated endpoint, because
            // an unverified payout profile must not fail a whole import — so `paid` is refused here
            // when the org has no verified profile, per row, in the report's own vocabulary.
            if (billing is not null)
            {
                if (string.Equals(billing, ModeBBilling.Paid, StringComparison.Ordinal)
                    && !await HasVerifiedPayoutProfileAsync(unitOfWork, fleetId, cancellationToken))
                {
                    await RollbackAsync(unitOfWork, savepoint, cancellationToken);

                    return Failed(
                        line,
                        MageRideErrors.PayoutProfileNotVerified.Code,
                        "A Verification Officer must verify the organisation's bank and payout profile before a "
                        + "vehicle can be Paid (BR-31.1).");
                }

                await vehicles.SetClassificationAsync(
                    unitOfWork.Connection,
                    unitOfWork.Transaction,
                    fleetId,
                    added.VehicleId,
                    billing,
                    fare,
                    cancellationToken);
            }

            await unitOfWork.Connection.ExecuteAsync(new CommandDefinition(
                $"RELEASE SAVEPOINT {savepoint};",
                transaction: unitOfWork.Transaction,
                cancellationToken: cancellationToken));

            return new BulkVehicleRow(
                line.LineNumber,
                registration,
                line.VehicleType,
                line.Mode,
                billing,
                fare,
                BulkRowStatuses.Imported,
                added.VehicleId,
                null,
                null);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await RollbackAsync(unitOfWork, savepoint, cancellationToken);

            return Failed(
                line,
                MageRideErrors.RegistrationExists.Code,
                $"A live vehicle is already registered as {registration} (D-37).");
        }
    }

    private static async Task RollbackAsync(
        IUnitOfWork unitOfWork, string savepoint, CancellationToken cancellationToken) =>
        await unitOfWork.Connection.ExecuteAsync(new CommandDefinition(
            $"ROLLBACK TO SAVEPOINT {savepoint};",
            transaction: unitOfWork.Transaction,
            cancellationToken: cancellationToken));

    private static Task<bool> HasVerifiedPayoutProfileAsync(
        IUnitOfWork unitOfWork, Guid fleetId, CancellationToken cancellationToken) =>
        unitOfWork.Connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            $"""
             SELECT EXISTS (SELECT 1 FROM registry.fleet_payout_profiles
                             WHERE fleet_id = @FleetId AND status = '{PayoutProfileStatuses.Verified}');
             """,
            new { FleetId = fleetId },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));

    private static BulkVehicleRow Failed(BulkVehicleCsvLine line, string code, string detail) =>
        new(line.LineNumber,
            line.RegistrationNumber,
            line.VehicleType,
            line.Mode,
            line.ModeBBilling,
            null,
            BulkRowStatuses.Failed,
            null,
            code,
            detail);

    private static string? NormaliseBilling(string? value) =>
        value is { Length: > 0 } ? value.Trim().ToLowerInvariant() : null;

    /// <summary>
    /// The fare column, or <see langword="null"/> — refused rather than guessed when it is not a
    /// whole number of cents.
    /// </summary>
    /// <remarks>
    /// <c>registry.vehicles.default_monthly_fare_minor</c> is <c>INTEGER</c>, so a wider number is
    /// out of range for the column whatever the contract types the field as; and a decimal is
    /// almost always a spreadsheet with rupees in it, which is out by a hundred.
    /// </remarks>
    private static int? ParseFare(BulkVehicleCsvLine line) =>
        line.DefaultMonthlyFareMinor is { Length: > 0 } raw
            ? int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["defaultMonthlyFareMinor"] =
                        ["must be a whole number of cents (Rs 2,500.00 is 250000), with no separators."],
                })
            : null;
}
