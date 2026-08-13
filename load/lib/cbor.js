// =====================================================================================
// The PositionSample wire codec, encode-only (C129).
//
// This is the k6 half of `MageRide.Shared.Telemetry.PositionSampleCodec` and of the KMP
// module's `PositionCodec` (C017). The field names are copied from the .NET file's own
// constants, which are the contract (`backend/contracts/realtime/mqtt-topics.md` §2.1).
//
// WHY CBOR AND NOT THE JSON THE DECODER ALSO ACCEPTS
// ------------------------------------------------------------------------------------
// `PositionSampleCodec.Decode` reads JSON when the payload starts with `{`, so a JSON
// publisher would work and would be half the code. It would also be ~2.4x the bytes, and this
// suite exists to measure bandwidth, broker memory and Redpanda retention against ADD §16.1's
// own arithmetic. A load generator that inflated every payload would report a bandwidth
// number that no deployed client produces.
//
// WHAT THE MEASURED SIZE SAYS ABOUT ADD §3.4 A3
// ------------------------------------------------------------------------------------
// A3 assumes "~80–120 bytes on the wire (CBOR/Protobuf); ~250 bytes JSON". The landed shape
// cannot reach that: `vehicleId` is a 36-character UUID *string* and `sampleTs` is a
// 28-character ISO-8601 *string*, so the two required identity fields alone are 64 bytes of
// text before a single key is written. `encodedSize()` is exported so the report states the
// measured figure rather than the assumed one.
// =====================================================================================

const MAJOR_UINT = 0x00;
const MAJOR_TEXT = 0x60;
const MAJOR_MAP = 0xa0;

/** A growable byte sink. k6 has no Buffer, and an array of numbers is fast enough here. */
function Writer() {
  this.bytes = [];
}

Writer.prototype.head = function head(major, value) {
  if (value < 24) {
    this.bytes.push(major | value);
  } else if (value < 0x100) {
    this.bytes.push(major | 24, value);
  } else if (value < 0x10000) {
    this.bytes.push(major | 25, (value >> 8) & 0xff, value & 0xff);
  } else if (value < 0x100000000) {
    this.bytes.push(
      major | 26, (value >>> 24) & 0xff, (value >>> 16) & 0xff, (value >>> 8) & 0xff, value & 0xff);
  } else {
    // 64-bit. Split rather than shifted: JavaScript's bitwise operators are 32-bit and
    // `seq` is a millisecond epoch, which passed 2^32 in 1970.
    const high = Math.floor(value / 0x100000000);
    const low = value >>> 0;
    this.bytes.push(
      major | 27,
      (high >>> 24) & 0xff, (high >>> 16) & 0xff, (high >>> 8) & 0xff, high & 0xff,
      (low >>> 24) & 0xff, (low >>> 16) & 0xff, (low >>> 8) & 0xff, low & 0xff);
  }
};

Writer.prototype.uint = function uint(value) {
  this.head(MAJOR_UINT, value);
};

Writer.prototype.text = function text(value) {
  // ASCII throughout — every value this codec writes is a UUID, an ISO instant or a
  // lower-case enum name. A multi-byte character here would be a bug in the caller.
  this.head(MAJOR_TEXT, value.length);
  for (let i = 0; i < value.length; i++) {
    this.bytes.push(value.charCodeAt(i) & 0x7f);
  }
};

/** IEEE-754 double, big-endian — CBOR major type 7, additional information 27. */
Writer.prototype.double = function double(value) {
  const view = new DataView(new ArrayBuffer(8));
  view.setFloat64(0, value, false);
  this.bytes.push(0xfb);
  for (let i = 0; i < 8; i++) {
    this.bytes.push(view.getUint8(i));
  }
};

/**
 * The instant format `PositionSampleCodec.FormatInstant` writes:
 * `yyyy-MM-ddTHH:mm:ss.fffffffZ` — seven fractional digits, always.
 *
 * JavaScript's `toISOString()` gives three, and `DateTimeOffset.TryParse` reads either. The
 * four zeros are appended anyway so the encoded payload is byte-for-byte the size the
 * platform's own encoder produces, which is the number §16.1's bandwidth line is measured
 * against.
 */
export function instant(epochMillis) {
  return `${new Date(epochMillis).toISOString().slice(0, -1)}0000Z`;
}

/**
 * Encodes one position sample.
 *
 * A definite-length map, which is what `PositionSampleCodec`'s own remark says it writes and
 * is a byte shorter than the indefinite form; the .NET decoder reads both.
 *
 * @param {object} sample
 * @returns {Uint8Array}
 */
export function encodePosition(sample) {
  const writer = new Writer();
  const optional = [];

  if (sample.speedMps !== undefined) optional.push(['speedMps', 'd', sample.speedMps]);
  if (sample.headingDeg !== undefined) optional.push(['headingDeg', 'u', sample.headingDeg]);
  if (sample.accuracyM !== undefined) optional.push(['accuracyM', 'd', sample.accuracyM]);
  if (sample.hdop !== undefined) optional.push(['hdop', 'd', sample.hdop]);
  if (sample.satCount !== undefined) optional.push(['satCount', 'u', sample.satCount]);
  if (sample.mode !== undefined) optional.push(['mode', 't', sample.mode]);
  if (sample.vehicleType !== undefined) optional.push(['vehicleType', 't', sample.vehicleType]);
  if (sample.fleetId !== undefined) optional.push(['fleetId', 't', sample.fleetId]);
  if (sample.tripId !== undefined) optional.push(['tripId', 't', sample.tripId]);

  // The six required members, then whatever the device reports. `receivedTs` is deliberately
  // never written: it is the platform's own receive clock, stamped by position-processor-svc,
  // and a device that supplied one would have the whole ingest latency measured against a
  // number it chose itself.
  writer.head(MAJOR_MAP, 6 + optional.length);

  writer.text('vehicleId');
  writer.text(sample.vehicleId);
  writer.text('sampleTs');
  writer.text(instant(sample.sampleTs));
  writer.text('seq');
  writer.uint(sample.seq);
  writer.text('lat');
  writer.double(sample.lat);
  writer.text('lng');
  writer.double(sample.lng);
  writer.text('source');
  writer.uint(sample.source);

  for (const [key, kind, value] of optional) {
    writer.text(key);
    if (kind === 'd') writer.double(value);
    else if (kind === 'u') writer.uint(value);
    else writer.text(value);
  }

  return new Uint8Array(writer.bytes);
}

/** The encoded size of a representative sample, for the bandwidth line of the report. */
export function encodedSize(sample) {
  return encodePosition(sample).length;
}
