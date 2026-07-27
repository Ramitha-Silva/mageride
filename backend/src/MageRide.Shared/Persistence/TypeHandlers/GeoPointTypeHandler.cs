using System.Data;
using Dapper;
using MageRide.Shared.Primitives;
using NetTopologySuite.Geometries;
using Npgsql;
using NpgsqlTypes;

namespace MageRide.Shared.Persistence.TypeHandlers;

/// <summary>
/// Maps <see cref="GeoPoint"/> to and from a PostGIS <c>geography(Point,4326)</c> column
/// (ADD §9.1: <c>pickup_geo</c>, <c>resolved_geo</c>, <c>captured_geo</c>, …).
/// </summary>
public sealed class GeoPointTypeHandler : SqlMapper.TypeHandler<GeoPoint>
{
    private static readonly GeometryFactory Factory =
        NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(GeoPoint.Wgs84Srid);

    public override GeoPoint Parse(object value) => value switch
    {
        Point point => new GeoPoint(point.Y, point.X),
        Geometry geometry => throw new DataException(
            $"Expected a PostGIS Point but the column returned {geometry.GeometryType}."),
        _ => throw new DataException($"Cannot convert {value?.GetType().Name ?? "null"} to {nameof(GeoPoint)}."),
    };

    public override void SetValue(IDbDataParameter parameter, GeoPoint value)
    {
        // PostGIS is (x=longitude, y=latitude); the record is (latitude, longitude).
        var point = Factory.CreatePoint(new Coordinate(value.Longitude, value.Latitude));
        point.SRID = GeoPoint.Wgs84Srid;

        parameter.Value = point;

        if (parameter is NpgsqlParameter npgsqlParameter)
        {
            npgsqlParameter.NpgsqlDbType = NpgsqlDbType.Geography;
        }
    }
}

/// <summary>Nullable companion — Dapper resolves handlers by exact type.</summary>
public sealed class NullableGeoPointTypeHandler : SqlMapper.TypeHandler<GeoPoint?>
{
    private static readonly GeoPointTypeHandler Inner = new();

    public override GeoPoint? Parse(object value) => value is null or DBNull ? null : Inner.Parse(value);

    public override void SetValue(IDbDataParameter parameter, GeoPoint? value)
    {
        if (value is null)
        {
            parameter.Value = DBNull.Value;

            if (parameter is NpgsqlParameter npgsqlParameter)
            {
                npgsqlParameter.NpgsqlDbType = NpgsqlDbType.Geography;
            }

            return;
        }

        Inner.SetValue(parameter, value.Value);
    }
}
