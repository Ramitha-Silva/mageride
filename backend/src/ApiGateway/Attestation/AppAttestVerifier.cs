using System.Buffers.Binary;
using System.Buffers.Text;
using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;
using MageRide.Shared.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MageRide.ApiGateway.Attestation;

/// <summary>
/// iOS half of D-30. Verifies an App Attest <em>assertion</em> against the public key the device
/// registered, following Apple's documented algorithm.
/// </summary>
/// <remarks>
/// <para>
/// Header wire format (defined by this component — see <see cref="AppAttestOptions"/>):
/// <c>base64url(keyId) "." base64url(assertion CBOR)</c>.
/// </para>
/// <para>
/// The client data the assertion signs is the request binding <c>"{METHOD} {path}"</c>. That ties
/// an assertion to the operation it authorises, so an assertion captured from
/// <c>POST /v1/auth/otp/request</c> cannot be replayed onto <c>POST /v1/wallet/topup/onepay</c>.
/// Replay of the <em>same</em> operation is closed by the monotonic signature counter, which is the
/// guarantee App Attest is built around.
/// </para>
/// </remarks>
internal sealed class AppAttestVerifier(
    IOptionsMonitor<AttestationOptions> options,
    IAttestedKeyStore keyStore,
    ILogger<AppAttestVerifier> logger) : IAttestationVerifier
{
    private const int RpIdHashLength = 32;
    private const int MinimumAuthenticatorDataLength = RpIdHashLength + 1 + sizeof(uint);
    private const int MaxKeyIdLength = 128;

    private readonly IOptionsMonitor<AttestationOptions> _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly IAttestedKeyStore _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
    private readonly ILogger<AppAttestVerifier> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public string Platform => ClientPlatforms.Ios;

    public async ValueTask<AttestationResult> VerifyAsync(
        AttestationRequest request, CancellationToken cancellationToken)
    {
        var settings = _options.CurrentValue.AppAttest;

        if (string.IsNullOrWhiteSpace(settings.AppId))
        {
            return AttestationResult.Invalid("app-attest-not-configured");
        }

        if (!TrySplit(request.Token, out var keyId, out var assertionSegment))
        {
            return AttestationResult.Invalid("app-attest-malformed-header");
        }

        if (!TryDecodeBase64Url(assertionSegment, settings.MaxAssertionBytes, out var assertion))
        {
            return AttestationResult.Invalid("app-attest-malformed-assertion");
        }

        if (!TryReadAssertion(assertion, out var signature, out var authenticatorData))
        {
            return AttestationResult.Invalid("app-attest-malformed-cbor");
        }

        if (authenticatorData.Length < MinimumAuthenticatorDataLength)
        {
            return AttestationResult.Invalid("app-attest-short-authenticator-data");
        }

        var expectedRpIdHash = SHA256.HashData(Encoding.UTF8.GetBytes(settings.AppId));
        if (!CryptographicOperations.FixedTimeEquals(authenticatorData.AsSpan(0, RpIdHashLength), expectedRpIdHash))
        {
            return AttestationResult.Invalid("app-attest-rpid-mismatch");
        }

        var key = await _keyStore.GetAsync(keyId, cancellationToken).ConfigureAwait(false);
        if (key is null)
        {
            // The device has not completed App Attest registration with iam-svc, or its key was
            // revoked. Either way the edge has nothing to verify against.
            return AttestationResult.Invalid("app-attest-unknown-key");
        }

        var counter = BinaryPrimitives.ReadUInt32BigEndian(authenticatorData.AsSpan(RpIdHashLength + 1, sizeof(uint)));
        if (settings.RequireCounterIncrease && counter <= key.Counter)
        {
            return AttestationResult.Invalid("app-attest-counter-replay");
        }

        var clientDataHash = SHA256.HashData(Encoding.UTF8.GetBytes(ClientData(request)));

        Span<byte> nonceInput = authenticatorData.Length + clientDataHash.Length <= 512
            ? stackalloc byte[authenticatorData.Length + clientDataHash.Length]
            : new byte[authenticatorData.Length + clientDataHash.Length];

        authenticatorData.CopyTo(nonceInput);
        clientDataHash.CopyTo(nonceInput[authenticatorData.Length..]);

        Span<byte> nonce = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(nonceInput, nonce);

        bool verified;
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(key.PublicKeyDer, out _);
            verified = ecdsa.VerifyHash(nonce, signature, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (CryptographicException ex)
        {
            _logger.LogWarning(ex, "App Attest key {KeyId} could not be used to verify an assertion.", keyId);
            return AttestationResult.Invalid("app-attest-bad-key");
        }

        if (!verified)
        {
            return AttestationResult.Invalid("app-attest-bad-signature");
        }

        await _keyStore.AdvanceCounterAsync(keyId, counter, cancellationToken).ConfigureAwait(false);
        return AttestationResult.Valid();
    }

    /// <summary>The client data the device is expected to have signed. Must match the iOS client exactly.</summary>
    internal static string ClientData(AttestationRequest request) =>
        string.Concat(request.Method.ToUpperInvariant(), " ", request.Path);

    private static bool TrySplit(string token, out string keyId, out ReadOnlySpan<char> assertion)
    {
        keyId = string.Empty;
        assertion = default;

        var separator = token.IndexOf('.', StringComparison.Ordinal);
        if (separator <= 0 || separator > MaxKeyIdLength || separator == token.Length - 1)
        {
            return false;
        }

        keyId = token[..separator];
        assertion = token.AsSpan(separator + 1);

        // The key id is used verbatim as part of a Redis key; keep it to the base64url alphabet
        // so nothing can inject a separator into the key space.
        foreach (var c in keyId)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryDecodeBase64Url(ReadOnlySpan<char> value, int maxBytes, out byte[] decoded)
    {
        decoded = [];

        // 4 base64 characters carry 3 bytes; reject on the encoded length before allocating.
        if (value.Length > (maxBytes / 3 + 1) * 4)
        {
            return false;
        }

        var buffer = new byte[Base64Url.GetMaxDecodedLength(value.Length)];
        if (!Base64Url.TryDecodeFromChars(value, buffer, out var written) || written > maxBytes)
        {
            return false;
        }

        decoded = buffer[..written];
        return true;
    }

    private static bool TryReadAssertion(byte[] assertion, out byte[] signature, out byte[] authenticatorData)
    {
        signature = [];
        authenticatorData = [];

        try
        {
            var reader = new CborReader(assertion, CborConformanceMode.Lax);
            var count = reader.ReadStartMap();

            for (var i = 0; count is null || i < count; i++)
            {
                if (reader.PeekState() == CborReaderState.EndMap)
                {
                    break;
                }

                var name = reader.ReadTextString();
                switch (name)
                {
                    case "signature":
                        signature = reader.ReadByteString();
                        break;
                    case "authenticatorData":
                        authenticatorData = reader.ReadByteString();
                        break;
                    default:
                        reader.SkipValue();
                        break;
                }
            }

            reader.ReadEndMap();
        }
        catch (CborContentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        return signature.Length > 0 && authenticatorData.Length > 0;
    }
}
