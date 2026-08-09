/**
 * Google's Encoded Polyline Algorithm, precision 5 — the decoder for
 * `ProxyRideSnapshot.route.polyline`.
 *
 * The platform's encoder is `MageRide.Shared.Geo.EncodedPolyline` and D3' makes
 * `polyline` a `string` for size: as JSON numbers the same path is four to five
 * times the bytes. The format is published, stable and patent-free despite the
 * name — nothing here calls a Google service, which D3's map hard rule forbids.
 *
 * Twenty-five lines rather than a dependency: MapLibre reads a `LineString`
 * directly and this is the only thing standing between the two, so a package would
 * be a supply-chain entry on a public, no-login surface in exchange for an
 * afternoon's algorithm.
 *
 * A malformed string decodes to whatever prefix parsed and the caller draws that.
 * The alternative is throwing inside a map effect and taking the whole tracking
 * screen down over a line that is decoration — the driver's marker is the data
 * this screen exists for, and the route is context around it.
 */

/** `[lng, lat]` pairs, in MapLibre's own order. */
export type LngLat = readonly [number, number];

const PRECISION = 1e5;

export function decodePolyline(encoded: string | null | undefined): LngLat[] {
  if (!encoded) return [];

  const path: LngLat[] = [];
  let index = 0;
  let lat = 0;
  let lng = 0;

  while (index < encoded.length) {
    const latDelta = readValue();
    if (latDelta === null) break;

    const lngDelta = readValue();
    if (lngDelta === null) break;

    lat += latDelta;
    lng += lngDelta;
    path.push([lng / PRECISION, lat / PRECISION]);
  }

  return path;

  /** One varint, or `null` when the string ran out mid-value. */
  function readValue(): number | null {
    let result = 0;
    let shift = 0;
    let byte: number;

    do {
      if (index >= encoded!.length) return null;
      byte = encoded!.charCodeAt(index++) - 63;
      result |= (byte & 0x1f) << shift;
      shift += 5;
    } while (byte >= 0x20);

    // The low bit is the sign, and the value is the zig-zag of the delta.
    return result & 1 ? ~(result >> 1) : result >> 1;
  }
}
