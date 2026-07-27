-- =====================================================================================
-- 0502 — trips: session business events and 1–5 star ratings
-- Source: server_db_schema.md §4 · D4' §4 · ADD §9.1 · US-8.6, US-18.1, US-18.2
-- =====================================================================================

-- Business events on a tracking session (trip-state-svc). Distinct from the transactional
-- outbox: this is a domain-visible log, not a delivery queue.
CREATE TABLE IF NOT EXISTS trips.events (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  session_id UUID NOT NULL REFERENCES trips.sessions(id) ON DELETE CASCADE,
  kind TEXT NOT NULL,
  payload JSONB,
  ts TIMESTAMPTZ NOT NULL DEFAULT now());

CREATE INDEX IF NOT EXISTS ix_trip_events_session ON trips.events(session_id, ts DESC);

-- Ratings span both planes: subject_kind='session' points at trips.sessions, 'ride' at
-- rides.rides. The reference is therefore polymorphic and carries no FK — the same reason
-- both specs print it without one.
CREATE TABLE IF NOT EXISTS trips.ratings (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  subject_kind TEXT NOT NULL
    CONSTRAINT ck_ratings_subject_kind CHECK (subject_kind IN ('session','ride')),
  subject_id UUID NOT NULL,
  rater_id UUID NOT NULL REFERENCES iam.users(id),
  ratee_id UUID NOT NULL REFERENCES iam.users(id),
  stars SMALLINT NOT NULL CONSTRAINT ck_ratings_stars CHECK (stars BETWEEN 1 AND 5),
  comment TEXT,
  direction TEXT NOT NULL CONSTRAINT ck_ratings_direction
    CHECK (direction IN ('passenger_to_driver','driver_to_passenger')),
  created_at TIMESTAMPTZ NOT NULL DEFAULT now());

CREATE INDEX IF NOT EXISTS ix_ratings_ratee ON trips.ratings(ratee_id);
CREATE INDEX IF NOT EXISTS ix_ratings_subject ON trips.ratings(subject_kind, subject_id);

COMMENT ON TABLE trips.ratings IS
  '1–5 stars + optional comment (US-8.6/18.1/18.2). Feeds dispatch.driver_levels.rating_points (D5 §4.1) and reputation.counters.';
COMMENT ON COLUMN trips.ratings.subject_id IS
  'trips.sessions(id) when subject_kind=''session'', rides.rides(id) when ''ride'' — polymorphic, so no FK.';
