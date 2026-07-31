using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using MageRide.Voip.Configuration;
using MageRide.Voip.Signalling;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace MageRide.Voip.Tests.Unit;

/// <summary>
/// The teardown call, against a stub LiveKit on a real socket.
/// </summary>
/// <remarks>
/// The wire shape is asserted here — the Twirp path, the bearer, the admin grant — because it is
/// the part a running SFU would reject silently: a `DeleteRoom` with the wrong grant answers 401 and
/// the call simply runs on.
/// </remarks>
public sealed class LiveKitRoomServiceTests : IAsyncLifetime
{
    private sealed record RecordedCall(string Path, string? Authorization, string Body);

    private WebApplication _livekit = null!;
    private string _baseUrl = null!;

    private readonly ConcurrentQueue<RecordedCall> _calls = new();
    private HttpStatusCode _status = HttpStatusCode.OK;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();

        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        _livekit = builder.Build();

        _livekit.MapPost("/twirp/livekit.RoomService/{**rest}", async (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body);

            _calls.Enqueue(new RecordedCall(
                context.Request.Path,
                context.Request.Headers.Authorization.ToString(),
                await reader.ReadToEndAsync()));

            context.Response.StatusCode = (int)_status;

            await context.Response.WriteAsync("{}");
        });

        await _livekit.StartAsync();

        _baseUrl = _livekit.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First() + "/";
    }

    public async ValueTask DisposeAsync()
    {
        await _livekit.StopAsync();
        await _livekit.DisposeAsync();
    }

    [Fact]
    public async Task A_teardown_is_a_DeleteRoom_carrying_an_admin_token_for_that_room()
    {
        var (rooms, _) = Build();

        Assert.True(await rooms.CloseRoomAsync("ride_abc", CancellationToken.None));

        var call = Assert.Single(_calls);

        Assert.Equal("/twirp/livekit.RoomService/DeleteRoom", call.Path);
        Assert.Contains("\"room\":\"ride_abc\"", call.Body, StringComparison.Ordinal);
        Assert.StartsWith("Bearer ", call.Authorization, StringComparison.Ordinal);

        var video = Payload(call.Authorization!["Bearer ".Length..]).GetProperty("video");

        Assert.Equal("ride_abc", video.GetProperty("room").GetString());
        Assert.True(video.GetProperty("roomAdmin").GetBoolean());
    }

    [Fact]
    public async Task A_room_LiveKit_has_never_heard_of_is_already_closed()
    {
        // The common case on a redelivery, and on a ride whose call never actually connected. A 404
        // treated as a failure would leave the consumer retrying something that cannot succeed.
        _status = HttpStatusCode.NotFound;

        var (rooms, _) = Build();

        Assert.True(await rooms.CloseRoomAsync("ride_abc", CancellationToken.None));
    }

    [Fact]
    public async Task A_LiveKit_that_refuses_is_reported_rather_than_thrown()
    {
        // The teardown runs on the Kafka consumer; throwing would stall the partition behind one
        // unreachable SFU. The uncommitted offset is the retry.
        _status = HttpStatusCode.InternalServerError;

        var (rooms, _) = Build();

        Assert.False(await rooms.CloseRoomAsync("ride_abc", CancellationToken.None));
    }

    [Fact]
    public async Task A_LiveKit_that_cannot_be_reached_at_all_is_reported_rather_than_thrown()
    {
        var (rooms, _) = Build(baseUrl: "http://127.0.0.1:1/");

        Assert.False(await rooms.CloseRoomAsync("ride_abc", CancellationToken.None));
    }

    [Fact]
    public async Task An_unconfigured_deployment_says_so_rather_than_reporting_success()
    {
        // A stub that "succeeded" would make a room nobody closed indistinguishable from one that
        // was — which is the property D6' §6 names.
        var rooms = new UnconfiguredLiveKitRoomService(NullLogger<UnconfiguredLiveKitRoomService>.Instance);

        Assert.False(rooms.IsConfigured);
        Assert.False(await rooms.CloseRoomAsync("ride_abc", CancellationToken.None));
    }

    private (ILiveKitRoomService Rooms, ILiveKitTokenMinter Tokens) Build(string? baseUrl = null)
    {
        var options = Options.Create(new VoipOptions
        {
            LiveKit = new VoipOptions.LiveKitOptions
            {
                WsUrl = "wss://voip.mageride.test",
                ApiUrl = baseUrl ?? _baseUrl,
                ApiKey = "APIkey123",
                ApiSecret = "secret",
                ApiTimeout = TimeSpan.FromSeconds(3),
            },
        });

        var minter = new LiveKitTokenMinter(options, new FakeTimeProvider(new DateTimeOffset(2026, 7, 31, 9, 0, 0, TimeSpan.Zero)));

        var services = new ServiceCollection();

        services.AddHttpClient(LiveKitRoomService.HttpClientName)
            .ConfigureHttpClient(client =>
            {
                client.BaseAddress = new Uri(options.Value.LiveKit.ApiUrl!, UriKind.Absolute);
                client.Timeout = options.Value.LiveKit.ApiTimeout;
            });

        var provider = services.BuildServiceProvider();

        return (
            new LiveKitRoomService(
                provider.GetRequiredService<IHttpClientFactory>(),
                minter,
                options,
                NullLogger<LiveKitRoomService>.Instance),
            minter);
    }

    private static JsonElement Payload(string token) =>
        JsonDocument.Parse(Base64Url.DecodeFromChars(token.Split('.')[1])).RootElement.Clone();
}
