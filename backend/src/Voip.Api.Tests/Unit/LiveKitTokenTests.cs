using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MageRide.Voip.Configuration;
using MageRide.Voip.Signalling;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace MageRide.Voip.Tests.Unit;

/// <summary>
/// The LiveKit access token, decoded and verified the way LiveKit itself would.
/// </summary>
/// <remarks>
/// This is worth asserting in detail because a token that is subtly wrong does not fail here — it
/// fails at the SFU, minutes later, with a message about permissions. The nested <c>video</c> grant
/// in particular is the reason the minter is hand-rolled.
/// </remarks>
public sealed class LiveKitTokenTests
{
    private const string ApiKey = "APIkey123";
    private const string ApiSecret = "secret-that-never-leaves-the-minter";

    private static readonly DateTimeOffset Now = new(2026, 7, 31, 9, 0, 0, TimeSpan.Zero);

    private static LiveKitTokenMinter Build(FakeTimeProvider? clock = null) => new(
        Options.Create(new VoipOptions
        {
            LiveKit = new VoipOptions.LiveKitOptions
            {
                WsUrl = "wss://voip.mageride.test",
                ApiUrl = "https://voip.mageride.test/",
                ApiKey = ApiKey,
                ApiSecret = ApiSecret,
            },
        }),
        clock ?? new FakeTimeProvider(Now));

    [Fact]
    public void The_signature_verifies_against_the_api_secret()
    {
        var token = Build().Mint("ride_x", "rider-1", "rider", TimeSpan.FromMinutes(5)).Token;

        var parts = token.Split('.');

        Assert.Equal(3, parts.Length);

        var expected = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(ApiSecret), Encoding.UTF8.GetBytes($"{parts[0]}.{parts[1]}"));

        Assert.Equal(Base64Url.EncodeToString(expected), parts[2]);
    }

    [Fact]
    public void The_header_is_HS256()
    {
        var header = Decode(Build().Mint("ride_x", "rider-1", "rider", TimeSpan.FromMinutes(5)).Token, 0);

        Assert.Equal("HS256", header.GetProperty("alg").GetString());
        Assert.Equal("JWT", header.GetProperty("typ").GetString());
    }

    [Fact]
    public void The_video_grant_is_a_nested_object_not_a_string()
    {
        // The whole reason this minter is hand-rolled: a `video` claim that arrives as a JSON
        // *string* is accepted by no LiveKit server, and the claims-dictionary APIs stringify it
        // depending on the path taken through them.
        var payload = Decode(Build().Mint("ride_abc", "rider-1", "rider", TimeSpan.FromMinutes(5)).Token, 1);

        var video = payload.GetProperty("video");

        Assert.Equal(JsonValueKind.Object, video.ValueKind);
        Assert.Equal("ride_abc", video.GetProperty("room").GetString());
        Assert.True(video.GetProperty("roomJoin").GetBoolean());
        Assert.True(video.GetProperty("canPublish").GetBoolean());
        Assert.True(video.GetProperty("canSubscribe").GetBoolean());
    }

    [Fact]
    public void A_join_token_can_publish_audio_only_and_no_data()
    {
        // The room carries a voice call between two people. A camera track or a data channel would
        // be a side channel through the platform's own media plane that nothing polices.
        var video = Decode(Build().Mint("ride_x", "rider-1", "rider", TimeSpan.FromMinutes(5)).Token, 1)
            .GetProperty("video");

        Assert.False(video.GetProperty("canPublishData").GetBoolean());
        Assert.Equal(
            ["microphone"],
            video.GetProperty("canPublishSources").EnumerateArray().Select(source => source.GetString()));
    }

    [Fact]
    public void The_identity_is_the_subject_and_the_expiry_is_the_ttl()
    {
        var clock = new FakeTimeProvider(Now);
        var minted = Build(clock).Mint("ride_x", "rider-42", "rider", TimeSpan.FromMinutes(5));

        var payload = Decode(minted.Token, 1);

        Assert.Equal("rider-42", payload.GetProperty("sub").GetString());
        Assert.Equal(ApiKey, payload.GetProperty("iss").GetString());
        Assert.Equal(Now.AddMinutes(5).ToUnixTimeSeconds(), payload.GetProperty("exp").GetInt64());
        Assert.Equal(Now.AddMinutes(5), minted.ExpiresAt);

        // A minute of leeway, because the handset presenting this has its own clock and a
        // not-yet-valid token fails exactly like a forged one.
        Assert.Equal(Now.AddMinutes(-1).ToUnixTimeSeconds(), payload.GetProperty("nbf").GetInt64());
    }

    [Fact]
    public void An_admin_token_is_scoped_to_one_room_and_cannot_join_it()
    {
        // `roomList`/`roomCreate` would make the teardown credential a key to every conversation on
        // the platform; `roomJoin` would make it a way into one.
        var video = Decode(Build().MintAdminToken("ride_x", TimeSpan.FromMinutes(1)), 1);

        Assert.Equal("ride_x", video.GetProperty("video").GetProperty("room").GetString());
        Assert.True(video.GetProperty("video").GetProperty("roomAdmin").GetBoolean());
        Assert.False(video.GetProperty("video").TryGetProperty("roomJoin", out _));

        // And it can never be mistaken for a user: no MageRide user id looks like this.
        Assert.StartsWith(LiveKitTokenMinter.ServiceIdentityPrefix, video.GetProperty("sub").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_unconfigured_LiveKit_reports_itself_rather_than_minting_something_useless()
    {
        var minter = new LiveKitTokenMinter(
            Options.Create(new VoipOptions()), new FakeTimeProvider(Now));

        Assert.False(minter.IsConfigured);
    }

    private static JsonElement Decode(string token, int part)
    {
        var segment = token.Split('.')[part];

        return JsonDocument.Parse(Base64Url.DecodeFromChars(segment)).RootElement.Clone();
    }
}
