namespace MageRide.Provisioning.Credentials;

/// <summary>
/// The device PKI (T-02, ADD §7.7.3): mints per-device credentials and publishes the revocation
/// list the MQTT broker checks them against.
/// </summary>
/// <remarks>
/// <para>
/// An interface rather than a concrete class because D6' §4.2 names <b>step-ca + Vault PKI</b> as
/// the issuer and C030 ships an embedded one. The seam is where a real step-ca goes; everything
/// above it — the binding, the rotation sweep, the CRL publication — is written against this and
/// does not know which is behind it.
/// </para>
/// <para>
/// Nothing here touches the database. A credential is minted first and recorded afterwards, in
/// the caller's transaction, so a mint that succeeds against a transaction that then rolls back
/// leaves an unreferenced serial rather than a binding pointing at a credential that was never
/// issued. The CRL is the reconciliation: a serial nothing points at is not on it, so it can
/// never authenticate anything.
/// </para>
/// </remarks>
public interface ICertificateAuthority
{
    /// <summary>The root certificate, PEM. What EMQX loads as its <c>cacertfile</c>.</summary>
    string RootCertificatePem { get; }

    /// <summary>Root and issuing intermediate, PEM, root last — a trust bundle for a verifier.</summary>
    string CaChainPem { get; }

    /// <summary>
    /// Mints a credential for one device.
    /// </summary>
    /// <param name="credentialType"><see cref="Domain.CredentialTypes.X509"/> or
    /// <see cref="Domain.CredentialTypes.Psk"/>.</param>
    /// <param name="vehicleId">Goes in the certificate's <c>CN</c>. <b>Load-bearing:</b> the 8883
    /// listener derives the MQTT username from the CN (<c>peer_cert_as_username = cn</c>) and
    /// <c>acl.conf</c> writes every device rule in terms of <c>veh/${username}/*</c>, so the CN is
    /// what confines a tracker to its own vehicle's topics.</param>
    /// <param name="imei">Goes in the subject alternative names, so the credential and the device
    /// it was issued for can be reconciled from the certificate alone.</param>
    DeviceCredential Issue(string credentialType, Guid vehicleId, string imei, DateTimeOffset now);

    /// <summary>
    /// Builds a signed CRL over <paramref name="revoked"/>, DER-encoded.
    /// </summary>
    /// <param name="crlNumber">Monotonic. A verifier caching a CRL uses this to tell a newer list
    /// from a replayed older one, so it must never go backwards.</param>
    byte[] BuildCrl(IReadOnlyCollection<RevokedCredential> revoked, long crlNumber, DateTimeOffset now, TimeSpan validFor);

    /// <summary>
    /// Verifies a PSK token offline and reports the serial it names.
    /// </summary>
    /// <remarks>
    /// This is what makes the PSK "signed" (D6' §4.2) rather than merely random: an adapter
    /// holding the signing key can reject a forged or expired token without a network call, and
    /// spends the round trip to <c>/v1/internal/trackers/{imei}/validate</c> on the one question it
    /// cannot answer locally — whether the credential has since been revoked.
    /// </remarks>
    bool TryReadPsk(string? token, string imei, DateTimeOffset now, out string serial);
}
