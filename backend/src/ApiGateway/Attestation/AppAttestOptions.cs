using System.ComponentModel.DataAnnotations;

namespace MageRide.ApiGateway.Attestation;

/// <summary>
/// App Attest settings for the iOS half of D-30.
/// </summary>
/// <remarks>
/// <para>
/// Apple publishes no server API for App Attest: the relying party verifies the assertion itself
/// against the public key it kept when the device first attested. That registration step is
/// iam-svc's (<c>iam.devices.attestation_verified_at</c>, <c>server_db_schema.md</c> §1); the
/// gateway only reads the resulting key through <see cref="IAttestedKeyStore"/>.
/// </para>
/// <para>
/// <b>Spec gap.</b> No spec defines the <c>X-Attestation</c> wire format, so this component
/// defines one and it needs a micro-change-set into D3' §0: iOS sends
/// <c>base64url(keyId) "." base64url(assertion)</c>. Android sends the Play Integrity token
/// unwrapped, which needs no encoding.
/// </para>
/// </remarks>
public sealed class AppAttestOptions
{
    /// <summary>
    /// The App Attest relying-party id: <c>&lt;TeamID&gt;.&lt;bundle id&gt;</c>, e.g.
    /// <c>ABCDE12345.lk.mageride.driver</c>. Its SHA-256 must equal the assertion's
    /// <c>rpIdHash</c>, which is what stops an assertion minted for another app being replayed here.
    /// </summary>
    public string AppId { get; set; } = string.Empty;

    /// <summary>Ceiling on the decoded assertion, before any parsing runs on it.</summary>
    [Range(64, 65536)]
    public int MaxAssertionBytes { get; set; } = 4096;

    /// <summary>
    /// Require the assertion's signature counter to be strictly greater than the last one stored
    /// for the key. This is the whole replay defence: an assertion captured off the wire is
    /// worthless the moment the genuine device signs anything else. Off only for a local harness.
    /// </summary>
    public bool RequireCounterIncrease { get; set; } = true;
}
