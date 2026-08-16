// =====================================================================================
// The synthetic fleet, and how it moves (C129).
//
// IDS ARE DERIVED, NOT ALLOCATED
// ------------------------------------------------------------------------------------
// Vehicle N's id is a pure function of N, so a k6 VU, `load/configure.sh` and a psql query run
// afterwards all name the same vehicle without sharing a file. The prefix `10ad10ad` is valid
// hex, parses as a UUID, and makes every artefact this suite leaves behind greppable:
//
//   redis-cli --scan --pattern 'veh:meta:10ad10ad-*'
//   SELECT count(*) FROM telemetry.positions WHERE vehicle_id::text LIKE '10ad10ad-%';
//
// No vehicle here exists in `registry.vehicles`, and it does not need to: `telemetry.positions`
// carries no foreign key to it (migration 1801 says why — an FK costs an index probe per row on
// a COPY path sized for 40k rows/s), position-processor-svc keys everything by the vehicleId
// EMQX authenticated, and fanout-svc has no database at all. The one thing an unregistered
// vehicle does not exercise is persistence-writer-svc's operational downsample, which needs an
// ACTIVE `trips.sessions` row — that is measured separately and is called out in the report.
//
// HOW IT MOVES, AND WHY SO SLOWLY
// ------------------------------------------------------------------------------------
// position-processor-svc judges implied speed over `max(actual gap, MinStepInterval)` and
// MinStepInterval is 1 s, so a burst published at 4 Hz is judged as though every step took a
// whole second. A 10 m step therefore reads as 36 km/h whatever the publish rate — under every
// ceiling in ADD §12.6 including a motorbike's. A refused sample is not just a lost measurement:
// it never becomes the position the NEXT sample is measured against, so one over-long step
// poisons the rest of the track (the same trap `tests/E2E/CLAUDE.md` records for C121).
// =====================================================================================

const METRES_PER_DEGREE_LAT = 111_320;

/** Vehicle N's id. Stable, greppable, and never a registered vehicle. */
export function vehicleId(index) {
  const tail = String(index).padStart(12, '0');
  return `10ad10ad-0000-4000-8000-${tail}`;
}

/**
 * The vehicle types the fleet is drawn from, in the proportions ADD §12.6 prices.
 *
 * The type is on the wire because the anti-spoof ceiling is per type: a fleet published with no
 * `vehicleType` would be judged against `DefaultMaxSpeedKph` (200), which is the most permissive
 * row in the table and would quietly stop testing the filter at all.
 */
const TYPES = ['three_wheeler', 'motorbike', 'sedan', 'mini_van', 'van'];

export function vehicleType(index) {
  return TYPES[index % TYPES.length];
}

/**
 * Where vehicle N starts.
 *
 * Vehicles are laid out in CLUSTERS rather than on a uniform grid, because ADD §16.3's fan-out
 * arithmetic is per H3 cell: "average 5 vehicles per 3 km ring". A uniform spread would put one
 * vehicle in each of hundreds of cells, which is a different — and much cheaper — fan-out shape
 * than the one the model prices. `clusterSize` vehicles are placed within ~250 m of a cluster
 * centre, and the centres are `clusterSpacingKm` apart, which is wider than a res-7 cell
 * (~1.2 km edge) so two clusters cannot share one.
 *
 * @param {number} index
 * @param {object} options {originLat, originLng, clusterSize, clusterSpacingKm, columns}
 */
export function startPosition(index, options) {
  const clusterSize = options.clusterSize || 5;
  const spacing = (options.clusterSpacingKm || 3) * 1000;
  const columns = options.columns || 24;

  const cluster = Math.floor(index / clusterSize);
  const within = index % clusterSize;

  const row = Math.floor(cluster / columns);
  const column = cluster % columns;

  const lat = options.originLat + metresToLat(row * spacing);
  const lng = options.originLng + metresToLng(column * spacing, options.originLat);

  // A ring of `clusterSize` around the centre at 250 m — inside one cell, and far enough apart
  // that the vehicles are distinguishable on a map rather than stacked on one pixel.
  const angle = (2 * Math.PI * within) / clusterSize;

  return {
    lat: lat + metresToLat(250 * Math.sin(angle)),
    lng: lng + metresToLng(250 * Math.cos(angle), lat),
    heading: (index * 37) % 360,
    cluster,
  };
}

function metresToLat(metres) {
  return metres / METRES_PER_DEGREE_LAT;
}

function metresToLng(metres, atLat) {
  return metres / (METRES_PER_DEGREE_LAT * Math.cos((atLat * Math.PI) / 180));
}

/**
 * One publishing vehicle: its identity, where it is, and the next sample it would send.
 *
 * @param {number} index
 * @param {object} options {originLat, originLng, stepMetres, clusterSize, clusterSpacingKm}
 */
export function Vehicle(index, options) {
  const settings = options || {};
  const start = startPosition(index, settings);

  this.index = index;
  this.id = vehicleId(index);
  this.type = vehicleType(index);
  this.cluster = start.cluster;
  this.lat = start.lat;
  this.lng = start.lng;
  this.heading = start.heading;
  this.sent = 0;
  this.lastSeq = 0;

  const step = settings.stepMetres || 10;

  /**
   * Advances the vehicle and returns the sample it publishes.
   *
   * `seq` is the millisecond epoch, which is what the driver app uses and what makes several
   * samples inside one second distinguishable. A tracker cannot do this — tcp-adapter derives
   * `seq` from a frame whose clock has whole-second resolution, so every one of its seqs ends
   * in `000` and a second sample inside the same second is discarded as a replay
   * (`HotPath.PositionProcessor/CLAUDE.md` records that as a genuine gap). This suite publishes
   * on the MOBILE plane, so the gap is not exercised and the report says so.
   */
  this.next = function next() {
    // A slow, continuous curve rather than a straight line: a vehicle that never turns leaves a
    // cluster within a minute and the fan-out shape drifts during the run. At 3 degrees and 10 m
    // a step the track closes after 120 steps — a ~191 m circle, well inside one res-7 cell, so
    // the cell a subscriber joined stays the cell the vehicle publishes into for the whole run.
    this.heading = (this.heading + 3) % 360;

    const radians = (this.heading * Math.PI) / 180;
    this.lat += metresToLat(step * Math.cos(radians));
    this.lng += metresToLng(step * Math.sin(radians), this.lat);
    this.sent++;

    // Strictly increasing, even when two samples are produced inside one millisecond. `seq` is
    // the R-17/T-05 replay watermark and position-processor-svc discards `seq <= watermark`
    // outright — a catch-up tick that emitted two samples with one timestamp would have the
    // second counted `replayed` and silently dropped, which reads as the pipeline losing data.
    const now = Math.max(Date.now(), this.lastSeq + 1);
    this.lastSeq = now;

    return {
      vehicleId: this.id,
      sampleTs: now,
      seq: now,
      lat: this.lat,
      lng: this.lng,
      source: 0, // PositionSource.Mobile — the driver app's own GPS.
      speedMps: step, // What the step implies at the 1 s floor the filter judges against.
      headingDeg: this.heading,
      accuracyM: 8,
      satCount: 11,
      mode: 'C', // Idle Mode C is public on `cell:{h3}`, which is what the fan-out measures.
      vehicleType: this.type,
    };
  };
}

/** `veh/{vehicleId}/pos/live` — the hot path (`MqttTopics.PositionLive`). */
export function positionTopic(id) {
  return `veh/${id}/pos/live`;
}
