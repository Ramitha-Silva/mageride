using System.Buffers.Binary;
using System.Buffers.Text;
using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;
using MageRide.ApiGateway.Attestation;
using MageRide.ApiGateway.Tests.Infrastructure;
using MageRide.Shared.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace MageRide.ApiGateway.Tests;

/// <summary>
/// The iOS half of D-30, exercised against assertions built here with a real P-256 key — the same
/// bytes <c>DCAppAttestService.generateAssertion</c> produces on a device.
/// </summary>
public sealed class AppAttestVerifierTests
{
    private const string AppId = "ABCDE12345.lk.mageride.driver";
    private const string Path = "/v1/sos";
    private const string Method = "POST";

    [Fact]
    public async Task A_genuine_assertion_is_accepted()
    {
        var device = new FakeDevice(AppId);
        var (verifier, store) = Build(device, counter: 0);

        var result = await verifier.VerifyAsync(Request(device.Assert(Method, Path, counter: 1)), CancellationToken.None);

        Assert.True(result.IsValid, result.Reason);

        // The replay counter moved, which is what makes the next replay detectable.
        var stored = await store.GetAsync(device.KeyId, CancellationToken.None);
        Assert.Equal(1u, stored!.Counter);
    }

    [Fact]
    public async Task A_replayed_assertion_is_refused()
    {
        var device = new FakeDevice(AppId);
        var (verifier, _) = Build(device, counter: 0);

        var token = device.Assert(Method, Path, counter: 1);

        Assert.True((await verifier.VerifyAsync(Request(token), CancellationToken.None)).IsValid);

        var replay = await verifier.VerifyAsync(Request(token), CancellationToken.None);
        Assert.False(replay.IsValid);
        Assert.Equal("app-attest-counter-replay", replay.Reason);
    }

    [Fact]
    public async Task An_assertion_for_another_operation_is_refused()
    {
        // The signed client data is "{METHOD} {path}", so an assertion captured from the SOS call
        // cannot be lifted onto a wallet top-up.
        var device = new FakeDevice(AppId);
        var (verifier, _) = Build(device, counter: 0);

        var token = device.Assert(Method, Path, counter: 1);
        var elsewhere = new AttestationRequest(ClientPlatforms.Ios, token, "POST", "/v1/wallet/topup/onepay");

        var result = await verifier.VerifyAsync(elsewhere, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal("app-attest-bad-signature", result.Reason);
    }

    [Fact]
    public async Task An_assertion_minted_for_another_app_is_refused()
    {
        var device = new FakeDevice("ZZZZZ99999.com.example.other");
        var (verifier, _) = Build(device, counter: 0);

        var result = await verifier.VerifyAsync(Request(device.Assert(Method, Path, counter: 1)), CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal("app-attest-rpid-mismatch", result.Reason);
    }

    [Fact]
    public async Task A_tampered_signature_is_refused()
    {
        var device = new FakeDevice(AppId);
        var (verifier, _) = Build(device, counter: 0);

        var result = await verifier.VerifyAsync(
            Request(device.Assert(Method, Path, counter: 1, corruptSignature: true)), CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal("app-attest-bad-signature", result.Reason);
    }

    [Fact]
    public async Task An_unregistered_key_is_refused()
    {
        var device = new FakeDevice(AppId);
        var verifier = new AppAttestVerifier(
            new TestOptionsMonitor<AttestationOptions>(Options()),
            new InMemoryAttestedKeyStore(),
            NullLogger<AppAttestVerifier>.Instance);

        var result = await verifier.VerifyAsync(Request(device.Assert(Method, Path, counter: 1)), CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal("app-attest-unknown-key", result.Reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("no-separator")]
    [InlineData(".assertion-with-no-key-id")]
    [InlineData("key-id-with-no-assertion.")]
    [InlineData("has/illegal+chars.YWJj")]
    public async Task A_malformed_header_is_refused_before_any_lookup(string token)
    {
        var device = new FakeDevice(AppId);
        var (verifier, _) = Build(device, counter: 0);

        var result = await verifier.VerifyAsync(
            new AttestationRequest(ClientPlatforms.Ios, token, Method, Path), CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal("app-attest-malformed-header", result.Reason);
    }

    [Fact]
    public async Task An_unconfigured_verifier_fails_closed()
    {
        var device = new FakeDevice(AppId);
        var options = Options();
        options.AppAttest.AppId = string.Empty;

        var verifier = new AppAttestVerifier(
            new TestOptionsMonitor<AttestationOptions>(options),
            new InMemoryAttestedKeyStore(),
            NullLogger<AppAttestVerifier>.Instance);

        var result = await verifier.VerifyAsync(Request(device.Assert(Method, Path, counter: 1)), CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal("app-attest-not-configured", result.Reason);
    }

    private static AttestationRequest Request(string token) => new(ClientPlatforms.Ios, token, Method, Path);

    private static AttestationOptions Options() => new()
    {
        AppAttest = { AppId = AppId, RequireCounterIncrease = true },
    };

    private static (AppAttestVerifier Verifier, InMemoryAttestedKeyStore Store) Build(FakeDevice device, uint counter)
    {
        var store = new InMemoryAttestedKeyStore();
        store.Register(new AttestedKey(device.KeyId, device.PublicKeyDer, counter));

        var verifier = new AppAttestVerifier(
            new TestOptionsMonitor<AttestationOptions>(Options()),
            store,
            NullLogger<AppAttestVerifier>.Instance);

        return (verifier, store);
    }

    /// <summary>
    /// Produces the wire format the gateway defines — <c>base64url(keyId) "." base64url(assertion)</c>
    /// — over a genuine ECDSA P-256 assertion, following Apple's assertion algorithm.
    /// </summary>
    private sealed class FakeDevice
    {
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly string _appId;

        public FakeDevice(string appId)
        {
            _appId = appId;
            PublicKeyDer = _key.ExportSubjectPublicKeyInfo();
            KeyId = Base64Url.EncodeToString(SHA256.HashData(PublicKeyDer));
        }

        public byte[] PublicKeyDer { get; }

        public string KeyId { get; }

        public string Assert(string method, string path, uint counter, bool corruptSignature = false)
        {
            var authenticatorData = new byte[37];
            SHA256.HashData(Encoding.UTF8.GetBytes(_appId)).CopyTo(authenticatorData, 0);
            authenticatorData[32] = 0x40;
            BinaryPrimitives.WriteUInt32BigEndian(authenticatorData.AsSpan(33), counter);

            var clientDataHash = SHA256.HashData(Encoding.UTF8.GetBytes($"{method} {path}"));

            var nonceInput = new byte[authenticatorData.Length + clientDataHash.Length];
            authenticatorData.CopyTo(nonceInput, 0);
            clientDataHash.CopyTo(nonceInput, authenticatorData.Length);

            var signature = _key.SignHash(SHA256.HashData(nonceInput), DSASignatureFormat.Rfc3279DerSequence);
            if (corruptSignature)
            {
                signature[^1] ^= 0xFF;
            }

            var writer = new CborWriter();
            writer.WriteStartMap(2);
            writer.WriteTextString("signature");
            writer.WriteByteString(signature);
            writer.WriteTextString("authenticatorData");
            writer.WriteByteString(authenticatorData);
            writer.WriteEndMap();

            return string.Concat(KeyId, ".", Base64Url.EncodeToString(writer.Encode()));
        }
    }
}
