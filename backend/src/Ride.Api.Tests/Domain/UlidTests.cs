using MageRide.Ride.Domain;

namespace MageRide.Ride.Tests.Domain;

/// <summary>
/// <c>clientRequestId</c> is typed <c>Ulid</c> by the contract and stored in a Postgres
/// <c>UUID</c>; ADD §11.13 has the mobile apps generate real ULIDs. If this reader is wrong, every
/// booking from a correct client is a 400.
/// </summary>
public sealed class UlidTests
{
    [Fact]
    public void A_canonical_uuid_is_accepted_unchanged()
    {
        var id = Guid.NewGuid();

        Assert.True(Ulids.TryParse(id.ToString(), out var parsed));
        Assert.Equal(id, parsed);
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("{2f1c4b8e-6d2a-4f27-9c31-6c1b8e5b2d44}")]
    [InlineData("2f1c4b8e6d2a4f279c316c1b8e5b2d44")]
    public void Every_uuid_format_the_platform_prints_round_trips(string value) =>
        Assert.True(Ulids.TryParse(value, out _));

    /// <summary>The two ends of the ULID range, which pin the bit order.</summary>
    [Theory]
    [InlineData("00000000000000000000000000", "00000000-0000-0000-0000-000000000000")]
    [InlineData("7ZZZZZZZZZZZZZZZZZZZZZZZZZ", "ffffffff-ffff-ffff-ffff-ffffffffffff")]
    public void A_ulid_decodes_to_its_128_bits(string ulid, string expected)
    {
        Assert.True(Ulids.TryParse(ulid, out var parsed));
        Assert.Equal(Guid.Parse(expected), parsed);
    }

    /// <summary>Crockford base32 is case-insensitive; a client that lower-cases must still book.</summary>
    [Fact]
    public void Case_does_not_change_the_value()
    {
        Assert.True(Ulids.TryParse("01ARZ3NDEKTSV4RRFFQ69G5FAV", out var upper));
        Assert.True(Ulids.TryParse("01arz3ndektsv4rrffq69g5fav", out var lower));

        Assert.Equal(upper, lower);
        Assert.NotEqual(Guid.Empty, upper);
    }

    [Fact]
    public void Different_ulids_decode_to_different_values()
    {
        Assert.True(Ulids.TryParse("01ARZ3NDEKTSV4RRFFQ69G5FAV", out var first));
        Assert.True(Ulids.TryParse("01ARZ3NDEKTSV4RRFFQ69G5FAW", out var second));

        Assert.NotEqual(first, second);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    // 25 and 27 characters.
    [InlineData("01ARZ3NDEKTSV4RRFFQ69G5FA")]
    [InlineData("01ARZ3NDEKTSV4RRFFQ69G5FAVX")]
    // I, L, O and U are excluded from the alphabet precisely because they are misread.
    [InlineData("01ARZ3NDEKTSV4RRFFQ69G5FAI")]
    [InlineData("01ARZ3NDEKTSV4RRFFQ69G5FAU")]
    // 8 in the leading position overflows 128 bits.
    [InlineData("8ZZZZZZZZZZZZZZZZZZZZZZZZZ")]
    [InlineData("ZZZZZZZZZZZZZZZZZZZZZZZZZZ")]
    [InlineData("not-an-identifier")]
    public void Anything_else_is_refused(string? value) =>
        Assert.False(Ulids.TryParse(value, out _));
}
