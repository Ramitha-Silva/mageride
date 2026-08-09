import { describe, expect, it } from 'vitest';

import { decodePolyline } from '@/lib/polyline';

/**
 * The decoder for `ProxyRideSnapshot.route.polyline`, held against the algorithm's
 * own published example and against the shape ride-svc actually sends.
 *
 * The platform's encoder is `MageRide.Shared.Geo.EncodedPolyline` (precision 5,
 * deltas quantised before differencing). This is the other half of the same wire
 * format, so getting it wrong would draw a route in the wrong hemisphere rather
 * than failing.
 */

/** The canonical example from the published algorithm. */
const CANONICAL = '_p~iF~ps|U_ulLnnqC_mqNvxq`@';

describe('decoding an encoded polyline', () => {
  it('decodes the algorithm’s own published example', () => {
    const path = decodePolyline(CANONICAL);

    // `[lng, lat]`, MapLibre's order — the swap is the mistake this asserts against.
    expect(path).toHaveLength(3);
    expect(path[0]![1]).toBeCloseTo(38.5, 5);
    expect(path[0]![0]).toBeCloseTo(-120.2, 5);
    expect(path[1]![1]).toBeCloseTo(40.7, 5);
    expect(path[1]![0]).toBeCloseTo(-120.95, 5);
    expect(path[2]![1]).toBeCloseTo(43.252, 5);
    expect(path[2]![0]).toBeCloseTo(-126.453, 5);
  });

  it('holds five decimal places, which is about a metre', () => {
    // Precision 5 is what `EncodedPolyline.Precision` is; a decoder that used 6
    // would put a Colombo pickup in the Indian Ocean.
    const [point] = decodePolyline('_p~iF~ps|U');
    expect(point![1]).toBeCloseTo(38.5, 6);
  });

  it('answers an empty path for nothing at all', () => {
    // public-bff omits `route` on a ride it has no geometry for, and omits the
    // whole `polyline` when the two ends coincide (`Encode` returns null for fewer
    // than two distinct points).
    expect(decodePolyline(undefined)).toEqual([]);
    expect(decodePolyline(null)).toEqual([]);
    expect(decodePolyline('')).toEqual([]);
  });

  it('keeps whatever prefix parsed when the string is truncated', () => {
    // A malformed line is decoration on a screen whose point is the driver's
    // marker. Throwing here would take the whole tracking page down over it.
    const truncated = decodePolyline(CANONICAL.slice(0, 12));
    expect(truncated.length).toBeGreaterThanOrEqual(1);
    expect(truncated[0]![1]).toBeCloseTo(38.5, 5);
  });
});
