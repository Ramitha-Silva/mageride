using MageRide.Fleet.Domain;

namespace MageRide.Fleet.Tests.Unit;

/// <summary>
/// D-37's uniqueness is a unique index over the <em>stored text</em>, so it holds only while every
/// writer stores the same text for the same plate.
/// </summary>
/// <remarks>
/// <b>This is a pin, not a unit test of a string function.</b> There are two writers of
/// <c>registry.vehicles.registration_number</c> — a driver registering their own Mode C vehicle in
/// the Driver App (registry-svc's <c>RegistrationNumbers</c>) and an operator onboarding a Mode A/B
/// vehicle here — and if the two normalise differently, <c>WP QA-1234</c> from the Fleet Portal and
/// <c>WP-QA-1234</c> from the Driver App become two rows for one plate. The table below is the
/// canonical form; a divergence fails a build rather than a plate lookup. <b>The duplication itself
/// is raised in the C059 handoff: this belongs in <c>MageRide.Shared.Primitives</c>.</b>
/// </remarks>
public sealed class FleetRegistrationNumberTests
{
    [Theory]
    [InlineData("WP-QA-1234", "WP-QA-1234")]
    [InlineData("wp qa-1234", "WP-QA-1234")]
    [InlineData("  WP  QA   1234  ", "WP-QA-1234")]
    [InlineData("wp_qa_1234", "WP-QA-1234")]
    [InlineData("WP - QA - 1234", "WP-QA-1234")]
    [InlineData("QA-1234", "QA-1234")]
    [InlineData("-WP-QA-1234-", "WP-QA-1234")]
    public void A_plate_canonicalises_the_way_registry_svc_canonicalises_it(string typed, string stored)
    {
        Assert.True(FleetRegistrationNumbers.TryNormalise(typed, out var normalised));
        Assert.Equal(stored, normalised);
    }

    /// <summary>
    /// A character a plate cannot contain is refused rather than stripped: deleting it would let
    /// two different plates canonicalise to one value.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("---")]
    [InlineData("WP/QA/1234")]
    [InlineData("WP-QA-1234​")]
    [InlineData("WPQA1234WPQA1234WPQA1234WPQA1234WPQA1234")]
    public void A_plate_that_is_empty_too_long_or_carries_an_impossible_character_is_refused(string? typed)
    {
        Assert.False(FleetRegistrationNumbers.TryNormalise(typed, out var normalised));
        Assert.Null(normalised);
    }
}
