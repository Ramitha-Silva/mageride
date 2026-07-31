using System.Globalization;
using MageRide.Fleet.Configuration;
using MageRide.Fleet.Documents;
using MageRide.Fleet.Domain;
using MageRide.Fleet.Persistence;
using MageRide.Shared.Errors;
using MageRide.Shared.Persistence;
using Microsoft.Extensions.Options;

namespace MageRide.Fleet.Vehicles;

/// <summary>What an operator posts into one of SCR-FP-004's named slots.</summary>
/// <param name="WireKind">
/// The slot label, as <c>fleet.yaml</c>'s <c>kind</c> enum spells it — <c>registration_copy</c>,
/// <c>insurance</c>, <c>revenue_license</c> or <c>route_permit</c>.
/// </param>
/// <param name="DeclaredExpiry">
/// What the operator typed, used only when extraction returned no expiry of its own. A typed date
/// is evidence about a document nobody has read yet, which is why it never overrides one that was.
/// </param>
public sealed record UploadVehicleDocumentCommand(
    string? WireKind, Stream Content, DateOnly? DeclaredExpiry);

/// <summary>AL-50's four named document slots for one of the org's vehicles (SCR-FP-004, US-27.3).</summary>
public interface IVehicleDocumentService
{
    /// <summary>Every slot and its chip, with the extracted fields behind each.</summary>
    Task<IReadOnlyList<VehicleDocumentSlot>> ListAsync(
        Guid fleetId, Guid vehicleId, CancellationToken cancellationToken);

    /// <summary>Stores a document into a slot and queues the ocr-svc extraction that settles it.</summary>
    Task<VehicleDocumentSlot> UploadAsync(
        Guid fleetId, Guid vehicleId, Guid uploaderId, UploadVehicleDocumentCommand command,
        CancellationToken cancellationToken);
}

/// <summary>
/// <inheritdoc cref="IVehicleDocumentService"/>
/// </summary>
/// <remarks>
/// <para>
/// <b>The order of the three writes is the crash-safety argument.</b> The bytes go to storage
/// first, then <c>docs.uploads</c>, then — after ocr-svc has answered — <c>registry.documents</c>
/// and its fields. Each failure in between leaves the benign side of a choice: an orphan file that
/// NFR-28's deadline sweeps, or an upload row pointing at a file nothing references. The other
/// order leaves a slot claiming a document the Verification Officer is told exists and cannot open.
/// </para>
/// <para>
/// <b>The <c>docs.uploads</c> row must commit before ocr-svc is called</b>, and that is why it is
/// its own transaction rather than folded into the one below. ocr-svc writes a
/// <c>docs.extractions</c> row whose <c>upload_id</c> is a foreign key onto it; calling first and
/// inserting afterwards would fail that key inside another service.
/// </para>
/// <para>
/// <b>The extraction runs with no transaction open.</b> ocr-svc is a network hop that may take
/// tens of seconds — a redaction pre-pass, a vision-model call, an on-prem fallback behind it — and
/// holding a Postgres transaction across it would put another service's latency on this one's
/// connection pool. C029's rule, for the same reason.
/// </para>
/// <para>
/// <b>A required field extraction did not return is written anyway</b>, with a null value and
/// <c>pending</c>. "The permit expiry could not be read" is then a row the officer's queue shows
/// and can fill, rather than an absence somebody has to notice — and it is what holds the slot out
/// of <c>verified</c>, which is what holds the vehicle out of APPROVED.
/// </para>
/// </remarks>
internal sealed class VehicleDocumentService(
    IUnitOfWorkFactory unitOfWorkFactory,
    IFleetScopedReader scopedReader,
    IFleetVehicleRepository vehicles,
    IVehicleDocumentRepository documents,
    IDocumentStore store,
    IVehicleDocumentExtractionClient extraction,
    IOptions<FleetOptions> options,
    ILogger<VehicleDocumentService> logger) : IVehicleDocumentService
{
    private readonly FleetOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public Task<IReadOnlyList<VehicleDocumentSlot>> ListAsync(
        Guid fleetId, Guid vehicleId, CancellationToken cancellationToken) =>
        scopedReader.ReadAsync(
            fleetId,
            async (connection, transaction) =>
            {
                var vehicle = await vehicles.FindAsync(
                    connection, transaction, fleetId, vehicleId, cancellationToken)
                    ?? throw new MageRideException(
                        MageRideErrors.VehicleNotFound, "This vehicle is not in the organisation's fleet.");

                return await ReadSlotsAsync(connection, transaction, fleetId, vehicle.Mode, vehicleId, cancellationToken);
            },
            cancellationToken);

    public async Task<VehicleDocumentSlot> UploadAsync(
        Guid fleetId,
        Guid vehicleId,
        Guid uploaderId,
        UploadVehicleDocumentCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var kind = VehicleDocumentKinds.ToStoredKind(command.WireKind?.Trim())
            ?? throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["kind"] =
                [
                    "kind must be one of registration_copy, insurance, revenue_license, route_permit "
                    + "(SCR-FP-004's named slots, AL-50).",
                ],
            });

        // Read before writing a byte: an upload against a vehicle that is not this org's must not
        // leave a file on disk, and the plate is what a CR book's `reg_no_match` is judged against.
        var vehicle = await scopedReader.ReadAsync(
            fleetId,
            (connection, transaction) => vehicles.FindAsync(
                connection, transaction, fleetId, vehicleId, cancellationToken),
            cancellationToken)
            ?? throw new MageRideException(
                MageRideErrors.VehicleNotFound, "This vehicle is not in the organisation's fleet.");

        // A Mode B vehicle has no route permit to file. Refused rather than accepted and ignored:
        // an operator who uploaded one would see a chip that never turns green on a slot AL-50 does
        // not require, and would go looking for the reason.
        if (string.Equals(kind, VehicleDocumentKinds.Permit, StringComparison.Ordinal)
            && !string.Equals(vehicle.Mode, FleetModes.PublicTransport, StringComparison.Ordinal))
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["kind"] =
                [
                    "A route permit belongs to a Mode A passenger-transport vehicle (US-27.3); this one is Mode "
                    + vehicle.Mode + ".",
                ],
            });
        }

        var uploadId = Guid.CreateVersion7();
        var stored = await store.WriteAsync(uploadId, kind, command.Content, cancellationToken);

        await using (var upload = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken))
        {
            await documents.CreateUploadAsync(
                upload.Connection,
                upload.Transaction,
                uploadId,
                uploaderId,
                stored.StorageUrl,
                stored.Sha256,
                kind,
                _options.DocumentRetention,
                cancellationToken);

            await upload.CommitAsync(cancellationToken);
        }

        var read = await extraction.ExtractAsync(
            new VehicleDocumentExtractionRequest(uploadId, stored.StorageUrl, kind, vehicle.RegistrationNumber),
            cancellationToken);

        var fields = BuildFields(kind, read);
        var expiry = ResolveExpiry(kind, read, command.DeclaredExpiry);

        await using (var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken))
        {
            var document = await documents.CreateAsync(
                unitOfWork.Connection,
                unitOfWork.Transaction,
                fleetId,
                vehicleId,
                kind,
                stored.StorageUrl,
                expiry,
                cancellationToken);

            await documents.AddFieldsAsync(
                unitOfWork.Connection, unitOfWork.Transaction, document.Id, fields, cancellationToken);

            await unitOfWork.CommitAsync(cancellationToken);
        }

        logger.LogInformation(
            "Fleet {FleetId} filed a {Kind} document for vehicle {VehicleId}: extraction {Outcome}, "
            + "{Pending} of {Total} field(s) waiting on a Verification Officer (AL-50).",
            fleetId,
            kind,
            vehicleId,
            read.Succeeded ? "succeeded" : "unavailable",
            fields.Count(field => DocumentFieldVerifyStatuses.BlocksApproval(field.VerifyStatus)),
            fields.Count);

        var slots = await ListAsync(fleetId, vehicleId, cancellationToken);

        return slots.Single(slot => string.Equals(slot.Kind, kind, StringComparison.Ordinal));
    }

    private async Task<IReadOnlyList<VehicleDocumentSlot>> ReadSlotsAsync(
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction,
        Guid fleetId,
        string mode,
        Guid vehicleId,
        CancellationToken cancellationToken)
    {
        var held = await documents.ListForVehicleAsync(
            connection, transaction, fleetId, vehicleId, cancellationToken);

        var fields = await documents.ListFieldsAsync(
            connection, transaction, [.. held.Select(document => document.Id)], cancellationToken);

        return VehicleDocumentSlots.For(mode, held, fields);
    }

    /// <summary>
    /// AL-29's per-field verdict, applied to what came back and to what did not.
    /// </summary>
    /// <remarks>
    /// Deliberately identical to registry-svc's <c>DeriveVerifyStatus</c>, including the two
    /// clauses that are not about confidence: a field with no value is pending because an unread
    /// value has not been verified, and <c>reg_no_match</c> that is not true is pending however
    /// sure ocr-svc was — a plate that read as something else is a confident reading of the wrong
    /// vehicle (I-25.2).
    /// </remarks>
    private IReadOnlyList<VehicleDocumentField> BuildFields(string kind, VehicleDocumentExtraction read)
    {
        var required = RequiredFieldsFor(kind);
        var written = new List<VehicleDocumentField>();

        foreach (var field in read.Fields)
        {
            written.Add(new VehicleDocumentField(
                Guid.Empty,
                Guid.Empty,
                field.Key,
                field.Value,
                field.Confidence,
                DocumentFieldSources.Ai,
                VerifyStatusFor(field.Key, field.Value, field.Confidence)));
        }

        foreach (var key in required.Where(key => !written.Any(
                     field => string.Equals(field.FieldKey, key, StringComparison.Ordinal))))
        {
            written.Add(new VehicleDocumentField(
                Guid.Empty,
                Guid.Empty,
                key,
                null,
                null,
                DocumentFieldSources.Ai,
                DocumentFieldVerifyStatuses.Pending));
        }

        return written;
    }

    private string VerifyStatusFor(string key, string? value, decimal? confidence)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DocumentFieldVerifyStatuses.Pending;
        }

        if (string.Equals(key, VehicleDocumentFieldKeys.RegNoMatch, StringComparison.Ordinal)
            && !string.Equals(value.Trim(), "true", StringComparison.OrdinalIgnoreCase))
        {
            return DocumentFieldVerifyStatuses.Pending;
        }

        // No confidence is treated exactly like a low one.
        return confidence is null || confidence < _options.OcrConfidenceThreshold
            ? DocumentFieldVerifyStatuses.Pending
            : DocumentFieldVerifyStatuses.AutoVerified;
    }

    /// <summary>
    /// What <c>registry.documents.expires_at</c> becomes — the seam between AL-29 and E-03.
    /// </summary>
    /// <remarks>
    /// The extracted value wins when there is one, because it is what the certificate says; the
    /// operator's typed date is the fallback for a document nothing could read, and its own field
    /// is <c>pending</c> in that case, so the vehicle is not approved on an unverified number. A
    /// document with neither has no expiry to sweep — which is also why its slot stays
    /// <c>pending</c>.
    /// </remarks>
    private static DateTimeOffset? ResolveExpiry(
        string kind, VehicleDocumentExtraction read, DateOnly? declared)
    {
        var key = VehicleDocumentFieldKeys.ExpiryFieldFor(kind);

        if (key is not null)
        {
            var extracted = read.Fields.FirstOrDefault(
                field => string.Equals(field.Key, key, StringComparison.Ordinal))?.Value;

            if (DateTimeOffset.TryParse(
                    extracted, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            {
                return parsed;
            }
        }

        // Midnight UTC of the declared day. `registry.documents.expires_at` is TIMESTAMPTZ and
        // E-03's sweep compares it against `now()`; a date with no time is the start of that day,
        // which expires the document at the first moment of the day it stops being valid.
        return declared is { } day
            ? new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
            : null;
    }

    /// <summary>
    /// The fields a slot must yield before it counts as read — ocr-svc's <c>RequiredFor</c>, for
    /// the four fleet kinds.
    /// </summary>
    /// <remarks>
    /// Reproduced rather than requested from ocr-svc, because it is what this service does when
    /// ocr-svc answers nothing at all: a slot whose required keys are unknown would come back with
    /// no rows and read as verified, which is the one direction this must not fail in.
    /// </remarks>
    private static IReadOnlyList<string> RequiredFieldsFor(string kind) => kind switch
    {
        VehicleDocumentKinds.Registration => [VehicleDocumentFieldKeys.RegNoMatch],
        VehicleDocumentKinds.Insurance => [VehicleDocumentFieldKeys.InsuranceExpiry],
        VehicleDocumentKinds.RevenueLicense =>
            [VehicleDocumentFieldKeys.RevenueNo, VehicleDocumentFieldKeys.RevenueExpiry],
        VehicleDocumentKinds.Permit =>
            [VehicleDocumentFieldKeys.PermitNo, VehicleDocumentFieldKeys.PermitExpiry],
        _ => [],
    };
}
