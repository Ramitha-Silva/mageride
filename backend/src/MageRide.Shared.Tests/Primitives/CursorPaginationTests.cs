using System.Text.Json;
using MageRide.Shared.Http;
using MageRide.Shared.Primitives;

namespace MageRide.Shared.Tests.Primitives;

/// <summary>Cursor pagination per D3' §0: <c>{items, cursor, hasMore}</c>, default 20, max 100.</summary>
public sealed class CursorPaginationTests
{
    private sealed record Trip(Guid Id, DateTimeOffset CompletedAt);

    [Fact]
    public void The_envelope_serialises_to_the_D3_shape()
    {
        var page = new CursorPage<string>(["a", "b"], "opaque", true);
        var json = JsonSerializer.Serialize(page, MageRideJson.Options);

        Assert.Equal("""{"items":["a","b"],"cursor":"opaque","hasMore":true}""", json);
    }

    [Fact]
    public void A_last_page_serialises_cursor_as_null()
    {
        var json = JsonSerializer.Serialize(new CursorPage<string>(["a"], null, false), MageRideJson.Options);

        // cursor is explicitly null rather than omitted: clients branch on its presence.
        Assert.Contains("\"cursor\":null", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Limit_defaults_to_20_and_caps_at_100()
    {
        Assert.Equal(20, PageRequest.Create(null, null).Limit);
        Assert.Equal(20, PageRequest.Create(null, 0).Limit);
        Assert.Equal(20, PageRequest.Create(null, -5).Limit);
        Assert.Equal(50, PageRequest.Create(null, 50).Limit);
        Assert.Equal(100, PageRequest.Create(null, 100).Limit);
        Assert.Equal(100, PageRequest.Create(null, 5000).Limit);
    }

    [Fact]
    public void An_overfetched_slab_yields_a_cursor_only_when_more_remains()
    {
        var rows = Enumerable.Range(0, 21).Select(i => $"row-{i}").ToArray();

        var full = CursorPage<string>.FromOverfetch(rows, 20, r => r);
        Assert.Equal(20, full.Items.Count);
        Assert.True(full.HasMore);
        Assert.Equal("row-19", full.Cursor);

        var last = CursorPage<string>.FromOverfetch(rows[..5], 20, r => r);
        Assert.Equal(5, last.Items.Count);
        Assert.False(last.HasMore);
        Assert.Null(last.Cursor);

        var empty = CursorPage<string>.FromOverfetch([], 20, r => r);
        Assert.Empty(empty.Items);
        Assert.Null(empty.Cursor);
    }

    [Fact]
    public void A_cursor_round_trips_a_typed_position()
    {
        var position = new Trip(Guid.NewGuid(), new DateTimeOffset(2026, 7, 27, 10, 0, 0, TimeSpan.Zero));

        var cursor = CursorCodec.Unsigned.Encode(position);
        Assert.True(CursorCodec.Unsigned.TryDecode<Trip>(cursor, out var decoded));
        Assert.Equal(position, decoded);
    }

    [Fact]
    public void A_cursor_is_url_safe()
    {
        var cursor = CursorCodec.Unsigned.EncodeString(new string('a', 200) + "?&=/+");

        Assert.DoesNotContain('+', cursor);
        Assert.DoesNotContain('/', cursor);
        Assert.DoesNotContain('=', cursor);
    }

    [Fact]
    public void Garbage_decodes_to_false_rather_than_throwing()
    {
        Assert.False(CursorCodec.Unsigned.TryDecode<Trip>(null, out _));
        Assert.False(CursorCodec.Unsigned.TryDecode<Trip>("", out _));
        Assert.False(CursorCodec.Unsigned.TryDecode<Trip>("not-base64-!!!", out _));
        Assert.False(CursorCodec.Unsigned.TryDecodeString("AA", out _));
    }

    [Fact]
    public void A_signed_cursor_rejects_tampering()
    {
        var codec = new CursorCodec("a-32-byte-key-for-hmac-signing!!"u8.ToArray());
        Assert.True(codec.IsSigned);

        var cursor = codec.EncodeString("trip:1000");
        Assert.True(codec.TryDecodeString(cursor, out var value));
        Assert.Equal("trip:1000", value);

        // Flip a character in the payload region; the HMAC no longer matches.
        var tampered = cursor[..8] + (cursor[8] == 'A' ? 'B' : 'A') + cursor[9..];
        Assert.False(codec.TryDecodeString(tampered, out _));
    }

    [Fact]
    public void A_signed_cursor_is_not_accepted_by_an_unsigned_codec_and_vice_versa()
    {
        var signed = new CursorCodec("a-32-byte-key-for-hmac-signing!!"u8.ToArray());

        Assert.False(CursorCodec.Unsigned.TryDecodeString(signed.EncodeString("x"), out _));
        Assert.False(signed.TryDecodeString(CursorCodec.Unsigned.EncodeString("x"), out _));
    }
}
