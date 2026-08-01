using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MageRide.PublicBff.Upstream;

/// <summary>
/// The <c>Idempotency-Key</c> this service sends upstream when the browser sent none.
/// </summary>
/// <remarks>
/// <para>
/// <b>public-bff owns no command log, and that is why this exists.</b> Every write on this surface
/// is somebody else's: ride-svc resolves the location request and safety-svc records the alert, and
/// both dedupe on the header (R-14). A BFF that minted a fresh key per call would defeat the
/// dedupe of the two services it forwards to — and on the SOS route that means a double-tapped panic
/// button sending two messages, which is the exact failure safety-svc's own file names.
/// </para>
/// <para>
/// <b>A caller's key always wins.</b> <c>public-bff.yaml</c> declares the header on all three POSTs,
/// so a client that sends one gets end-to-end replay. These derivations are the floor for a page
/// that does not — and an unauthenticated page opened on a bad connection is exactly where a retry
/// is likeliest.
/// </para>
/// <para>
/// <b>The key is a business fact, not a nonce</b>, which is the shape admin-bff's ledger key uses.
/// It carries no secret: the token is hashed rather than embedded, so an <c>Idempotency-Key</c>
/// travelling to another service in a log line is not a credential.
/// </para>
/// </remarks>
internal static class PublicIdempotency
{
    /// <summary>
    /// The pickup answer: one per token per verb, stable for ever.
    /// </summary>
    /// <remarks>
    /// A location request can be answered exactly once — the token burns on use and ride-svc's own
    /// guarded update refuses a second — so a stable key makes a retried tap a replay rather than a
    /// refusal the rider has to read.
    /// </remarks>
    public static string ForPickup(string token, string verb) => Derive($"pickup:{verb}:{token}");

    /// <summary>
    /// The alert: one per token per window.
    /// </summary>
    /// <remarks>
    /// <b>Windowed rather than stable, and both halves matter.</b> Stable would make a second
    /// genuine SOS twenty minutes later replay the first and send nobody anything. Fresh per call
    /// would make a double tap two messages. The window is the width of a double tap.
    /// </remarks>
    public static string ForSos(string token, DateTimeOffset now, TimeSpan window) =>
        Derive(string.Create(
            CultureInfo.InvariantCulture,
            $"sos:{now.ToUnixTimeSeconds() / (long)Math.Max(1, window.TotalSeconds)}:{token}"));

    /// <summary>Base64url over SHA-256, 43 characters — inside D3' §0's `[A-Za-z0-9_-]{16,128}`.</summary>
    private static string Derive(string material) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(material)))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
