using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using MageRide.Provisioning.Configuration;
using MageRide.Provisioning.Credentials;
using MageRide.Provisioning.Domain;
using MageRide.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MageRide.Provisioning.Tests.Credentials;

/// <summary>
/// The CA is created outside this service and loaded by it, so the two writers have to agree.
/// </summary>
/// <remarks>
/// <para>
/// There are two of them and neither is <c>EmbeddedStepCa</c>: <c>infra/scripts/ensure-device-ca.sh</c>
/// writes the CA with <c>openssl</c> before the dev stack comes up, and
/// <c>MageRide.TestKit.DeviceCa</c> writes it before <c>EmqxFixture</c>'s broker starts. Both exist
/// for the same hard reason — EMQX reads its <c>cacertfile</c> when the 8883 listener starts, and
/// a service cannot create the file in time.
/// </para>
/// <para>
/// The failure this rules out is quiet and total: a key format the loader cannot read means
/// provisioning-svc will not start in the dev stack at all, and nothing in this suite would
/// otherwise notice, because the suite's own CA comes from the other writer.
/// </para>
/// </remarks>
public sealed class GeneratedCaLoadTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "mageride-generated-ca-" + Guid.NewGuid().ToString("N")[..12]);

    [Fact]
    public void The_testkit_writes_a_ca_the_service_loads_and_mints_from()
    {
        DeviceCa.Create(_directory);

        AssertMintsAChainedCredential();
    }

    /// <summary>
    /// The dev-stack path, run against the real script rather than a copy of it — a comment saying
    /// "PKCS#8" is not evidence that `openssl` wrote one.
    /// </summary>
    [Fact]
    public void The_device_ca_script_writes_a_ca_the_service_loads_and_mints_from()
    {
        // Δ C124: this used to read `infra/scripts/dev-up.sh`, slice out the lines between the
        // device-CA comment and the next bare `fi`, and run the fragment. The generation moved to
        // its own script when it turned out slim-verify.sh needed it too — CI brings the slim stack
        // up without dev-up.sh, so it had no CA and EMQX never booted — and the extraction then
        // found nothing and this test went red. Running the REAL script is both the fix and a
        // better test: a reconstructed fragment can pass while the script it came from is broken.
        var script = RepositoryFile("infra/scripts/ensure-device-ca.sh");
        Assert.SkipWhen(script is null, "infra/scripts/ensure-device-ca.sh was not found from the test output directory.");
        Assert.SkipWhen(!HasOpenssl(), "openssl is not on PATH.");

        Directory.CreateDirectory(_directory);

        // REPO_ROOT is honoured by the script when it is already set, so this writes into the
        // throwaway tree rather than the working copy.
        Run("bash", script!, ("REPO_ROOT", _directory));

        // The script writes into $REPO_ROOT/infra/deploy/device-ca; that is what a deployment
        // mounts at StepCa:RootKeyPath.
        AssertMintsAChainedCredential(Path.Combine(_directory, "infra", "deploy", "device-ca"));
    }

    private void AssertMintsAChainedCredential(string? directory = null)
    {
        var root = directory ?? _directory;

        using var authority = new EmbeddedStepCa(
            Options.Create(new DevicePkiOptions { RootKeyPath = root }), NullLogger<EmbeddedStepCa>.Instance);

        var vehicleId = Guid.NewGuid();
        var credential = authority.Issue(CredentialTypes.X509, vehicleId, "359586015829435", DateTimeOffset.UtcNow);

        using var leaf = X509Certificate2.CreateFromPem(credential.ClientCertPem);
        Assert.Equal($"CN={vehicleId}", leaf.Subject);

        // The CA it loaded is the one on disk — not a second one it quietly generated because the
        // key was unreadable, which is the failure that would take the dev stack down.
        var onDisk = File.ReadAllText(Path.Combine(root, "certs", "root_ca.crt")).Trim();
        Assert.Equal(onDisk, authority.RootCertificatePem.Trim());

        // And the chain EMQX was given verifies the leaf it just minted.
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.CustomTrustStore.ImportFromPemFile(Path.Combine(root, "certs", "ca_chain.crt"));

        Assert.True(
            chain.Build(leaf),
            string.Join("; ", chain.ChainStatus.Select(status => status.StatusInformation)));
    }

    private static bool HasOpenssl()
    {
        try
        {
            return Run("openssl", "version") == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static int Run(string file, string arguments, params (string Key, string Value)[] environment)
    {
        var start = new ProcessStartInfo(file, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var (key, value) in environment)
        {
            start.Environment[key] = value;
        }

        using var process = Process.Start(start)!;
        process.WaitForExit(TimeSpan.FromSeconds(60));

        return process.ExitCode;
    }

    /// <summary>
    /// Walks up from the test output directory to the repository root.
    /// </summary>
    /// <remarks>
    /// The script is read from the source tree rather than copied into the output, because a copy
    /// is exactly what this test exists to rule out: it has to assert against the file the dev
    /// stack runs.
    /// </remarks>
    private static string? RepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);

            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
