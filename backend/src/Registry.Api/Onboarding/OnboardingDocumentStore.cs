using Dapper;
using MageRide.Registry.Configuration;
using MageRide.Shared.Errors;
using MageRide.Shared.Persistence;
using MageRide.Shared.Storage;
using Microsoft.Extensions.Options;

namespace MageRide.Registry.Onboarding;

/// <summary>
/// AL-43's capture provenance, as <c>docs.uploads.captured_via</c> stores it.
/// </summary>
/// <remarks>
/// <para>
/// <b>A gallery pick is the fraud signal the verification queue sorts on.</b> The in-app scanner
/// (SCR-DA/DI-005) photographs the document in front of the driver; a gallery pick is a file that
/// was already on the handset, which is how a licence belonging to somebody else arrives. The two
/// are the same bytes and the officer has to be able to tell them apart.
/// </para>
/// <para>
/// <c>other</c> is deliberately not accepted here. It is the Fleet Portal's value — a desktop
/// browser file picker, which is neither of these — and an onboarding capture that claimed it
/// would be saying "not from a phone" about something that came from one.
/// </para>
/// </remarks>
public static class OnboardingCaptureSources
{
    /// <summary>The in-app camera document-scanner with the drag-crop quad (AL-43).</summary>
    public const string CameraDragCrop = "camera_dragcrop";

    /// <summary>Picked from the handset's gallery. Permitted, and flagged.</summary>
    public const string Gallery = "gallery";

    /// <summary>The two an onboarding upload may declare.</summary>
    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { CameraDragCrop, Gallery };
}

/// <summary>
/// The <c>docs.uploads.kind</c> values this surface writes that are not
/// <see cref="MageRide.Registry.Domain.DocumentKinds"/>.
/// </summary>
/// <remarks>
/// <c>registry.documents.kind</c> carries a CHECK constraint and its set is fixed;
/// <c>docs.uploads.kind</c> is deliberately free text, because — in migration 1301's own words —
/// "the set grows with every onboarding surface". A driver's profile photo is an upload and is
/// **not** a document: it is shown to passengers (US-2.12), no officer verifies it, and it never
/// becomes a <c>registry.documents</c> row.
/// </remarks>
public static class OnboardingUploadKinds
{
    /// <summary>The avatar Profile Setup requires. Not evidence; not reviewed.</summary>
    public const string ProfilePhoto = "profile_photo";
}

/// <summary>
/// Writes an onboarding document's bytes and its <c>docs.uploads</c> row (D-36, AL-43, NFR-28).
/// </summary>
/// <remarks>
/// <para>
/// <b>Δ MCS-01 — this is the upload surface that did not exist.</b> `PUT /v1/drivers/profile` and
/// `PUT /v1/vehicles/{id}/onboarding/{step}` both take already-uploaded <c>docs.uploads</c> ids and
/// nothing on the platform created one for an onboarding document: the only writer in this service
/// was <see cref="MageRide.Registry.Vehicles.IPayoutDocumentStore"/>, scoped to the three AL-58
/// payout kinds, and this suite's own harness seeded the row directly with the comment "as the
/// upload surface would. No service owns that table yet." Both screens were unreachable on a real
/// gateway as a result.
/// </para>
/// <para>
/// <b>This does not move the D-36 perimeter.</b> registry-svc holds the bytes exactly long enough
/// to put them in the bucket; the redaction pre-pass and the external model are still ocr-svc's,
/// and ocr-svc still fetches by <c>storage_url</c> rather than being handed an image. What changed
/// is who fills the table, not who reads the picture. fleet-svc already owns the identical surface
/// for SCR-FP-004's vehicle documents (<c>VehicleDocumentRepository.CreateUploadAsync</c>), so the
/// platform rule is "the service that owns the document's record owns its upload", and this makes
/// registry-svc obey it for the documents it already owns.
/// </para>
/// <para>
/// <b>The bytes are written before the row</b>, which is the ordering both existing writers use and
/// for the same reason: a crash between them leaves an orphan object that NFR-28's deadline
/// reclaims, while the other order leaves an onboarding step pointing at a document an officer is
/// told exists and cannot open.
/// </para>
/// <para>
/// <b>Retention is null for exactly one kind (Δ MCS-25).</b> Every document that reaches this store
/// is raw identity evidence and carries NFR-28's deadline — except the profile photo, which the
/// platform keeps <em>serving</em> as the driver's avatar (US-2.12). That is the same exception the
/// payout store makes for a driver's LankaQR (AL-59) and it is made the same way: null retention,
/// the <c>retained/</c> prefix, a NULL <c>auto_delete_at</c>, and a key of its own.
///
/// <b>What this costs, stated plainly.</b> Nothing on this platform can delete an object —
/// <see cref="IObjectStore"/> has no delete, so removal is the bucket lifecycle rule and nothing
/// else — and PDPA erasure clears <c>driver_profiles.photo_url</c> without touching
/// <c>docs.uploads</c>. A retained photo therefore outlives an erasure request as unreferenced
/// bytes. That was already true of the LankaQR; this extends it to a face. Closing it means a
/// delete on the store and an erasure hook that uses it, and is worth doing.
/// </para>
/// </remarks>
public interface IOnboardingDocumentStore
{
    /// <summary>Stores one document for <paramref name="ownerId"/> and returns its upload id.</summary>
    /// <param name="capturedVia">One of <see cref="OnboardingCaptureSources"/>. Required — see there.</param>
    Task<Guid> WriteAsync(
        Guid ownerId,
        string kind,
        string capturedVia,
        Stream content,
        string contentType,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IOnboardingDocumentStore"/>
internal sealed class OnboardingDocumentStore(
    INpgsqlConnectionFactory connections,
    IObjectStore objects,
    IOptions<RegistryOptions> options) : IOnboardingDocumentStore
{
    private readonly RegistryOptions _options = options.Value;

    public async Task<Guid> WriteAsync(
        Guid ownerId,
        string kind,
        string capturedVia,
        Stream content,
        string contentType,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (!OnboardingCaptureSources.All.Contains(capturedVia))
        {
            // Not defaulted, on purpose. Defaulting to `camera_dragcrop` would record a scan that
            // did not happen and erase AL-43's signal; defaulting to `gallery` would flag every
            // honest capture from a client that forgot the field. The client knows; make it say.
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["capturedVia"] =
                [
                    $"capturedVia must be one of {string.Join(", ", OnboardingCaptureSources.All.Order(StringComparer.Ordinal))}.",
                ],
            });
        }

        var uploadId = Guid.CreateVersion7();

        // Δ MCS-25 — the profile photo is the one document here the platform keeps SERVING.
        //
        // Everything else in this store is evidence somebody checked once: a licence is read, its
        // fields are extracted, and NFR-28 then wants the image gone. The profile photo is read
        // once too, but it is also the avatar SCR-DA/DI-029 and -036 draw and the face US-2.12 has
        // a passenger recognise their driver by — so expiring it turns a working screen into a
        // broken one ninety days after Profile Setup, with nothing to re-upload from.
        //
        // This is `PayoutDocumentStore`'s LankaQR exception, for the same reason and by the same
        // mechanism: null retention puts the object under the `retained/` prefix, which the NFR-28
        // lifecycle rule does not match, and leaves `docs.uploads.auto_delete_at` NULL.
        //
        // The key is separate too, so the two classes are distinguishable in the bucket without
        // joining back to Postgres.
        var isRetained = string.Equals(kind, OnboardingUploadKinds.ProfilePhoto, StringComparison.Ordinal);

        TimeSpan? retention = isRetained ? null : _options.OnboardingDocumentRetention;

        var stored = await objects.PutAsync(
            new ObjectPutRequest(
                // Built from ids this service minted, never from the client's filename.
                isRetained
                    ? $"registry/driver-photo/{ownerId:N}/{uploadId:N}"
                    : $"registry/onboarding/{ownerId:N}/{uploadId:N}",
                content,
                contentType,
                _options.OnboardingDocumentMaxBytes,
                retention),
            cancellationToken);

        await using var connection = await connections.OpenAsync(cancellationToken);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO docs.uploads (id, owner_id, storage_url, sha256, kind, captured_via, auto_delete_at)
            VALUES (@Id, @OwnerId, @StorageUrl, @Sha256, @Kind, @CapturedVia,
                    CASE WHEN @Retention::interval IS NULL THEN NULL ELSE now() + @Retention END);
            """,
            new
            {
                Id = uploadId,
                OwnerId = ownerId,
                StorageUrl = stored.StorageUrl,
                Sha256 = stored.Sha256,
                Kind = kind,
                CapturedVia = capturedVia,
                Retention = retention,
            },
            cancellationToken: cancellationToken));

        return uploadId;
    }
}
