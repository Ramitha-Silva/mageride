#!/usr/bin/env python3
"""infra/replica/gtfs_shape.py — is a returned shape a shape of THIS corridor? (C126)

    python3 gtfs_shape.py --polyline <encoded> --near 6.9366,79.8524 --near 6.8412,79.9647 \
        [--tolerance-m 1200] [--bbox 5.7,10.0,79.4,82.1]

Exit 0 and a one-line JSON verdict on stdout; exit 1 and the same verdict with `ok: false` when a
check fails. Nothing here reads the feed, the database or the network — it is given one polyline
from one `/v1/transit/options` answer and asked whether it is geometrically possible.

WHY THIS EXISTS

"A corridor sample set returns the expected routes with correct shapes" (C126's definition of
done) needs "correct" to mean something a script can decide without a second copy of the feed to
compare against. Three properties do:

  * it decodes at all, to at least two points — a `shape` field that is present but truncated,
    double-encoded, or encoded at precision 6 while the client decodes at 5 (the classic polyline
    bug) fails here rather than drawing a line across the Indian Ocean on a passenger's map;
  * every point is inside the same Sri Lanka bounding box BR-32.1 rejects stops outside of — one
    rule for stops and shapes, so a lat/lng transposition (79.85, 6.93 is in Somalia) cannot pass;
  * the line passes near BOTH ends of the corridor that produced it — which is what makes it this
    corridor's shape rather than some other route's, and is the check that catches a router
    returning a real, valid, wrong polyline.

The distance is haversine to the nearest VERTEX, not to the nearest point of the nearest segment.
Perpendicular distance to a segment would be the truer measure, but a GTFS shape is sampled every
few tens of metres along the road, so the difference is far below the tolerance — and the simpler
measure cannot itself be subtly wrong.
"""

from __future__ import annotations

import argparse
import json
import math
import sys

EARTH_RADIUS_M = 6_371_008.8


def decode_polyline(encoded: str, precision: int = 5) -> list[tuple[float, float]]:
    """Google's encoded-polyline algorithm, the decode half of MageRide.Shared.Geo.EncodedPolyline.

    Precision 1e5 — five decimal places — which is what EncodedPolyline.Precision is. It is a
    parameter rather than a constant so that a suspected precision mismatch can be demonstrated
    (decode at 6, watch every point land off the island) instead of argued about.
    """
    factor = float(10**precision)
    points: list[tuple[float, float]] = []
    index = 0
    lat = 0
    lng = 0
    length = len(encoded)

    while index < length:
        for axis in ("lat", "lng"):
            shift = 0
            result = 0
            while True:
                if index >= length:
                    raise ValueError("the polyline ends inside a coordinate")
                byte = ord(encoded[index]) - 63
                index += 1
                result |= (byte & 0x1F) << shift
                shift += 5
                if byte < 0x20:
                    break
            # The low bit is the sign, and the value is inverted rather than negated.
            delta = ~(result >> 1) if result & 1 else result >> 1
            if axis == "lat":
                lat += delta
            else:
                lng += delta
        points.append((lat / factor, lng / factor))

    return points


def haversine_m(a: tuple[float, float], b: tuple[float, float]) -> float:
    lat1, lng1 = math.radians(a[0]), math.radians(a[1])
    lat2, lng2 = math.radians(b[0]), math.radians(b[1])
    dlat = lat2 - lat1
    dlng = lng2 - lng1
    h = math.sin(dlat / 2) ** 2 + math.cos(lat1) * math.cos(lat2) * math.sin(dlng / 2) ** 2
    return 2 * EARTH_RADIUS_M * math.asin(math.sqrt(h))


def nearest_vertex_m(points: list[tuple[float, float]], target: tuple[float, float]) -> float:
    return min(haversine_m(point, target) for point in points)


def coordinate(text: str) -> tuple[float, float]:
    lat, _, lng = text.partition(",")
    return float(lat), float(lng)


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(add_help=True)
    parser.add_argument("--polyline", required=True)
    parser.add_argument(
        "--near",
        action="append",
        default=[],
        metavar="LAT,LNG",
        help="a point the shape must pass near; pass twice for the two ends of a corridor",
    )
    parser.add_argument("--tolerance-m", type=float, default=1200.0)
    parser.add_argument(
        "--bbox",
        default="5.7,10.0,79.4,82.1",
        metavar="MINLAT,MAXLAT,MINLNG,MAXLNG",
        help="GtfsValidator's Sri Lanka box, so a stop and a shape are judged by one rule",
    )
    parser.add_argument("--precision", type=int, default=5)
    args = parser.parse_args(argv)

    verdict: dict[str, object] = {"ok": False, "points": 0, "failures": []}
    failures: list[str] = verdict["failures"]  # type: ignore[assignment]

    if not args.polyline:
        failures.append("the leg carries no shape at all")
        print(json.dumps(verdict, separators=(",", ":")))
        return 1

    try:
        points = decode_polyline(args.polyline, args.precision)
    except (ValueError, IndexError) as error:
        failures.append(f"the shape does not decode: {error}")
        print(json.dumps(verdict, separators=(",", ":")))
        return 1

    verdict["points"] = len(points)

    if len(points) < 2:
        failures.append(f"the shape decodes to {len(points)} point(s); a line needs two")

    min_lat, max_lat, min_lng, max_lng = (float(v) for v in args.bbox.split(","))
    outside = [
        (round(lat, 5), round(lng, 5))
        for lat, lng in points
        if not (min_lat <= lat <= max_lat and min_lng <= lng <= max_lng)
    ]
    if outside:
        failures.append(
            f"{len(outside)} of {len(points)} shape points are outside Sri Lanka "
            f"({min_lat}–{max_lat} °N, {min_lng}–{max_lng} °E), first {outside[0]}"
        )

    distances: list[dict[str, object]] = []
    if points:
        for text in args.near:
            target = coordinate(text)
            metres = nearest_vertex_m(points, target)
            distances.append({"point": text, "nearestVertexM": round(metres)})
            if metres > args.tolerance_m:
                failures.append(
                    f"the shape's nearest point to {text} is {round(metres)} m away, "
                    f"over the {round(args.tolerance_m)} m tolerance"
                )

    verdict["distances"] = distances
    verdict["ok"] = not failures

    print(json.dumps(verdict, separators=(",", ":")))
    return 0 if verdict["ok"] else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
