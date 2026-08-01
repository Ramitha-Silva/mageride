using System.IO.Compression;
using System.Text;
using System.Text.Json;
using MageRide.AdminBff.Persistence;
using MageRide.Shared.Http;

namespace MageRide.AdminBff.Pdpa;

/// <summary>One file inside the export archive.</summary>
/// <param name="Name">The entry name, which is also what the manifest lists it under.</param>
/// <param name="Sql">
/// A compile-time constant returning one JSON <c>text</c> column per row, bound only by
/// <c>@Id</c> (the subject) and <c>@Limit</c>. Never composed from anything a caller sent.
/// </param>
public sealed record PdpaDataset(string Name, string Sql);

/// <summary>What one assembled archive is, and what it left out.</summary>
public sealed record PdpaExportArchive(byte[] Bytes, IReadOnlyDictionary<string, int> Counts, bool Truncated);

/// <summary>
/// The export archive an E-06 request is fulfilled with (US-1.8, ADD §6 admin-bff).
/// </summary>
/// <remarks>
/// <para>
/// <b>The datasets are the platform's answer to "what do you hold about me", and the list is
/// deliberately explicit.</b> A generic "every table with a <c>user_id</c>" sweep would be shorter
/// and would be wrong twice: it would miss the rows that reach a person through a ride or a wallet
/// account rather than by a direct column, and it would include internal machinery — command logs,
/// outbox rows, intake ledgers — that is about the platform's own processing rather than about the
/// data subject. Each entry below names what it is and is columns-out rather than <c>SELECT *</c>,
/// so a column added to somebody else's table cannot silently join an export.
/// </para>
/// <para>
/// <b>The JSON is rendered by Postgres.</b> A CLR shape per dataset would be thirteen record types
/// that have to be kept in step with thirteen other services' tables, and the first one to drift
/// would silently drop a field from a statutory disclosure. <c>to_jsonb</c> over an explicit column
/// list is the same guarantee with none of the maintenance.
/// </para>
/// <para>
/// <b>Documents are listed and not enclosed.</b> A licence photograph is the subject's data and they
/// are entitled to it — but an archive assembled in memory cannot carry a folder of scans, and a
/// PDPA download that timed out would be a fulfilment that did not happen. The metadata rows carry
/// the kind, the dates and the status, and the ZIP's own <c>README</c> says the images are available
/// on request. Recorded in the handoff as the one thing this export names rather than includes.
/// </para>
/// </remarks>
internal static class PdpaExport
{
    /// <summary>
    /// Every dataset, in the order the manifest lists them.
    /// </summary>
    /// <remarks>
    /// <c>@Limit</c> is applied per dataset (<c>AdminBff:Pdpa:MaxRowsPerDataset</c>) and truncation
    /// is reported in the manifest rather than being silent — a subject given nine thousand of their
    /// ten thousand rides with no note would be given a false answer to a statutory question.
    /// </remarks>
    public static readonly IReadOnlyList<PdpaDataset> Datasets =
    [
        new("profile.json",
            """
            SELECT to_jsonb(x)::text FROM (
              SELECT u.id, u.phone, u.email, u.role, u.first_name, u.photo_url, u.language,
                     u.notif_prefs, u.default_payment_method, u.emergency_contact_name,
                     u.emergency_contact_phone, u.is_blocked, u.created_at, u.updated_at,
                     u.anonymised_at
                FROM iam.users u WHERE u.id = @Id LIMIT @Limit) x;
            """),

        new("driver-profile.json",
            """
            SELECT to_jsonb(x)::text FROM (
              SELECT p.driver_id, p.display_name, p.photo_url, p.nic_no, p.allowed_vehicle_types,
                     p.verified_at, p.rejection_reason, p.created_at
                FROM registry.driver_profiles p WHERE p.driver_id = @Id LIMIT @Limit) x;
            """),

        new("saved-addresses.json",
            """
            SELECT to_jsonb(x)::text FROM (
              SELECT a.id, a.label, a.is_home, a.is_work,
                     ST_Y(a.geo::geometry) AS lat, ST_X(a.geo::geometry) AS lng,
                     a.created_at, a.updated_at
                FROM iam.saved_addresses a WHERE a.user_id = @Id
               ORDER BY a.created_at LIMIT @Limit) x;
            """),

        new("emergency-contacts.json",
            """
            SELECT to_jsonb(x)::text FROM (
              SELECT c.id, c.name, c.phone, c.created_at
                FROM iam.emergency_contacts c WHERE c.user_id = @Id
               ORDER BY c.created_at LIMIT @Limit) x;
            """),

        // Both sides of a ride: a person may be the rider, the booker who paid for somebody else's
        // journey (P-04) or the driver who took it, and all three are their data.
        new("rides.json",
            """
            SELECT to_jsonb(x)::text FROM (
              SELECT r.id, r.state, r.kind, r.vehicle_type, r.payment_method,
                     r.fare_estimate_minor, r.created_at, r.terminal_at,
                     CASE WHEN r.passenger_id      = @Id THEN 'passenger'
                          WHEN r.booker_id         = @Id THEN 'booker'
                          WHEN r.accepted_driver_id = @Id THEN 'driver' END AS role
                FROM rides.rides r
               WHERE r.passenger_id = @Id OR r.booker_id = @Id OR r.accepted_driver_id = @Id
               ORDER BY r.created_at DESC LIMIT @Limit) x;
            """),

        new("tracking-sessions.json",
            """
            SELECT to_jsonb(x)::text FROM (
              SELECT s.id, s.vehicle_id, s.mode, s.state, s.started_at, s.ended_at
                FROM trips.sessions s WHERE s.driver_id = @Id
               ORDER BY s.started_at DESC LIMIT @Limit) x;
            """),

        new("payments.json",
            """
            SELECT to_jsonb(x)::text FROM (
              SELECT rp.id, rp.ride_id, rp.state, rp.method, rp.amount_minor, rp.surcharge_minor,
                     rp.tip_amount_minor, rp.currency, rp.payer_role, rp.attempt_no, rp.created_at
                FROM fares.ride_payments rp
                JOIN rides.rides r ON r.id = rp.ride_id
               WHERE r.passenger_id = @Id OR r.booker_id = @Id OR rp.payer_user_id = @Id
               ORDER BY rp.created_at DESC LIMIT @Limit) x;
            """),

        new("wallet.json",
            """
            SELECT to_jsonb(x)::text FROM (
              SELECT wt.id, wt.kind, wt.amount_minor, wt.balance_after_minor, wt.description, wt.ts
                FROM billing.wallet_transactions wt
                JOIN billing.accounts a ON a.id = wt.account_id
               WHERE a.owner_id = @Id
               ORDER BY wt.ts DESC LIMIT @Limit) x;
            """),

        new("daily-fees.json",
            """
            SELECT to_jsonb(x)::text FROM (
              SELECT c.fee_date, c.vehicle_id, c.amount_minor, c.currency, c.trips_that_day,
                     c.status, c.charged_at
                FROM billing.daily_fee_charges c WHERE c.driver_id = @Id
               ORDER BY c.fee_date DESC LIMIT @Limit) x;
            """),

        new("credit-transfers.json",
            """
            SELECT to_jsonb(x)::text FROM (
              SELECT t.id, t.amount_minor, t.currency, t.direction, t.status, t.created_at,
                     CASE WHEN t.sender_driver_id = @Id THEN 'sent' ELSE 'received' END AS side
                FROM billing.credit_transfers t
               WHERE t.sender_driver_id = @Id OR t.recipient_driver_id = @Id
               ORDER BY t.created_at DESC LIMIT @Limit) x;
            """),

        new("support-tickets.json",
            """
            SELECT to_jsonb(x)::text FROM (
              SELECT t.id, t.category, t.status, t.description, t.ride_id, t.created_at, t.updated_at
                FROM support.tickets t WHERE t.user_id = @Id
               ORDER BY t.created_at DESC LIMIT @Limit) x;
            """),

        new("vehicles.json",
            """
            SELECT to_jsonb(x)::text FROM (
              SELECT v.id, v.registration_number, v.vehicle_type, v.mode, v.status,
                     v.dispatch_state, v.onboarding_status, v.created_at
                FROM registry.vehicles v WHERE v.owner_id = @Id
               ORDER BY v.created_at DESC LIMIT @Limit) x;
            """),

        // Metadata only — see the remark on this class for why the images are named and not enclosed.
        new("documents.json",
            """
            SELECT to_jsonb(x)::text FROM (
              SELECT d.id, d.kind, d.status, d.vehicle_id, d.issued_at, d.expires_at, d.created_at
                FROM registry.documents d WHERE d.driver_id = @Id
               ORDER BY d.created_at DESC LIMIT @Limit) x;
            """),

        new("ratings.json",
            """
            SELECT to_jsonb(x)::text FROM (
              SELECT ra.id, ra.stars, ra.direction, ra.comment, ra.created_at
                FROM trips.ratings ra WHERE ra.ratee_id = @Id
               ORDER BY ra.created_at DESC LIMIT @Limit) x;
            """),
    ];

    /// <summary>
    /// What the archive says about itself, in plain words, as its first entry.
    /// </summary>
    /// <remarks>
    /// English only, and that is a deliberate exception to D-26's trilingual rule rather than an
    /// oversight: this file is generated server-side into a ZIP that has no locale and no client to
    /// render it, and the alternative — three copies of a compliance note in a resource bundle the
    /// Passenger App owns — would put the text somewhere no build step reaches it. The trilingual
    /// obligation is met where the subject actually reads about their request, which is the app
    /// screen that shows the status.
    /// </remarks>
    private const string Readme =
        """
        MageRide — personal data export (Sri Lanka PDPA, request E-06)

        This archive contains the personal data MageRide holds about the account named in
        manifest.json, as at the instant recorded there.

        Each .json file is a list of records from one part of the platform. manifest.json lists
        every file, how many records it holds, and whether it was truncated.

        Not included: the image files of uploaded documents (driving licence, insurance
        certificate, bank statement). documents.json lists what is on file — kind, dates and
        status — and the images themselves are available on request through support, because an
        archive assembled in one request cannot carry them reliably.

        Also not included, and deliberately: records that are about MageRide's own processing
        rather than about you — internal command logs, message outboxes and the immutable
        administrative audit trail, which a statute requires be retained.
        """;

    /// <summary>Assembles the archive. One round trip per dataset; nothing is streamed twice.</summary>
    public static async Task<PdpaExportArchive> BuildAsync(
        IPdpaRepository repository,
        Guid requestId,
        Guid userId,
        int maxRowsPerDataset,
        DateTimeOffset generatedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var truncated = new List<string>();
        var contents = new List<(string Name, byte[] Bytes)>();

        foreach (var dataset in Datasets)
        {
            // One extra row, so "there was more" is known without a second count query — the same
            // over-fetch the cursor pages use.
            var rows = await repository.ExportDatasetAsync(
                dataset.Sql, userId, maxRowsPerDataset + 1, cancellationToken);

            var complete = rows.Count <= maxRowsPerDataset;
            var kept = complete ? rows : [.. rows.Take(maxRowsPerDataset)];

            counts[dataset.Name] = kept.Count;

            if (!complete)
            {
                truncated.Add(dataset.Name);
            }

            // The rows are already JSON objects; wrapping them in an array is string work rather
            // than a parse-and-reserialise that would reformat Postgres's own rendering.
            contents.Add((dataset.Name, Encoding.UTF8.GetBytes($"[{string.Join(",\n ", kept)}]\n")));
        }

        var manifest = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                requestId,
                subjectId = userId,
                generatedAt,
                datasets = counts.Select(entry => new
                {
                    file = entry.Key,
                    records = entry.Value,
                    truncated = truncated.Contains(entry.Key, StringComparer.Ordinal),
                }),
                maxRecordsPerFile = maxRowsPerDataset,
                notes = truncated.Count == 0
                    ? "Complete."
                    : $"Truncated at {maxRowsPerDataset} records: {string.Join(", ", truncated)}. "
                      + "The remaining records are available on request.",
            },
            MageRideJson.Options);

        using var buffer = new MemoryStream();

        // `leaveOpen` so the archive's central directory is flushed by the Dispose below while the
        // stream survives to be read — the one ordering mistake that produces a ZIP nothing can open.
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            await AddAsync(archive, "README.txt", Encoding.UTF8.GetBytes(Readme), cancellationToken);
            await AddAsync(archive, "manifest.json", manifest, cancellationToken);

            foreach (var (name, bytes) in contents)
            {
                await AddAsync(archive, name, bytes, cancellationToken);
            }
        }

        return new PdpaExportArchive(buffer.ToArray(), counts, truncated.Count > 0);
    }

    private static async Task AddAsync(
        ZipArchive archive, string name, byte[] bytes, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);

        await using var stream = entry.Open();
        await stream.WriteAsync(bytes, cancellationToken);
    }
}
