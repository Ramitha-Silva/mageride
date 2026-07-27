namespace MageRide.ApiGateway.Attestation;

/// <summary>The outcome of one attestation check. <see cref="Reason"/> is for logs, never for the client.</summary>
/// <remarks>
/// The 401 body deliberately says only <c>attestation-failed</c>. Telling a caller <em>which</em>
/// check failed — verdict, package name, counter, key id — hands an attacker a working oracle for
/// tuning a bypass.
/// </remarks>
public readonly record struct AttestationResult(bool IsValid, string? Reason)
{
    public static AttestationResult Valid() => new(true, null);

    public static AttestationResult Invalid(string reason) => new(false, reason);
}

/// <summary>What the verifier is asked to pass judgement on.</summary>
/// <param name="Platform"><c>android</c> or <c>ios</c> (<c>X-Platform</c>).</param>
/// <param name="Token">The raw <c>X-Attestation</c> header value.</param>
/// <param name="Method">HTTP method of the protected operation.</param>
/// <param name="Path">Path of the protected operation. Binds the assertion to what it authorises.</param>
public readonly record struct AttestationRequest(string Platform, string Token, string Method, string Path);

/// <summary>
/// Verifies a client attestation for one platform (D-30). The gateway owns the enforcement point;
/// the cryptography and the provider round-trip are behind this seam so the platforms can differ
/// and so the replica can run with attestation off without the enforcement path changing shape.
/// </summary>
public interface IAttestationVerifier
{
    /// <summary><c>android</c> (Play Integrity) or <c>ios</c> (App Attest).</summary>
    string Platform { get; }

    ValueTask<AttestationResult> VerifyAsync(AttestationRequest request, CancellationToken cancellationToken);
}
