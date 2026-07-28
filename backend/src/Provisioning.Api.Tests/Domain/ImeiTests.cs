using MageRide.Provisioning.Domain;
using MageRide.Shared.Errors;

namespace MageRide.Provisioning.Tests.Domain;

/// <summary><c>provisioning.yaml</c>'s <c>Imei</c> schema: <c>^\d{15}$</c>, and nothing else.</summary>
public sealed class ImeiTests
{
    [Theory]
    [InlineData("359586015829435")]
    [InlineData("000000000000000")]
    public void Fifteen_ascii_digits_are_valid(string imei) => Assert.True(Imeis.IsValid(imei));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("35958601582943")]      // 14
    [InlineData("3595860158294356")]    // 16
    [InlineData("35958601582943a")]
    [InlineData("359586015829 35")]
    [InlineData(" 359586015829435")]
    public void Anything_else_is_not(string? imei) => Assert.False(Imeis.IsValid(imei));

    /// <summary>
    /// The Unicode digit blocks are refused, which <c>char.IsDigit</c> would have admitted.
    /// </summary>
    /// <remarks>
    /// The platform is trilingual (CLAUDE.md) and Sinhala and Tamil both have decimal digit
    /// blocks. An IMEI typed in Sinhala digits would pass a <c>char.IsDigit</c> check here, fail
    /// the contract's ASCII <c>\d</c> at the gateway — or worse, be stored as a second spelling of
    /// an IMEI that is already bound, which is a duplicate the unique index cannot see.
    /// </remarks>
    [Theory]
    [InlineData("෩෫෧෫෨෦෧෫෨෯෪෨෪෩෫")]  // Sinhala Lith digits
    [InlineData("௩௫௭௫௨௦௭௫௨௯௪௨௪௩௫")]  // Tamil digits
    [InlineData("٣٥٩٥٨٦٠١٥٨٢٩٤٣٥")]  // Arabic-Indic digits
    public void Non_ascii_digits_are_refused(string imei) => Assert.False(Imeis.IsValid(imei));

    /// <summary>
    /// A Luhn-invalid IMEI is accepted, deliberately.
    /// </summary>
    /// <remarks>
    /// The fifteenth digit is a Luhn checksum and enforcing it would catch most single-digit typos
    /// in a 5,000-row CSV. It is not enforced because the contract's pattern is the contract, and
    /// because the grey-import GT06 and JT/T 808 units in D6' §4.1 report IMEIs that do not satisfy
    /// it — rejecting one would leave a working tracker unprovisionable with no override.
    /// </remarks>
    [Fact]
    public void A_luhn_invalid_imei_is_still_accepted()
    {
        // 359586015829435 is Luhn-valid; …436 is not.
        Assert.True(Imeis.IsValid("359586015829436"));
    }

    [Fact]
    public void A_body_field_reports_a_400_naming_the_field()
    {
        var thrown = Assert.Throws<MageRideValidationException>(() => Imeis.Require("123"));

        Assert.Equal(MageRideErrors.ValidationFailed, thrown.Error);
        Assert.Contains("imei", thrown.Errors.Keys);
    }

    /// <summary>
    /// On a path segment the answer is 404, not 400: "not a well-formed IMEI" and "no such
    /// tracker" are the same answer to a caller, and telling them apart confirms which of two
    /// IMEIs is real to somebody enumerating them.
    /// </summary>
    [Fact]
    public void A_path_segment_reports_a_404()
    {
        var thrown = Assert.Throws<MageRideException>(() => Imeis.RequirePath("not-an-imei"));

        Assert.Equal(MageRideErrors.NotFound, thrown.Error);
    }
}
