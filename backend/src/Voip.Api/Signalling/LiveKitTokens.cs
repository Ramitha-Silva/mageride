using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MageRide.Voip.Configuration;
using Microsoft.Extensions.Options;

namespace MageRide.Voip.Signalling;

/// <summary>A minted LiveKit access token and where to present it.</summary>
public sealed record SignallingToken(string RoomName, string Token, string WsUrl, DateTimeOffset ExpiresAt);

/// <summary>Mints LiveKit access tokens (D-24, D6' §6).</summary>
public interface ILiveKitTokenMinter
{
    /// <summary>Whether LiveKit is configured at all.</summary>
    bool IsConfigured { get; }

    /// <summary>The websocket endpoint clients connect to.</summary>
    string WsUrl { get; }

    /// <summary>
    /// A join token for one identity in one room.
    /// </summary>
    /// <param name="identity">
    /// The LiveKit participant identity. This is what P-05 is enforced through: for the passenger
    /// side it is the <em>rider's</em> user id, never the booker's.
    /// </param>
    /// <param name="ttl">How long the token may be presented for. Bounded by the options.</param>
    SignallingToken Mint(string roomName, string identity, string displayName, TimeSpan ttl);

    /// <summary>A short-lived admin token for the server API (room teardown).</summary>
    string MintAdminToken(string roomName, TimeSpan ttl);
}

/// <summary>
/// LiveKit access tokens are plain HS256 JWTs, and this mints them by hand.
/// </summary>
/// <remarks>
/// <para>
/// <b>Hand-rolled rather than through <c>JsonWebTokenHandler</c>, for one reason:</b> a LiveKit
/// token's authority lives in a <em>nested object</em> claim (<c>video</c>), and the
/// claims-dictionary APIs stringify nested values or flatten them into dotted names depending on
/// the path taken through them. A token whose <c>video</c> grant arrives as the string
/// <c>"{\"roomJoin\":true}"</c> is accepted by nothing and fails at join time with an error about
/// permissions rather than about serialisation. Sixty lines of `Base64Url(header).Base64Url(payload)`
/// with an HMAC over them is the whole format, and it is exactly testable.
/// </para>
/// <para>
/// <b>The secret never leaves this class</b> and is never logged. The token it produces is a
/// bearer credential for a room that carries two people's conversation.
/// </para>
/// </remarks>
public sealed class LiveKitTokenMinter : ILiveKitTokenMinter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly VoipOptions _options;
    private readonly TimeProvider _clock;

    public LiveKitTokenMinter(IOptions<VoipOptions> options, TimeProvider clock)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_options.LiveKit.ApiKey)
        && !string.IsNullOrWhiteSpace(_options.LiveKit.ApiSecret)
        && !string.IsNullOrWhiteSpace(_options.LiveKit.WsUrl);

    public string WsUrl => _options.LiveKit.WsUrl ?? string.Empty;

    public SignallingToken Mint(string roomName, string identity, string displayName, TimeSpan ttl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roomName);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);

        var expiresAt = _clock.GetUtcNow() + ttl;

        var grant = new VideoGrant
        {
            Room = roomName,
            RoomJoin = true,
            CanPublish = true,
            CanSubscribe = true,
            // No data channel: this room carries a voice call between two people, and a data
            // channel is a side channel through the platform's own media plane that nothing in any
            // spec asks for and nothing here would police.
            CanPublishData = false,
            // The call is voice (D-24 "in-app voice"). Sources are named rather than left to the
            // client so a compromised app cannot publish a camera track into somebody's ride.
            CanPublishSources = ["microphone"],
        };

        return new SignallingToken(roomName, Sign(identity, displayName, grant, expiresAt), WsUrl, expiresAt);
    }

    public string MintAdminToken(string roomName, TimeSpan ttl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roomName);

        var grant = new VideoGrant
        {
            Room = roomName,
            // Admin only, and scoped to the one room being closed. `roomList`/`roomCreate` would
            // make this a key to every conversation on the platform.
            RoomAdmin = true,
        };

        return Sign($"{ServiceIdentityPrefix}{roomName}", "voip-svc", grant, _clock.GetUtcNow() + ttl);
    }

    /// <summary>Prefix for the service's own identity, so an admin token can never look like a user.</summary>
    internal const string ServiceIdentityPrefix = "svc:voip:";

    private string Sign(string identity, string displayName, VideoGrant grant, DateTimeOffset expiresAt)
    {
        var now = _clock.GetUtcNow();

        var payload = new AccessTokenPayload
        {
            Issuer = _options.LiveKit.ApiKey!,
            Subject = identity,
            Name = string.IsNullOrWhiteSpace(displayName) ? null : displayName,
            // A minute of leeway: the handset that presents this token has its own clock, and a
            // token that is not yet valid fails exactly like a forged one.
            NotBefore = now.AddMinutes(-1).ToUnixTimeSeconds(),
            IssuedAt = now.ToUnixTimeSeconds(),
            Expiry = expiresAt.ToUnixTimeSeconds(),
            Video = grant,
        };

        var header = Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "HS256", typ = "JWT" }, Json));
        var body = Encode(JsonSerializer.SerializeToUtf8Bytes(payload, Json));
        var signingInput = $"{header}.{body}";

        var signature = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(_options.LiveKit.ApiSecret!), Encoding.UTF8.GetBytes(signingInput));

        return $"{signingInput}.{Encode(signature)}";
    }

    private static string Encode(ReadOnlySpan<byte> bytes)
    {
        var buffer = new char[Base64Url.GetEncodedLength(bytes.Length)];

        Base64Url.EncodeToChars(bytes, buffer);

        return new string(buffer);
    }

    /// <summary>LiveKit's own claim names. Snake/short, so they are spelled out rather than derived.</summary>
    private sealed class AccessTokenPayload
    {
        [JsonPropertyName("iss")] public required string Issuer { get; init; }

        [JsonPropertyName("sub")] public required string Subject { get; init; }

        [JsonPropertyName("name")] public string? Name { get; init; }

        [JsonPropertyName("nbf")] public required long NotBefore { get; init; }

        [JsonPropertyName("iat")] public required long IssuedAt { get; init; }

        [JsonPropertyName("exp")] public required long Expiry { get; init; }

        [JsonPropertyName("video")] public required VideoGrant Video { get; init; }
    }

    /// <summary>LiveKit's `VideoGrant`. Absent members mean "not granted".</summary>
    private sealed class VideoGrant
    {
        [JsonPropertyName("room")] public string? Room { get; init; }

        [JsonPropertyName("roomJoin")] public bool? RoomJoin { get; init; }

        [JsonPropertyName("roomAdmin")] public bool? RoomAdmin { get; init; }

        [JsonPropertyName("canPublish")] public bool? CanPublish { get; init; }

        [JsonPropertyName("canSubscribe")] public bool? CanSubscribe { get; init; }

        [JsonPropertyName("canPublishData")] public bool? CanPublishData { get; init; }

        [JsonPropertyName("canPublishSources")] public IReadOnlyList<string>? CanPublishSources { get; init; }
    }
}
