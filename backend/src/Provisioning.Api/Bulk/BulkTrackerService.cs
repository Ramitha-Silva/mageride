using System.Text;
using Dapper;
using MageRide.Provisioning.Configuration;
using MageRide.Provisioning.Domain;
using MageRide.Provisioning.Persistence;
using MageRide.Shared.Errors;
using MageRide.Shared.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace MageRide.Provisioning.Bulk;

/// <summary>
/// Bulk IMEI onboarding for a fleet (T-09, US-3.2).
/// </summary>
/// <remarks>
/// <para>
/// <b>Validation is atomic; execution is per row.</b> Those are two different guarantees and D3'
/// asks for both: "SAGA validates rows; materialises bindings; queues credential-mint jobs;
/// per-row error report". The job and every one of its rows commit together or not at all, so a
/// CSV never half-arrives; the bindings behind them are minted one at a time afterwards, and a row
/// that cannot be bound fails on its own and shows up in the report rather than taking the batch
/// with it.
/// </para>
/// <para>
/// <b>A row whose IMEI is already bound to the very vehicle it names is failed here, not sent to
/// the minter.</b> Re-uploading last week's CSV is the most likely thing an operator will ever do
/// with this endpoint, and putting those rows through the bind path would hand every one of them
/// to the T-08 anti-clone rule and quarantine a working fleet. A row naming a *different* vehicle
/// for a live IMEI is the opposite case and is left to the minter deliberately: that is a genuine
/// second claim, and quarantining it is the correct answer.
/// </para>
/// </remarks>
public interface IBulkTrackerService
{
    Task<BulkJob> SubmitAsync(
        Guid actorId, Guid fleetId, string credentialType, string csv, CancellationToken cancellationToken);

    Task<BulkJob> GetAsync(Guid actorId, Guid fleetId, Guid jobId, CancellationToken cancellationToken);

    /// <summary>The per-row report, as CSV text.</summary>
    Task<string> BuildErrorReportAsync(Guid fleetId, Guid jobId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IBulkTrackerService"/>
public sealed class BulkTrackerService(
    INpgsqlConnectionFactory connectionFactory,
    IUnitOfWorkFactory unitOfWorkFactory,
    IBulkJobRepository jobs,
    IVehicleLookupRepository vehicles,
    IOptions<ProvisioningOptions> options,
    TimeProvider clock,
    ILogger<BulkTrackerService> logger) : IBulkTrackerService
{
    private readonly ProvisioningOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<BulkJob> SubmitAsync(
        Guid actorId, Guid fleetId, string credentialType, string csv, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(csv);

        if (!CredentialTypes.IsKnown(credentialType))
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["credentialType"] = ["credentialType must be 'x509' or 'psk'."],
            });
        }

        var lines = BulkCsv.Parse(csv);

        if (lines.Count == 0)
        {
            throw new MageRideException(
                MageRideErrors.CsvInvalid, "The upload contained no rows. Expected lines of 'imei,registrationNumber'.");
        }

        if (lines.Count > _options.BulkMaxRows)
        {
            throw new MageRideException(
                MageRideErrors.TooManyRows,
                $"{lines.Count} rows exceeds the {_options.BulkMaxRows}-row limit for one upload.");
        }

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        if (!await vehicles.IsFleetPrincipalAsync(
                unitOfWork.Connection, unitOfWork.Transaction, fleetId, actorId, cancellationToken))
        {
            // Covers "not your fleet" and "no such fleet" alike. A caller who can tell those apart
            // can enumerate fleet ids, and D3' gives this operation no 404 to distinguish them with.
            throw new MageRideException(
                MageRideErrors.Forbidden, "You are not an owner or manager of this fleet (AL-03).");
        }

        var validated = await ValidateAsync(unitOfWork, fleetId, lines, cancellationToken);

        var job = await jobs.CreateAsync(
                      unitOfWork.Connection,
                      unitOfWork.Transaction,
                      fleetId,
                      actorId,
                      credentialType,
                      [.. validated.Select(row => row.Input)],
                      cancellationToken)
                  ?? throw new MageRideException(
                      MageRideErrors.BulkInProgress,
                      "This fleet already has a bulk tracker job in flight. Wait for it to finish before starting another.");

        var failures = validated.Where(row => row.ErrorCode is not null).ToArray();

        foreach (var failure in failures)
        {
            await jobs.CompleteRowAsync(
                unitOfWork.Connection,
                unitOfWork.Transaction,
                job.Id,
                failure.Input.RowNumber,
                BulkRowStatuses.Failed,
                bindingId: null,
                failure.ErrorCode,
                failure.ErrorDetail,
                cancellationToken);
        }

        // Recounted inside the same transaction, so a CSV where every row failed validation is
        // COMPLETED by the time the 202 is written rather than PROCESSING forever with no work for
        // the minter to pick up.
        var recounted = await jobs.RecountAsync(
            unitOfWork.Connection, unitOfWork.Transaction, job.Id, clock.GetUtcNow(), cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Bulk job {JobId} accepted for fleet {FleetId}: {Total} row(s), {Failed} rejected at validation",
            job.Id,
            fleetId,
            recounted.TotalRows,
            failures.Length);

        return recounted;
    }

    public async Task<BulkJob> GetAsync(Guid actorId, Guid fleetId, Guid jobId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        if (!await vehicles.IsFleetPrincipalAsync(connection, null, fleetId, actorId, cancellationToken))
        {
            throw new MageRideException(
                MageRideErrors.Forbidden, "You are not an owner or manager of this fleet (AL-03).");
        }

        return await jobs.FindAsync(connection, null, fleetId, jobId, cancellationToken)
               ?? throw new MageRideException(MageRideErrors.NotFound, $"No bulk job {jobId} for this fleet.");
    }

    public async Task<string> BuildErrorReportAsync(Guid fleetId, Guid jobId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        _ = await jobs.FindAsync(connection, null, fleetId, jobId, cancellationToken)
            ?? throw new MageRideException(MageRideErrors.NotFound, $"No bulk job {jobId} for this fleet.");

        var rows = await jobs.ListRowsAsync(connection, null, jobId, cancellationToken);

        var report = new StringBuilder("row,imei,registrationNumber,status,errorCode,errorDetail\n");

        // Every row, not only the failures. An operator reconciling 5,000 trackers needs to see
        // which ones landed as much as which ones did not, and a report that lists only problems
        // cannot be diffed against the file they uploaded.
        foreach (var row in rows)
        {
            report.Append(BulkCsv.Line(row.RowNumber)).Append(',')
                .Append(BulkCsv.Quote(row.Imei)).Append(',')
                .Append(BulkCsv.Quote(row.RegistrationNumber)).Append(',')
                .Append(BulkCsv.Quote(row.Status)).Append(',')
                .Append(BulkCsv.Quote(row.ErrorCode)).Append(',')
                .Append(BulkCsv.Quote(row.ErrorDetail)).Append('\n');
        }

        return report.ToString();
    }

    /// <summary>
    /// Resolves every line against the fleet's roster and the live bindings, in two queries.
    /// </summary>
    /// <remarks>
    /// Per-row lookups would be 10,000 round trips for a 5,000-row file, which is most of the
    /// NFR-43 budget spent before a single credential has been minted. Both reads are set-based
    /// and the matching is done here.
    /// </remarks>
    private async Task<IReadOnlyList<ValidatedRow>> ValidateAsync(
        IUnitOfWork unitOfWork, Guid fleetId, IReadOnlyList<CsvLine> lines, CancellationToken cancellationToken)
    {
        var roster = await LoadRosterAsync(unitOfWork.Connection, unitOfWork.Transaction, fleetId, cancellationToken);
        var live = await LoadLiveBindingsAsync(
            unitOfWork.Connection,
            unitOfWork.Transaction,
            [.. lines.Select(line => line.Imei).Where(Imeis.IsValid).Distinct(StringComparer.Ordinal)],
            cancellationToken);

        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var validated = new List<ValidatedRow>(lines.Count);

        foreach (var line in lines)
        {
            var input = new BulkRowInput(line.LineNumber, line.Imei, line.RegistrationNumber, null);

            if (line.Error is { } malformed)
            {
                validated.Add(new ValidatedRow(input, MageRideErrors.CsvInvalid.Code, malformed));
                continue;
            }

            if (!Imeis.IsValid(line.Imei))
            {
                validated.Add(new ValidatedRow(
                    input, MageRideErrors.ValidationFailed.Code, "imei must be exactly 15 digits"));
                continue;
            }

            // A file that lists one IMEI twice is a typo, not a clone: both lines came from one
            // operator in one upload, and the honest answer is to bind the first and tell them
            // about the second rather than quarantine what the first one just created.
            if (seen.TryGetValue(line.Imei, out var firstLine))
            {
                validated.Add(new ValidatedRow(
                    input, MageRideErrors.ImeiDuplicate.Code, $"imei also appears on line {BulkCsv.Line(firstLine)}"));
                continue;
            }

            seen[line.Imei] = line.LineNumber;

            if (!roster.TryGetValue(line.RegistrationNumber, out var vehicleId))
            {
                validated.Add(new ValidatedRow(
                    input,
                    MageRideErrors.VehicleNotFound.Code,
                    $"no vehicle '{line.RegistrationNumber}' on this fleet's roster"));

                continue;
            }

            if (live.TryGetValue(line.Imei, out var boundTo) && boundTo == vehicleId)
            {
                validated.Add(new ValidatedRow(
                    input,
                    MageRideErrors.ImeiDuplicate.Code,
                    "imei is already bound to this vehicle; nothing to do"));

                continue;
            }

            validated.Add(new ValidatedRow(input with { VehicleId = vehicleId }, null, null));
        }

        return validated;
    }

    private static async Task<Dictionary<string, Guid>> LoadRosterAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid fleetId, CancellationToken cancellationToken)
    {
        var rows = await connection.QueryAsync<(string RegistrationNumber, Guid Id)>(new CommandDefinition(
            """
            SELECT v.registration_number, v.id
              FROM registry.vehicles v
              JOIN registry.fleet_vehicles fv ON fv.vehicle_id = v.id
             WHERE fv.fleet_id = @FleetId;
            """,
            new { FleetId = fleetId },
            transaction,
            cancellationToken: cancellationToken));

        var roster = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

        foreach (var (registrationNumber, id) in rows)
        {
            roster[registrationNumber] = id;
        }

        return roster;
    }

    private static async Task<Dictionary<string, Guid>> LoadLiveBindingsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string[] imeis,
        CancellationToken cancellationToken)
    {
        if (imeis.Length == 0)
        {
            return new Dictionary<string, Guid>(StringComparer.Ordinal);
        }

        var rows = await connection.QueryAsync<(string Imei, Guid VehicleId)>(new CommandDefinition(
            $"""
             SELECT imei, vehicle_id
               FROM prov.tracker_bindings
              WHERE state = '{BindingStates.Active}' AND imei = ANY(@Imeis);
             """,
            new { Imeis = imeis },
            transaction,
            cancellationToken: cancellationToken));

        var live = new Dictionary<string, Guid>(StringComparer.Ordinal);

        foreach (var (imei, vehicleId) in rows)
        {
            live[imei] = vehicleId;
        }

        return live;
    }

    private sealed record ValidatedRow(BulkRowInput Input, string? ErrorCode, string? ErrorDetail);
}
