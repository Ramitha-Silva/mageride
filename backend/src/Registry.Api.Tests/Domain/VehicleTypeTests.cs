using MageRide.Registry.Domain;

namespace MageRide.Registry.Tests.Domain;

/// <summary>
/// AL-09's canonical enumeration. The database CHECK (0303) is the backstop; this set is what
/// produces the error a client can act on, so the two have to agree exactly —
/// <c>VehicleTypeCheckTests</c> asserts that against a real Postgres.
/// </summary>
public sealed class VehicleTypeTests
{
    [Fact]
    public void The_canonical_set_is_the_ten_al_09_names()
    {
        Assert.Equal(
            ["bus", "flex", "mini_truck", "mini_van", "motorbike", "sedan", "three_wheeler", "train", "truck", "van"],
            VehicleTypes.All.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_driver_app_set_is_the_canonical_set_without_the_two_mode_a_types()
    {
        Assert.Equal(
            ["bus", "train"],
            VehicleTypes.All.Except(VehicleTypes.DriverApp).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("car")]
    [InlineData("Car")]
    [InlineData("CAR")]
    [InlineData("tuk")]
    [InlineData("Three-wheeler")]
    [InlineData("three wheeler")]
    [InlineData("threeWheeler")]
    [InlineData("")]
    [InlineData(null)]
    public void A_non_canonical_type_is_not_accepted_in_any_casing_or_spelling(string? vehicleType)
    {
        // AL-09 maps car → sedan as a one-time data migration, not an input alias. A client still
        // sending "car" has not been updated, and rewriting it silently would hide that until a
        // fare tariff or a map marker disagreed.
        Assert.False(VehicleTypes.IsCanonical(vehicleType));
        Assert.False(VehicleTypes.IsDriverApp(vehicleType));
    }

    [Theory]
    [InlineData("motorbike")]
    [InlineData("three_wheeler")]
    [InlineData("flex")]
    [InlineData("sedan")]
    [InlineData("mini_van")]
    [InlineData("van")]
    [InlineData("truck")]
    [InlineData("mini_truck")]
    public void Every_driver_app_type_is_also_canonical(string vehicleType)
    {
        Assert.True(VehicleTypes.IsDriverApp(vehicleType));
        Assert.True(VehicleTypes.IsCanonical(vehicleType));
    }

    [Theory]
    [InlineData("bus")]
    [InlineData("train")]
    public void Mode_a_types_are_canonical_but_not_onboardable_here(string vehicleType)
    {
        Assert.True(VehicleTypes.IsCanonical(vehicleType));
        Assert.False(VehicleTypes.IsDriverApp(vehicleType));
    }
}
