using System.Globalization;

namespace MageRide.Shared.Primitives;

/// <summary>
/// A currency amount in <b>integer minor units</b> — the only representation the platform stores
/// or transmits (CLAUDE.md; D3' §0 "Money: integer minor units (Rs × 100; <c>long amountMinor</c>,
/// <c>currency:"LKR"</c>)").
/// </summary>
/// <remarks>
/// <para>
/// There is no <see cref="decimal"/> or <see cref="double"/> anywhere in a money path. Fares,
/// wallet balances, daily fees and ledger entries are <c>long</c> cents end to end, so a sum of
/// entries is exact and the ledger balances.
/// </para>
/// <para>
/// Payloads still carry <c>amountMinor</c> and <c>currency</c> as two flat fields per D3'; this
/// type is the in-process representation, not a wire shape. Services project it into their DTOs.
/// </para>
/// </remarks>
public readonly record struct Money : IComparable<Money>
{
    /// <summary>ISO-4217 code for the Sri Lankan rupee — the platform's only currency today.</summary>
    public const string Lkr = "LKR";

    /// <summary>Minor units per major unit: 100 cents to the rupee.</summary>
    public const int MinorUnitsPerMajor = 100;

    public Money(long amountMinor, string currency)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);

        if (currency.Length != 3)
        {
            throw new ArgumentException($"'{currency}' is not an ISO-4217 alphabetic code.", nameof(currency));
        }

        AmountMinor = amountMinor;
        Currency = currency.ToUpperInvariant();
    }

    /// <summary>The amount in minor units (cents). Negative values are legal — refunds and debits.</summary>
    public long AmountMinor { get; init; }

    /// <summary>ISO-4217 alphabetic code, upper case.</summary>
    public string Currency { get; init; }

    public static Money Zero { get; } = new(0, Lkr);

    /// <summary>An amount already expressed in cents.</summary>
    public static Money FromMinor(long amountMinor, string currency = Lkr) => new(amountMinor, currency);

    /// <summary>Rupees to cents, rounded half-away-from-zero. For config and test fixtures only.</summary>
    public static Money FromMajor(decimal amountMajor, string currency = Lkr) =>
        new((long)decimal.Round(amountMajor * MinorUnitsPerMajor, 0, MidpointRounding.AwayFromZero), currency);

    public bool IsZero => AmountMinor == 0;

    public bool IsNegative => AmountMinor < 0;

    /// <summary>The amount in major units. Presentation only — never round-trip through this.</summary>
    public decimal ToMajor() => (decimal)AmountMinor / MinorUnitsPerMajor;

    public static Money operator +(Money left, Money right) =>
        new(checked(left.AmountMinor + SameCurrency(left, right).AmountMinor), left.Currency);

    public static Money operator -(Money left, Money right) =>
        new(checked(left.AmountMinor - SameCurrency(left, right).AmountMinor), left.Currency);

    public static Money operator -(Money value) => new(checked(-value.AmountMinor), value.Currency);

    public static Money operator *(Money value, long factor) => new(checked(value.AmountMinor * factor), value.Currency);

    public static Money operator *(long factor, Money value) => value * factor;

    public static Money Add(Money left, Money right) => left + right;

    public static Money Subtract(Money left, Money right) => left - right;

    public static Money Multiply(Money value, long factor) => value * factor;

    public static Money Negate(Money value) => -value;

    public int CompareTo(Money other) => AmountMinor.CompareTo(SameCurrency(this, other).AmountMinor);

    public static bool operator <(Money left, Money right) => left.CompareTo(right) < 0;

    public static bool operator >(Money left, Money right) => left.CompareTo(right) > 0;

    public static bool operator <=(Money left, Money right) => left.CompareTo(right) <= 0;

    public static bool operator >=(Money left, Money right) => left.CompareTo(right) >= 0;

    /// <summary>e.g. <c>Rs 480.00</c>. Diagnostics only — user-facing copy is localised (Si/Ta/En).</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Currency} {ToMajor():0.00}");

    private static Money SameCurrency(Money left, Money right) =>
        string.Equals(left.Currency, right.Currency, StringComparison.Ordinal)
            ? right
            : throw new InvalidOperationException(
                $"Cannot combine {left.Currency} with {right.Currency}; convert explicitly first.");
}
