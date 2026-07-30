using MageRide.Shared.Primitives;

namespace MageRide.Fanout.Tests.Infrastructure;

/// <summary>Positions the suite works from.</summary>
/// <remarks>
/// Far enough apart to fall in different H3 res-7 cells, which several assertions depend on: a res-7
/// hexagon is about 1.2 km on a side, so "a different cell" means kilometres and not metres.
/// </remarks>
internal static class Samples
{
    /// <summary>Colombo Fort.</summary>
    public static readonly GeoPoint ColomboFort = new(6.9344, 79.8428);

    /// <summary>Dehiwala, ~9.6 km south — a different res-7 cell.</summary>
    public static readonly GeoPoint Dehiwala = new(6.8514, 79.8653);

    /// <summary>Moratuwa, ~18.5 km south.</summary>
    public static readonly GeoPoint Moratuwa = new(6.7730, 79.8816);

    /// <summary>Kandy, ~95 km inland. Nothing in Colombo's 19-cell view reaches it.</summary>
    public static readonly GeoPoint Kandy = new(7.2906, 80.6337);
}
