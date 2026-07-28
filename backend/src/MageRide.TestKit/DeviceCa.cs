using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace MageRide.TestKit;

/// <summary>
/// A two-tier ECDSA device CA on disk, in step-ca's layout — what
/// <c>infra/scripts/dev-up.sh</c> generates before the stack comes up (C030, T-02).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the TestKit generates it rather than provisioning-svc.</b> EMQX loads its
/// <c>cacertfile</c> when the 8883 listener starts, and the listener starts before any service
/// does — so the CA has to exist before the broker, exactly as it does in the dev stack, where
/// <c>dev-up.sh</c> writes it and both the <c>emqx</c> container and <c>app-services</c> mount it.
/// provisioning-svc's <c>EmbeddedStepCa</c> then <i>loads</i> the material rather than creating
/// it, which is the same code path a deployment takes.
/// </para>
/// <para>
/// The layout is step-ca's (<c>certs/</c>, <c>secrets/</c>) for the reason
/// <c>DevicePkiOptions.RootKeyPath</c> gives: swapping the embedded issuer for a real step-ca
/// should be a configuration change and not a migration of key material.
/// </para>
/// </remarks>
public static class DeviceCa
{
    /// <summary>The trust bundle EMQX takes as its <c>cacertfile</c> — intermediate, then root.</summary>
    public const string ChainFile = "certs/ca_chain.crt";

    /// <summary>Writes a fresh root and issuing intermediate into <paramref name="directory"/>.</summary>
    /// <remarks>Idempotent: a directory that already holds a complete CA is left alone, so a
    /// fixture restart does not re-root every credential a running suite has minted.</remarks>
    public static string Create(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var root = Path.GetFullPath(directory);
        Directory.CreateDirectory(Path.Combine(root, "certs"));
        Directory.CreateDirectory(Path.Combine(root, "secrets"));

        if (File.Exists(Path.Combine(root, ChainFile)))
        {
            return root;
        }

        var now = DateTimeOffset.UtcNow;

        using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var rootRequest = new CertificateRequest(
            new X500DistinguishedName("CN=MageRide Device Root CA"), rootKey, HashAlgorithmName.SHA256);

        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 1, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        rootRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(rootRequest.PublicKey, false));

        using var rootCertificate = rootRequest.CreateSelfSigned(now.AddHours(-1), now.AddYears(10));

        using var intermediateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var intermediateRequest = new CertificateRequest(
            new X500DistinguishedName("CN=MageRide Device Issuing CA"), intermediateKey, HashAlgorithmName.SHA256);

        intermediateRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        intermediateRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        intermediateRequest.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(intermediateRequest.PublicKey, false));
        intermediateRequest.CertificateExtensions.Add(
            X509AuthorityKeyIdentifierExtension.CreateFromCertificate(rootCertificate, true, false));

        using var intermediateCertificate = intermediateRequest.Create(
            rootCertificate, now.AddHours(-1), now.AddYears(5), RandomSerial());

        Write(Path.Combine(root, "certs/root_ca.crt"), rootCertificate.ExportCertificatePem(), secret: false);
        Write(Path.Combine(root, "secrets/root_ca_key"), rootKey.ExportPkcs8PrivateKeyPem(), secret: true);
        Write(
            Path.Combine(root, "certs/intermediate_ca.crt"),
            intermediateCertificate.ExportCertificatePem(),
            secret: false);
        Write(
            Path.Combine(root, "secrets/intermediate_ca_key"),
            intermediateKey.ExportPkcs8PrivateKeyPem(),
            secret: true);

        Write(
            Path.Combine(root, ChainFile),
            intermediateCertificate.ExportCertificatePem() + '\n' + rootCertificate.ExportCertificatePem() + '\n',
            secret: false);

        return root;
    }

    private static byte[] RandomSerial()
    {
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] = (byte)(serial[0] & 0x7F | 0x01);
        return serial;
    }

    private static void Write(string path, string contents, bool secret)
    {
        File.WriteAllText(path, contents);

        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // 0644 for certificates: the EMQX container reads the chain off this directory as a
        // different uid, and a 0600 file is a listener that silently fails to start.
        File.SetUnixFileMode(
            path,
            secret
                ? UnixFileMode.UserRead | UnixFileMode.UserWrite
                : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
    }
}
