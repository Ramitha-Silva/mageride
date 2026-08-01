using System.Text.Json;
using MageRide.PublicBff.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.PublicBff.Tests.Integration;

/// <summary>
/// "The SSE stream survives a reconnect and resumes from the cursor."
/// </summary>
/// <remarks>
/// <b>Resume is asserted through a genuine second connection, not through a replay buffer.</b> The
/// cursor describes what the client already knows rather than indexing a log — see
/// <c>TrackCursor</c> — so "resumes" means the reconnect is told what changed and is not told again
/// what it already had. A buffer would also be the historical replay D-34 forbids, reached through
/// the back door.
/// </remarks>
[Collection<PublicBffCollection>]
public sealed class LiveStreamTests(PostgresFixture postgres, RedisFixture redis)
{
    [Fact]
    public async Task The_stream_sends_a_position_and_a_status_and_then_resumes_from_the_cursor()
    {
        await using var harness = await StartAsync();

        var ride = await harness.Seed.RideAsync(state: "InProgress", kind: 2);
        var token = await harness.Seed.TokenAsync(
            ride.RideId, "package_recipient", harness.Now.AddHours(4));

        await harness.Seed.PositionAsync(
            ride.VehicleId, PublicBffSeed.DropoffLat, PublicBffSeed.DropoffLng, harness.Now);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var frames = await ReadFramesAsync(harness, $"/public/track/{token}/live", 2, cancellation.Token);

        Assert.Equal("position", frames[0].GetProperty("type").GetString());
        Assert.Equal("status", frames[1].GetProperty("type").GetString());
        Assert.Equal("InTransit", frames[1].GetProperty("status").GetString());

        var cursor = frames[1].GetProperty("cursor").GetString();
        Assert.False(string.IsNullOrWhiteSpace(cursor));

        // The reconnect. Nothing has changed, so a client that resumes correctly is told nothing —
        // which is the property that makes reconnecting cheap and makes a flapping connection
        // harmless.
        var resumed = await PublicBffHarness.OkAsync(
            await harness.GetAsync($"/public/track/{token}/live?since={Uri.EscapeDataString(cursor!)}"),
            "the resumed poll");

        Assert.Empty(resumed.GetProperty("events").EnumerateArray());
        Assert.Equal(cursor, resumed.GetProperty("cursor").GetString());

        // Now the vehicle moves and the parcel is delivered. The resumed client learns both, and
        // learns nothing about the fix it already drew.
        await harness.Seed.PositionAsync(
            ride.VehicleId,
            PublicBffSeed.DropoffLat + 0.002,
            PublicBffSeed.DropoffLng,
            harness.Now.AddSeconds(30));

        await MarkStateAsync(harness, ride.RideId, "PaymentPending");

        var afterwards = await PublicBffHarness.OkAsync(
            await harness.GetAsync($"/public/track/{token}/live?since={Uri.EscapeDataString(cursor!)}"),
            "the poll after the delivery");

        var types = afterwards.GetProperty("events").EnumerateArray()
            .Select(static frame => frame.GetProperty("type").GetString() ?? string.Empty)
            .ToArray();

        Assert.Equal(["position", "status", "resolved"], types);
    }

    [Fact]
    public async Task A_poll_with_no_prior_knowledge_is_told_everything_that_is_true_now()
    {
        await using var harness = await StartAsync();

        var ride = await harness.Seed.RideAsync(state: "Accepted", kind: 1);
        var token = await harness.Seed.TokenAsync(
            ride.RideId, "proxy_rider", harness.Now.AddHours(2));

        await harness.Seed.PositionAsync(
            ride.VehicleId, PublicBffSeed.PickupLat, PublicBffSeed.PickupLng, harness.Now);

        var batch = await PublicBffHarness.OkAsync(
            await harness.GetAsync($"/public/track/{token}/live?since="),
            "the first poll");

        var types = batch.GetProperty("events").EnumerateArray()
            .Select(static frame => frame.GetProperty("type").GetString() ?? string.Empty)
            .ToArray();

        Assert.Equal(["position", "status"], types);
        Assert.Equal("Accepted", batch.GetProperty("events")[1].GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_malformed_cursor_is_answered_rather_than_refused()
    {
        await using var harness = await StartAsync();

        var ride = await harness.Seed.RideAsync(state: "InProgress", kind: 1);
        var token = await harness.Seed.TokenAsync(
            ride.RideId, "proxy_rider", harness.Now.AddHours(2));

        // A proxy that mangled a query string is not something the page can act on, and the worst
        // case of accepting it is one redundant frame.
        var batch = await PublicBffHarness.OkAsync(
            await harness.GetAsync($"/public/track/{token}/live?since=%F0%9F%92%A5"),
            "a poll with a mangled cursor");

        Assert.NotEmpty(batch.GetProperty("events").EnumerateArray());
    }

    [Fact]
    public async Task A_pickup_confirm_feed_never_carries_a_position()
    {
        await using var harness = await StartAsync();

        var (token, _, _, _) = await harness.Seed.PickupRequestAsync(
            issuedAt: harness.Now.AddSeconds(-30));

        var batch = await PublicBffHarness.OkAsync(
            await harness.GetAsync($"/public/track/{token}/live?since="),
            "the pickup-confirm feed");

        // SCR-WT-003 is the screen on which nobody's location has been shared yet. A feed that
        // carried a coordinate here would be carrying the one this token exists to ask for (P-02).
        foreach (var frame in batch.GetProperty("events").EnumerateArray())
        {
            Assert.NotEqual("position", frame.GetProperty("type").GetString());
            Assert.False(frame.TryGetProperty("position", out _));
        }
    }

    [Fact]
    public async Task An_expired_pickup_request_closes_its_feed()
    {
        await using var harness = await StartAsync();

        var (token, _, _, _) = await harness.Seed.PickupRequestAsync(
            issuedAt: harness.Now.AddSeconds(-400),
            tokenExpiresAt: harness.Now.AddHours(1));

        var batch = await PublicBffHarness.OkAsync(
            await harness.GetAsync($"/public/track/{token}/live?since="),
            "the expired pickup feed");

        var types = batch.GetProperty("events").EnumerateArray()
            .Select(static frame => frame.GetProperty("type").GetString() ?? string.Empty)
            .ToArray();

        Assert.Contains("resolved", types);
        Assert.Equal("Expired", batch.GetProperty("events")[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_token_revoked_mid_stream_closes_the_connection()
    {
        await using var harness = await StartAsync(new Dictionary<string, string?>
        {
            ["PublicBff:StreamPollInterval"] = "00:00:00.100",
        });

        var ride = await harness.Seed.RideAsync(state: "InProgress", kind: 1);
        var token = await harness.Seed.TokenAsync(
            ride.RideId, "proxy_rider", harness.Now.AddHours(2));

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        using var response = await harness.StreamAsync($"/public/track/{token}/live", cancellation.Token);
        Assert.Equal(200, (int)response.StatusCode);

        await using var body = await response.Content.ReadAsStreamAsync(cancellation.Token);
        using var reader = new StreamReader(body);

        // Drain the opening status frame so the revocation is what ends the stream.
        await ReadOneFrameAsync(reader, cancellation.Token);

        await using (var connection = await harness.OpenAsync())
        {
            await Dapper.SqlMapper.ExecuteAsync(
                connection,
                "UPDATE safety.trip_share_tokens SET revoked_at = now() WHERE token = @Token;",
                new { Token = token });
        }

        // A no-login page has no session to expire, so the stream re-reading the token is the only
        // thing that can carry a revocation to somebody who left the tab open.
        harness.Clock.Advance(TimeSpan.FromSeconds(1));

        var closing = await ReadOneFrameAsync(reader, cancellation.Token);

        Assert.NotNull(closing);
        Assert.Equal("resolved", closing!.Value.GetProperty("type").GetString());
        Assert.Equal("token-closed", closing.Value.GetProperty("status").GetString());
    }

    private Task<PublicBffHarness> StartAsync(IDictionary<string, string?>? settings = null) =>
        PublicBffHarness.StartAsync(
            postgres,
            redis,
            settings ?? new Dictionary<string, string?> { ["PublicBff:StreamPollInterval"] = "00:00:00.100" });

    /// <summary>Reads <paramref name="count"/> <c>data:</c> frames off a live SSE connection.</summary>
    private static async Task<IReadOnlyList<JsonElement>> ReadFramesAsync(
        PublicBffHarness harness, string path, int count, CancellationToken cancellationToken)
    {
        using var response = await harness.StreamAsync(path, cancellationToken);

        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(body);

        var frames = new List<JsonElement>(count);

        while (frames.Count < count)
        {
            var frame = await ReadOneFrameAsync(reader, cancellationToken);

            if (frame is null)
            {
                break;
            }

            frames.Add(frame.Value);
        }

        Assert.Equal(count, frames.Count);

        return frames;
    }

    private static async Task<JsonElement?> ReadOneFrameAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                return JsonDocument.Parse(line[6..]).RootElement.Clone();
            }
        }

        return null;
    }

    private static async Task MarkStateAsync(PublicBffHarness harness, Guid rideId, string state)
    {
        await using var connection = await harness.OpenAsync();

        await Dapper.SqlMapper.ExecuteAsync(
            connection,
            "UPDATE rides.rides SET state = @State, terminal_at = now() WHERE id = @RideId;",
            new { RideId = rideId, State = state });
    }
}
