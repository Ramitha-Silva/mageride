using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MageRide.Provisioning.Configuration;
using MageRide.Provisioning.Credentials;
using MageRide.Provisioning.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MageRide.Provisioning.Tests.Credentials;

/// <summary>
/// The embedded issuer (T-02, ADD §7.7.3). Everything here is provable without a broker: a
/// certificate either chains to the root and carries the right subject or it does not.
/// </summary>
public sealed class DevicePkiTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "mageride-pki-" + Guid.NewGuid().ToString("N")[..12]);

    private readonly List<EmbeddedStepCa> _authorities = [];

    /// <summary>
    /// <b>The CN is the authorisation boundary.</b> <c>emqx.conf</c> gives the tracker listener
    /// <c>peer_cert_as_username = cn</c> and <c>acl.conf</c> writes every device rule as
    /// <c>veh/${username}/*</c>, so the subject is not merely an identifier — it is the topic
    /// grant. Nothing else may set it.
    /// </summary>
    [Fact]
    public void An_x509_credential_is_subject_to_its_vehicle_and_names_its_imei()
    {
        var vehicleId = Guid.NewGuid();
        const string imei = "359586015829435";

        var credential = Create().Issue(CredentialTypes.X509, vehicleId, imei, DateTimeOffset.UtcNow);

        using var leaf = ReadLeaf(credential.ClientCertPem!);

        Assert.Equal($"CN={vehicleId}", leaf.Subject);
        // Formatted rather than enumerated: X509SubjectAlternativeNameExtension exposes DNS and
        // IP names as typed collections but leaves a URI name to the formatter.
        Assert.Contains(
            $"urn:mageride:imei:{imei}",
            string.Join(
                ' ',
                leaf.Extensions
                    .OfType<X509SubjectAlternativeNameExtension>()
                    .Select(extension => extension.Format(multiLine: false))),
            StringComparison.Ordinal);
    }

    [Fact]
    public void An_x509_credential_chains_to_the_root_through_the_intermediate()
    {
        var authority = Create();
        var credential = authority.Issue(CredentialTypes.X509, Guid.NewGuid(), "359586015829435", DateTimeOffset.UtcNow);

        using var leaf = ReadLeaf(credential.ClientCertPem!);
        using var chain = new X509Chain();

        // The broker is given the root as its `cacertfile`, so that is what has to be the trust
        // anchor here too. Revocation is checked from the CRL EMQX fetches, not from this chain.
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.CustomTrustStore.Add(X509Certificate2.CreateFromPem(authority.RootCertificatePem));

        foreach (var extra in ReadChain(credential.ClientCertPem!).Skip(1))
        {
            chain.ChainPolicy.ExtraStore.Add(extra);
        }

        Assert.True(
            chain.Build(leaf),
            string.Join("; ", chain.ChainStatus.Select(status => status.StatusInformation)));
    }

    /// <summary>A client certificate that is not marked for client authentication is not one.</summary>
    [Fact]
    public void An_x509_credential_is_marked_for_client_authentication_and_is_not_a_ca()
    {
        var credential = Create().Issue(CredentialTypes.X509, Guid.NewGuid(), "359586015829435", DateTimeOffset.UtcNow);

        using var leaf = ReadLeaf(credential.ClientCertPem!);

        Assert.Contains(
            "1.3.6.1.5.5.7.3.2",
            leaf.Extensions.OfType<X509EnhancedKeyUsageExtension>()
                .SelectMany(extension => extension.EnhancedKeyUsages.Cast<Oid>())
                .Select(oid => oid.Value));

        Assert.All(
            leaf.Extensions.OfType<X509BasicConstraintsExtension>(),
            constraints => Assert.False(constraints.CertificateAuthority));
    }

    /// <summary>
    /// The bundle carries the key and the issuing intermediate, not just the leaf. A leaf alone is
    /// unusable — the device has no key for it and the broker cannot chain it to the root.
    /// </summary>
    [Fact]
    public void The_returned_bundle_carries_the_private_key_and_the_intermediate()
    {
        var credential = Create().Issue(CredentialTypes.X509, Guid.NewGuid(), "359586015829435", DateTimeOffset.UtcNow);

        Assert.Contains("BEGIN PRIVATE KEY", credential.ClientCertPem);
        Assert.Equal(2, ReadChain(credential.ClientCertPem!).Count);
        Assert.Null(credential.PskToken);
    }

    /// <summary>D6' §4.2 and D7' §4.2's <c>Cred__RotationDays</c>, and the overlap that makes it safe.</summary>
    [Fact]
    public void A_credential_lives_ninety_days_and_is_rotated_before_it_expires()
    {
        var now = DateTimeOffset.UtcNow;
        var credential = Create().Issue(CredentialTypes.X509, Guid.NewGuid(), "359586015829435", now);

        Assert.Equal(90, (credential.ExpiresAt - now).TotalDays, 0.01);
        Assert.True(
            credential.RotatesAt < credential.ExpiresAt,
            "rotation must be minted while the outgoing credential still works, or a tracker out of coverage is bricked");
        Assert.Equal(14, (credential.ExpiresAt - credential.RotatesAt).TotalDays, 0.01);
    }

    [Fact]
    public void Two_credentials_never_share_a_serial()
    {
        var authority = Create();
        var now = DateTimeOffset.UtcNow;

        var serials = Enumerable.Range(0, 50)
            .Select(_ => authority.Issue(CredentialTypes.X509, Guid.NewGuid(), "359586015829435", now).Serial)
            .ToArray();

        Assert.Equal(serials.Length, serials.Distinct(StringComparer.Ordinal).Count());
    }

    // -------------------------------------------------------------------------------------
    // PSK — the legacy TCP half (ADD §7.7.3)
    // -------------------------------------------------------------------------------------

    [Fact]
    public void A_psk_token_verifies_offline_for_the_imei_it_was_minted_for()
    {
        var authority = Create();
        var now = DateTimeOffset.UtcNow;
        const string imei = "359586015829435";

        var credential = authority.Issue(CredentialTypes.Psk, Guid.NewGuid(), imei, now);

        Assert.True(authority.TryReadPsk(credential.PskToken, imei, now, out var serial));
        Assert.Equal(credential.Serial, serial);
        Assert.Null(credential.ClientCertPem);
    }

    /// <summary>
    /// The IMEI is inside the signature, so a token lifted off one device cannot be replayed by
    /// another that copied it — the adapter checks the signature against the IMEI on the wire.
    /// </summary>
    [Fact]
    public void A_psk_token_does_not_verify_for_another_imei()
    {
        var authority = Create();
        var now = DateTimeOffset.UtcNow;

        var credential = authority.Issue(CredentialTypes.Psk, Guid.NewGuid(), "359586015829435", now);

        Assert.False(authority.TryReadPsk(credential.PskToken, "359586015829436", now, out _));
    }

    [Fact]
    public void A_psk_token_stops_verifying_at_its_expiry()
    {
        var authority = Create();
        var now = DateTimeOffset.UtcNow;
        const string imei = "359586015829435";

        var credential = authority.Issue(CredentialTypes.Psk, Guid.NewGuid(), imei, now);

        Assert.False(authority.TryReadPsk(credential.PskToken, imei, credential.ExpiresAt.AddSeconds(1), out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("mrp1.AABB.9999999999.secret.signature")]
    public void A_forged_or_malformed_psk_token_is_refused(string? token) =>
        Assert.False(Create().TryReadPsk(token, "359586015829435", DateTimeOffset.UtcNow, out _));

    /// <summary>A token minted by a different CA is not one of ours.</summary>
    [Fact]
    public void A_psk_token_from_another_authority_is_refused()
    {
        var now = DateTimeOffset.UtcNow;
        const string imei = "359586015829435";

        var theirs = Create(Path.Combine(_directory, "other")).Issue(CredentialTypes.Psk, Guid.NewGuid(), imei, now);

        Assert.False(Create().TryReadPsk(theirs.PskToken, imei, now, out _));
    }

    /// <summary>
    /// <c>prov.device_certs.pem_or_token_hash</c>'s comment is "never the credential itself".
    /// </summary>
    [Fact]
    public void Only_a_hash_of_the_secret_half_is_handed_to_the_caller_for_storage()
    {
        var credential = Create().Issue(CredentialTypes.Psk, Guid.NewGuid(), "359586015829435", DateTimeOffset.UtcNow);

        Assert.Equal(32, credential.MaterialHash.Length);
        Assert.DoesNotContain(
            Convert.ToHexString(credential.MaterialHash),
            credential.PskToken,
            StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------------------------------
    // CRL — the MQTT half of T-12
    // -------------------------------------------------------------------------------------

    [Fact]
    public void The_crl_carries_a_revoked_certificate_and_is_signed_by_the_issuing_intermediate()
    {
        var authority = Create();
        var now = DateTimeOffset.UtcNow;

        var credential = authority.Issue(CredentialTypes.X509, Guid.NewGuid(), "359586015829435", now);

        var der = authority.BuildCrl(
            [new RevokedCredential(credential.Serial, now, RevocationReasons.CessationOfOperation)],
            crlNumber: now.ToUnixTimeSeconds(),
            now,
            TimeSpan.FromHours(1));

        Assert.NotEmpty(der);

        // The serial is DER-encoded inside the list, so its bytes appear verbatim.
        Assert.Contains(
            Convert.ToHexString(der),
            new[] { Convert.ToHexString(der) }.Where(hex => hex.Contains(credential.Serial, StringComparison.Ordinal)));
    }

    /// <summary>
    /// A PSK serial was never an X.509 serial, so putting one on a CRL would ask a verifier to
    /// check a number no certificate ever carried. PSK revocation travels the Redis channel and the
    /// validate endpoint instead.
    /// </summary>
    [Fact]
    public void A_crl_over_no_certificates_is_still_a_valid_empty_list()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.NotEmpty(Create().BuildCrl([], now.ToUnixTimeSeconds(), now, TimeSpan.FromHours(1)));
    }

    // -------------------------------------------------------------------------------------
    // The CA's own lifecycle
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// A restart must not re-root the platform. Every certificate already on a tracker chains to
    /// the root on disk, and generating a new one would make all of them untrusted at once.
    /// </summary>
    [Fact]
    public void A_second_start_over_the_same_directory_reuses_the_root()
    {
        var first = Create().RootCertificatePem;
        var second = Create().RootCertificatePem;

        Assert.Equal(first, second);
    }

    [Fact]
    public void The_material_lands_in_step_cas_own_layout()
    {
        Create();

        Assert.True(File.Exists(Path.Combine(_directory, "certs/root_ca.crt")));
        Assert.True(File.Exists(Path.Combine(_directory, "secrets/root_ca_key")));
        Assert.True(File.Exists(Path.Combine(_directory, "certs/intermediate_ca.crt")));
        Assert.True(File.Exists(Path.Combine(_directory, "secrets/intermediate_ca_key")));

        // Not step-ca's, but what the broker mounts as its cacertfile.
        Assert.True(File.Exists(Path.Combine(_directory, "certs/ca_chain.crt")));
    }

    /// <summary>
    /// Half a CA is not repaired by generating the other half: an intermediate whose root is gone
    /// signs certificates nothing can chain, and a root whose intermediate is gone would start a
    /// second issuing tier that already-deployed devices do not trust.
    /// </summary>
    [Fact]
    public void A_partially_restored_directory_stops_the_process_rather_than_regenerating()
    {
        Create();
        File.Delete(Path.Combine(_directory, "secrets/intermediate_ca_key"));

        var thrown = Assert.Throws<InvalidOperationException>(() => Create());

        Assert.Contains("incomplete", thrown.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A configured <c>StepCa:Url</c> is refused rather than ignored: a deployment that set it
    /// believes its root key is in step-ca's store and not on a Docker volume.
    /// </summary>
    [Fact]
    public void A_configured_step_ca_url_is_refused_rather_than_silently_ignored()
    {
        var thrown = Assert.Throws<InvalidOperationException>(
            () => Create(configure: options => options.Url = "https://step-ca:9000"));

        Assert.Contains("embedded issuer", thrown.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_unknown_credential_type_is_a_programming_error_not_a_default()
    {
        Assert.Throws<ArgumentException>(
            () => Create().Issue("magic-beans", Guid.NewGuid(), "359586015829435", DateTimeOffset.UtcNow));
    }

    public void Dispose()
    {
        foreach (var authority in _authorities)
        {
            authority.Dispose();
        }

        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private EmbeddedStepCa Create(string? directory = null, Action<DevicePkiOptions>? configure = null)
    {
        var options = new DevicePkiOptions { RootKeyPath = directory ?? _directory };
        configure?.Invoke(options);

        var authority = new EmbeddedStepCa(Options.Create(options), NullLogger<EmbeddedStepCa>.Instance);
        _authorities.Add(authority);

        return authority;
    }

    private static X509Certificate2 ReadLeaf(string bundle) => ReadChain(bundle)[0];

    private static List<X509Certificate2> ReadChain(string bundle)
    {
        var certificates = new List<X509Certificate2>();
        var remaining = bundle.AsSpan();

        while (PemEncoding.TryFind(remaining, out var fields))
        {
            var label = remaining[fields.Label].ToString();

            if (label == "CERTIFICATE")
            {
                certificates.Add(X509Certificate2.CreateFromPem(remaining[fields.Location]));
            }

            remaining = remaining[fields.Location.End..];
        }

        return certificates;
    }
}
