using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MageRide.Provisioning.Configuration;
using MageRide.Provisioning.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MageRide.Provisioning.Credentials;

/// <summary>
/// The embedded issuer: a two-tier ECDSA P-256 CA kept in step-ca's own on-disk layout (T-02).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why embedded.</b> D6' §4.2 names "step-ca + Vault PKI". A step-ca container is a second
/// service to run, health-check and bootstrap before a single tracker can be provisioned, and the
/// dev compose already reserves the volume for it. This mints the same shapes — root →
/// intermediate → 90-day client leaf, ECDSA P-256, exactly step-ca's defaults — into
/// <c>$STEPPATH/certs</c> and <c>$STEPPATH/secrets</c>, so pointing
/// <see cref="DevicePkiOptions.Url"/> at a real step-ca later is a configuration change and not a
/// migration of key material. A configured URL is refused at start-up rather than ignored: a
/// deployment that set it believes its root key is in step-ca's store and not on a Docker volume.
/// </para>
/// <para>
/// <b>The CN is the authorisation boundary.</b> A leaf's subject is <c>CN={vehicleId}</c> because
/// <c>infra/deploy/emqx/emqx.conf</c> gives the 8883 listener
/// <c>peer_cert_as_username = cn</c> and <c>acl.conf</c> writes every device rule as
/// <c>veh/${username}/*</c>. The certificate is therefore not just proof of identity, it *is* the
/// topic grant, which is why nothing else may set the subject.
/// </para>
/// <para>
/// <b>The root private key is on disk, unencrypted.</b> That is what a single-process embedded CA
/// with no operator at start-up amounts to, and it is stated here rather than implied: file mode
/// 0600 under <c>secrets/</c> is the whole protection, the directory is a dedicated volume, and
/// D7' §13's answer (Vault) is C125's. Anybody holding that file can mint a credential for any
/// vehicle.
/// </para>
/// </remarks>
public sealed class EmbeddedStepCa : ICertificateAuthority, IDisposable
{
    /// <summary>step-ca's own paths, relative to <c>$STEPPATH</c>.</summary>
    internal const string RootCertificateFile = "certs/root_ca.crt";
    internal const string RootKeyFile = "secrets/root_ca_key";
    internal const string IntermediateCertificateFile = "certs/intermediate_ca.crt";
    internal const string IntermediateKeyFile = "secrets/intermediate_ca_key";

    /// <summary>Not step-ca's — the trust bundle EMQX loads, and the PSK signing key.</summary>
    internal const string CaChainFile = "certs/ca_chain.crt";
    internal const string PskSigningKeyFile = "secrets/psk_signing_key";

    /// <summary>Version tag on a PSK token, so a format change is detectable rather than silent.</summary>
    private const string PskPrefix = "mrp1";

    private const string ClientAuthOid = "1.3.6.1.5.5.7.3.2";

    /// <summary>
    /// How far a freshly minted certificate is backdated, for a tracker whose RTC has drifted.
    /// </summary>
    /// <remarks>
    /// A GT06-class unit with a flat backup cell comes up believing it is some time in 2016, and a
    /// certificate that is not yet valid by the device's clock fails the handshake with an error
    /// indistinguishable from a bad chain. Five minutes covers ordinary drift; a device that is
    /// hours out needs its clock, not a longer window.
    /// </remarks>
    private static readonly TimeSpan ClockSkewAllowance = TimeSpan.FromMinutes(5);

    private readonly DevicePkiOptions _options;
    private readonly ILogger<EmbeddedStepCa> _logger;
    private readonly X509Certificate2 _root;
    private readonly X509Certificate2 _intermediate;
    private readonly byte[] _pskSigningKey;

    public EmbeddedStepCa(IOptions<DevicePkiOptions> options, ILogger<EmbeddedStepCa> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options.Value;
        _logger = logger;

        if (!string.IsNullOrWhiteSpace(_options.Url))
        {
            throw new InvalidOperationException(
                $"StepCa:Url is set to '{_options.Url}' but C030 ships the embedded issuer only. " +
                "Leaving it set would mint device credentials from a key on the local volume while " +
                "the deployment believes they come from step-ca. Unset it, or land the step-ca client first.");
        }

        var root = Path.GetFullPath(_options.RootKeyPath);
        Directory.CreateDirectory(Path.Combine(root, "certs"));
        CreateSecretsDirectory(Path.Combine(root, "secrets"));

        (_root, _intermediate) = LoadOrCreate(root);

        RootCertificatePem = _root.ExportCertificatePem();
        CaChainPem = _intermediate.ExportCertificatePem() + '\n' + RootCertificatePem + '\n';

        // Written on every start so a directory restored from a partial backup, or one whose chain
        // predates a re-issued intermediate, converges rather than leaving EMQX trusting a root
        // that no longer signs anything this process mints.
        WriteFile(Path.Combine(root, CaChainFile), CaChainPem, secret: false);

        _pskSigningKey = LoadOrCreatePskKey(Path.Combine(root, PskSigningKeyFile));
    }

    public string RootCertificatePem { get; }

    public string CaChainPem { get; }

    public DeviceCredential Issue(string credentialType, Guid vehicleId, string imei, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialType);
        Imeis.Require(imei);

        if (vehicleId == Guid.Empty)
        {
            throw new ArgumentException(
                "A credential is scoped to a vehicle: the CN becomes the MQTT username the ACL is written against.",
                nameof(vehicleId));
        }

        var expiresAt = now.AddDays(_options.RotationDays);

        // Rotation happens before expiry, not at it. The replacement is minted while the outgoing
        // credential still works, so a tracker that has been out of coverage for a fortnight can
        // still reconnect and collect it (DevicePkiOptions.RotationLeadTime).
        var rotatesAt = expiresAt - _options.RotationLeadTime;
        if (rotatesAt <= now)
        {
            rotatesAt = now;
        }

        return credentialType switch
        {
            CredentialTypes.X509 => IssueCertificate(vehicleId, imei, now, expiresAt, rotatesAt),
            CredentialTypes.Psk => IssuePsk(imei, now, expiresAt, rotatesAt),
            _ => throw new ArgumentException($"'{credentialType}' is not a credential type.", nameof(credentialType)),
        };
    }

    public byte[] BuildCrl(
        IReadOnlyCollection<RevokedCredential> revoked, long crlNumber, DateTimeOffset now, TimeSpan validFor)
    {
        ArgumentNullException.ThrowIfNull(revoked);

        var builder = new CertificateRevocationListBuilder();

        foreach (var entry in revoked)
        {
            // A PSK serial is ours alone and was never an X.509 serial, so it has no place on a
            // CRL — a verifier would be asked to check a number no certificate ever carried. PSK
            // revocation travels the Redis channel and the validate endpoint instead.
            if (!TryParseSerial(entry.Serial, out var serialBytes))
            {
                continue;
            }

            builder.AddEntry(serialBytes, entry.RevokedAt, ToRevocationReason(entry.Reason));
        }

        return builder.Build(
            _intermediate,
            new BigInteger(crlNumber),
            now + validFor,
            HashAlgorithmName.SHA256,
            rsaSignaturePadding: null,
            thisUpdate: now);
    }

    public bool TryReadPsk(string? token, string imei, DateTimeOffset now, out string serial)
    {
        serial = string.Empty;

        if (string.IsNullOrWhiteSpace(token) || !Imeis.IsValid(imei))
        {
            return false;
        }

        var parts = token.Split('.');
        if (parts.Length != 5 || !string.Equals(parts[0], PskPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        if (!long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var expiresUnix))
        {
            return false;
        }

        if (DateTimeOffset.FromUnixTimeSeconds(expiresUnix) <= now)
        {
            return false;
        }

        byte[] presented;
        try
        {
            presented = Base64Url.Decode(parts[4]);
        }
        catch (FormatException)
        {
            return false;
        }

        var expected = SignPsk(parts[1], imei, expiresUnix, parts[3]);

        // Fixed-time: the signature is the only thing standing between a guessed token and a
        // device identity, and a comparison that returns early leaks it a byte at a time.
        if (!CryptographicOperations.FixedTimeEquals(presented, expected))
        {
            return false;
        }

        serial = parts[1];
        return true;
    }

    public void Dispose()
    {
        _root.Dispose();
        _intermediate.Dispose();
        CryptographicOperations.ZeroMemory(_pskSigningKey);
    }

    /// <summary>SHA-256 of a credential's secret half — what <c>pem_or_token_hash</c> stores.</summary>
    internal static byte[] HashMaterial(string material) => SHA256.HashData(Encoding.UTF8.GetBytes(material));

    private DeviceCredential IssueCertificate(
        Guid vehicleId, string imei, DateTimeOffset now, DateTimeOffset expiresAt, DateTimeOffset rotatesAt)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var request = new CertificateRequest(
            new X500DistinguishedName($"CN={vehicleId}"), key, HashAlgorithmName.SHA256);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyAgreement, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid(ClientAuthOid, "Client Authentication")], critical: false));

        var names = new SubjectAlternativeNameBuilder();
        names.AddUri(new Uri($"urn:mageride:imei:{imei}"));
        request.CertificateExtensions.Add(names.Build());

        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
        request.CertificateExtensions.Add(
            X509AuthorityKeyIdentifierExtension.CreateFromCertificate(
                _intermediate, includeKeyIdentifier: true, includeIssuerAndSerial: false));

        if (!string.IsNullOrWhiteSpace(_options.CrlDistributionPoint))
        {
            // Only when configured. EMQX's `enable_crl_check` refuses a certificate whose CRL it
            // cannot fetch, so a CDP written into every certificate in a deployment that does not
            // serve one would be a foot-gun waiting for somebody to turn the check on.
            request.CertificateExtensions.Add(
                CertificateRevocationListBuilder.BuildCrlDistributionPointExtension([CrlUrl()]));
        }

        var serial = NewSerialNumber();

        // notBefore is backdated a little: a tracker's RTC drifts, and a certificate that is not
        // yet valid by the device's clock fails a handshake with an error indistinguishable from a
        // bad chain.
        //
        // Clamped to the issuer's own notBefore, and that clamp is load-bearing rather than
        // defensive. A CA written by `openssl req -x509` — which is how dev-up.sh and a real
        // step-ca both create one — is valid from *now*, with no backdating of its own, so for the
        // first five minutes of a fresh stack every mint would be refused outright.
        var notBefore = now - ClockSkewAllowance;
        var issuerNotBefore = new DateTimeOffset(_intermediate.NotBefore.ToUniversalTime());

        using var certificate = request.Create(
            _intermediate, notBefore < issuerNotBefore ? issuerNotBefore : notBefore, expiresAt, serial);

        var pem = string.Concat(
            key.ExportPkcs8PrivateKeyPem(), '\n',
            certificate.ExportCertificatePem(), '\n',
            _intermediate.ExportCertificatePem(), '\n');

        return new DeviceCredential(
            FormatSerial(serial),
            CredentialTypes.X509,
            now,
            expiresAt,
            rotatesAt,
            ClientCertPem: pem,
            PskToken: null,
            MaterialHash: SHA256.HashData(certificate.RawData));
    }

    private DeviceCredential IssuePsk(string imei, DateTimeOffset now, DateTimeOffset expiresAt, DateTimeOffset rotatesAt)
    {
        var serial = FormatSerial(NewSerialNumber());
        var expiresUnix = expiresAt.ToUnixTimeSeconds();

        // The secret the device HMACs its (IMEI + nonce) with at connect time (ADD §7.7.3). The
        // signature beside it is over the secret, so an adapter can tell a token this CA minted
        // from one somebody made up without holding a per-device record.
        var secret = Base64Url.Encode(RandomNumberGenerator.GetBytes(32));
        var signature = Base64Url.Encode(SignPsk(serial, imei, expiresUnix, secret));

        var token = string.Join(
            '.', PskPrefix, serial, expiresUnix.ToString(CultureInfo.InvariantCulture), secret, signature);

        return new DeviceCredential(
            serial,
            CredentialTypes.Psk,
            now,
            expiresAt,
            rotatesAt,
            ClientCertPem: null,
            PskToken: token,
            MaterialHash: HashMaterial(token));
    }

    private byte[] SignPsk(string serial, string imei, long expiresUnix, string secret)
    {
        // The IMEI is inside the signature, so a token minted for one device cannot be replayed by
        // another that copied it — the adapter checks the signature against the IMEI on the wire.
        var payload = Encoding.UTF8.GetBytes(
            string.Join('|', PskPrefix, serial, imei, expiresUnix.ToString(CultureInfo.InvariantCulture), secret));

        return HMACSHA256.HashData(_pskSigningKey, payload);
    }

    private string CrlUrl() => _options.CrlDistributionPoint!.TrimEnd('/') + "/v1/internal/trackers/crl.der";

    private (X509Certificate2 Root, X509Certificate2 Intermediate) LoadOrCreate(string root)
    {
        var rootCertPath = Path.Combine(root, RootCertificateFile);
        var rootKeyPath = Path.Combine(root, RootKeyFile);
        var intermediateCertPath = Path.Combine(root, IntermediateCertificateFile);
        var intermediateKeyPath = Path.Combine(root, IntermediateKeyFile);

        if (File.Exists(rootCertPath) && File.Exists(rootKeyPath)
            && File.Exists(intermediateCertPath) && File.Exists(intermediateKeyPath))
        {
            _logger.LogInformation("Device CA loaded from {Path}", root);

            return (
                X509Certificate2.CreateFromPemFile(rootCertPath, rootKeyPath),
                X509Certificate2.CreateFromPemFile(intermediateCertPath, intermediateKeyPath));
        }

        // Partial material is not repaired by generating the missing half — an intermediate whose
        // root is gone signs certificates nothing can chain, and a root whose intermediate is gone
        // would silently start a second issuing tier that already-deployed devices do not trust.
        if (File.Exists(rootCertPath) || File.Exists(intermediateCertPath))
        {
            throw new InvalidOperationException(
                $"The device CA under '{root}' is incomplete: some of certs/root_ca.crt, secrets/root_ca_key, " +
                "certs/intermediate_ca.crt and secrets/intermediate_ca_key are present and some are not. " +
                "Restore the volume or empty it; generating the missing half would issue certificates no " +
                "already-provisioned tracker can chain.");
        }

        return CreateAuthority(root, rootCertPath, rootKeyPath, intermediateCertPath, intermediateKeyPath);
    }

    private (X509Certificate2 Root, X509Certificate2 Intermediate) CreateAuthority(
        string root, string rootCertPath, string rootKeyPath, string intermediateCertPath, string intermediateKeyPath)
    {
        var now = DateTimeOffset.UtcNow;

        using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var rootRequest = new CertificateRequest(
            new X500DistinguishedName($"CN={_options.RootCommonName}"), rootKey, HashAlgorithmName.SHA256);

        rootRequest.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(true, hasPathLengthConstraint: true, pathLengthConstraint: 1, critical: true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
        rootRequest.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(rootRequest.PublicKey, critical: false));

        using var rootCertificate = rootRequest.CreateSelfSigned(
            now - TimeSpan.FromHours(1), now.AddYears(_options.RootValidityYears));

        using var intermediateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var intermediateRequest = new CertificateRequest(
            new X500DistinguishedName($"CN={_options.IntermediateCommonName}"), intermediateKey, HashAlgorithmName.SHA256);

        intermediateRequest.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(true, hasPathLengthConstraint: true, pathLengthConstraint: 0, critical: true));
        intermediateRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
        intermediateRequest.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(intermediateRequest.PublicKey, critical: false));
        intermediateRequest.CertificateExtensions.Add(
            X509AuthorityKeyIdentifierExtension.CreateFromCertificate(
                rootCertificate, includeKeyIdentifier: true, includeIssuerAndSerial: false));

        // The intermediate outlives the longest credential it will ever sign, and by a margin: a
        // leaf issued the day before the intermediate expires is valid for 90 days that its issuer
        // is not, and every one of those handshakes fails.
        using var intermediateCertificate = intermediateRequest.Create(
            rootCertificate,
            now - TimeSpan.FromHours(1),
            now.AddYears(Math.Max(1, _options.RootValidityYears / 2)),
            NewSerialNumber());

        WriteFile(rootCertPath, rootCertificate.ExportCertificatePem(), secret: false);
        WriteFile(rootKeyPath, rootKey.ExportPkcs8PrivateKeyPem(), secret: true);
        WriteFile(intermediateCertPath, intermediateCertificate.ExportCertificatePem(), secret: false);
        WriteFile(intermediateKeyPath, intermediateKey.ExportPkcs8PrivateKeyPem(), secret: true);

        _logger.LogWarning(
            "No device CA under {Path}; generated a new root and issuing intermediate. Every credential " +
            "minted before this point is now untrusted, and the broker's cacertfile must be reloaded " +
            "from {RootFile}.",
            root,
            RootCertificateFile);

        return (
            X509Certificate2.CreateFromPemFile(rootCertPath, rootKeyPath),
            X509Certificate2.CreateFromPemFile(intermediateCertPath, intermediateKeyPath));
    }

    private byte[] LoadOrCreatePskKey(string path)
    {
        if (File.Exists(path))
        {
            return Base64Url.Decode(File.ReadAllText(path).Trim());
        }

        var key = RandomNumberGenerator.GetBytes(32);
        WriteFile(path, Base64Url.Encode(key), secret: true);

        return key;
    }

    private static void CreateSecretsDirectory(string path)
    {
        var directory = Directory.CreateDirectory(path);

        if (!OperatingSystem.IsWindows())
        {
            directory.UnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        }
    }

    private static void WriteFile(string path, string contents, bool secret)
    {
        File.WriteAllText(path, contents);

        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // 0600 for key material; 0644 for certificates, because the EMQX container reads
        // certs/root_ca.crt off the same volume as a different uid.
        File.SetUnixFileMode(
            path,
            secret
                ? UnixFileMode.UserRead | UnixFileMode.UserWrite
                : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
    }

    /// <summary>16 random bytes, forced positive — a negative DER serial is malformed.</summary>
    private static byte[] NewSerialNumber()
    {
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;

        // An all-zero leading byte after masking would be stripped by some encoders and change the
        // serial's identity between what is stored and what is on the wire.
        if (serial[0] == 0)
        {
            serial[0] = 0x01;
        }

        return serial;
    }

    internal static string FormatSerial(byte[] serial) => Convert.ToHexString(serial);

    internal static bool TryParseSerial(string? serial, out byte[] bytes)
    {
        bytes = [];

        if (string.IsNullOrWhiteSpace(serial) || serial.Length % 2 != 0)
        {
            return false;
        }

        try
        {
            bytes = Convert.FromHexString(serial);
            return bytes.Length > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static X509RevocationReason ToRevocationReason(string? reason) => reason switch
    {
        RevocationReasons.KeyCompromise => X509RevocationReason.KeyCompromise,
        RevocationReasons.AffiliationChanged => X509RevocationReason.AffiliationChanged,
        RevocationReasons.Superseded => X509RevocationReason.Superseded,
        RevocationReasons.CessationOfOperation => X509RevocationReason.CessationOfOperation,
        _ => X509RevocationReason.Unspecified,
    };
}

/// <summary>Base64url without padding — the encoding a token can carry in a URL or a CSV cell.</summary>
internal static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Decode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", 0 => string.Empty, _ => throw new FormatException("Not base64url.") };

        return Convert.FromBase64String(padded);
    }
}
