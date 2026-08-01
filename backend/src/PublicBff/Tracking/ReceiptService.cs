using MageRide.PublicBff.Configuration;
using MageRide.PublicBff.Domain;
using MageRide.PublicBff.Endpoints;
using MageRide.PublicBff.Persistence;
using MageRide.Shared.Errors;
using MageRide.Shared.Storage;
using Microsoft.Extensions.Options;

namespace MageRide.PublicBff.Tracking;

/// <summary>
/// SCR-WT-005 — what happened, and how the platform knows (US-25.6).
/// </summary>
/// <remarks>
/// <b>Every value is derived and none is stored.</b> There is no receipt table and no
/// <c>proof</c> column: the outcome is read off the ride's terminal, the settled payment attempt and
/// the presence of a delivery photograph, so a receipt reprinted a year later says the same thing as
/// the one the recipient saw, and the platform keeps no second copy of a number the ledger owns.
/// </remarks>
public interface IReceiptService
{
    Task<ReceiptResponse> BuildAsync(ShareToken share, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IReceiptService"/>
internal sealed class ReceiptService(
    ITrackService tracking,
    ITrackReadRepository rides,
    IObjectStore objects,
    IOptions<PublicBffOptions> options,
    ILogger<ReceiptService> logger) : IReceiptService
{
    private readonly PublicBffOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<ReceiptResponse> BuildAsync(ShareToken share, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(share);

        // A `pickup_confirm` token names a location request and never a ride: there is no journey to
        // receipt, and there never will be through this link.
        if (share.Scope is ShareTokenScopes.PickupConfirm)
        {
            throw new MageRideException(
                MageRideErrors.ReceiptNotReady, "This link is a pickup request, not a journey.");
        }

        var ride = await tracking.RequireRideAsync(share, cancellationToken);

        var receipt = await rides.FindReceiptAsync(ride.RideId, cancellationToken)
                      ?? throw new MageRideException(
                          MageRideErrors.TokenExpiredOrRevoked, "This link no longer has anything to show.");

        if (!RideStates.Receiptable(receipt.State) || receipt.CompletedAt is not { } completedAt)
        {
            throw new MageRideException(
                MageRideErrors.ReceiptNotReady,
                "This journey has not finished yet. The receipt appears when it does.");
        }

        return new ReceiptResponse(
            Kind: receipt.Kind == 2 ? "package" : "ride",
            State: receipt.State,
            TotalMinor: receipt.SettledMinor,

            // Omitted with the amount rather than defaulted: a currency beside no figure says
            // nothing, and the schema pairs them.
            Currency: receipt.SettledMinor is null ? null : receipt.Currency,
            Proof: ProofOf(receipt),
            ProofPhotoUrl: PresignedProof(receipt),
            Driver: new PublicDriverResponse(
                ride.DriverName!, ride.DriverPhotoUrl, ride.VehicleType, ride.RegistrationNumber!,

                // **No number on a finished journey.** AL-48's `tel:` link exists so a recipient can
                // reach the driver who is on the way to them; once the parcel is delivered there is
                // nothing to call about, and a receipt is a document that gets forwarded.
                Phone: null),
            CompletedAt: completedAt);
    }

    /// <summary>
    /// How the handoff was evidenced, in the order the questions are asked.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A dispute outranks everything.</b> P-14's uncollected-COD terminal and any other route to
    /// <c>Disputed</c> are the ride's own verdict, and a receipt that reported a successful handover
    /// on a disputed delivery would be the platform contradicting its own ledger.
    /// </para>
    /// <para>
    /// <b>Then the money, then the evidence.</b> P-08's collection is a fact about cash and P-10's
    /// photograph is a fact about a doorstep; a COD parcel that was both photographed and paid for
    /// reports the payment, because that is the question a receipt is opened to answer.
    /// <c>CashSettled</c> and <c>CashOnDeliveryCollected</c> are both <c>cod_collected</c>: they are
    /// the same event on a ride and on a delivery, and US-25.6's four values have no other name for
    /// "the driver was handed cash".
    /// </para>
    /// <para>
    /// <b>Otherwise the code was read out.</b> On a package that is literally P-07's delivery OTP.
    /// On a proxy ride it is the weakest of the four — <b>a ride has no handoff artefact at all</b>,
    /// and US-25.6's vocabulary is a delivery's applied to both kinds by a screen headed "Delivered /
    /// Trip Summary". Recorded as a spec gap in the C066 handoff; what it means here is "the journey
    /// ended the ordinary way", which the <c>state</c> beside it says precisely.
    /// </para>
    /// </remarks>
    private static string ProofOf(TrackedReceipt receipt) => receipt.State switch
    {
        RideStates.Disputed => ReceiptProofs.Disputed,
        RideStates.CashOnDeliveryCollected or RideStates.CashSettled => ReceiptProofs.CodCollected,
        _ when receipt.ProofPhotoUrl is { Length: > 0 } => ReceiptProofs.PhotoProof,
        _ => ReceiptProofs.OtpVerified,
    };

    /// <summary>
    /// A short-lived signed URL, or nothing.
    /// </summary>
    /// <remarks>
    /// <b>The stored pointer is never returned.</b> <c>rides.proof_artifacts.storage_url</c> is an
    /// <c>s3://</c> or <c>file://</c> pointer into D-36's bucket; handing it to a browser would
    /// publish a key rather than a photograph. The signature is the provider's, its TTL is the
    /// provider's, and a deployment with no bucket presigns nothing and the field is absent — which
    /// is the honest answer, because the photograph is genuinely not reachable from a browser there.
    /// </remarks>
    private string? PresignedProof(TrackedReceipt receipt)
    {
        if (receipt.ProofPhotoUrl is not { Length: > 0 } pointer)
        {
            return null;
        }

        if (objects.TryPresign(pointer, _options.ProofPhotoUrlTtl, out var url))
        {
            return url;
        }

        logger.LogInformation(
            "A delivery photograph could not be presigned ({Store}); the receipt omits it.", objects.Description);

        return null;
    }
}
