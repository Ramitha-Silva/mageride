package lk.mageride.shared.db

// The on-device schema as it stood at version 1 — `mobile_db_schema.md` §1-§3 plus the
// Δ 2026-06-28 change set (§6), before the Δ 2026-07-05 #2 set (§8) that migration 1 applies.
//
// Only the SIX tables migration 1 touches are written out here. A v1 database is built by creating
// the current schema and then dropping those six back to their old shape, so every OTHER table is
// materialised from the same `.sq` the app ships — which is both honest (they genuinely did not
// change) and self-checking: if a future migration touches a seventh table without adding it here,
// its `SELECT old_column` runs against a table that already has the new shape and the test fails
// loudly rather than silently skipping the case.
//
// Nothing here may be "tidied" to match the current schema. It is a historical artifact; its whole
// value is being different.

/** §2.3 + §2.5 at version 1: masked phone columns, no `qr_claimed_at`. */
internal val PASSENGER_V1_DOWNGRADE = """
    DROP TABLE rides;
    DROP TABLE location_requests;

    CREATE TABLE rides (
        id                    TEXT NOT NULL PRIMARY KEY,
        client_request_id     TEXT NOT NULL,
        state                 TEXT NOT NULL,
        is_active             INTEGER NOT NULL DEFAULT 1,
        kind                  INTEGER NOT NULL DEFAULT 0,
        is_proxy              INTEGER NOT NULL DEFAULT 0,
        vehicle_type          TEXT NOT NULL,
        pickup_lat            REAL NOT NULL,
        pickup_lng            REAL NOT NULL,
        pickup_label          TEXT,
        dropoff_lat           REAL NOT NULL,
        dropoff_lng           REAL NOT NULL,
        dropoff_label         TEXT,
        rider_name            TEXT,
        rider_phone_masked    TEXT,
        package_size          TEXT CHECK (package_size IS NULL OR package_size IN ('S','M','L')),
        package_description   TEXT,
        accepted_driver_id    TEXT,
        driver_name           TEXT,
        driver_photo_url      TEXT,
        driver_rating         REAL,
        vehicle_reg           TEXT,
        vehicle_actual_type   TEXT,
        vehicle_lat           REAL,
        vehicle_lng           REAL,
        vehicle_heading_deg   INTEGER,
        offer_expires_at      INTEGER,
        fare_amount_minor     INTEGER,
        surcharge_minor       INTEGER NOT NULL DEFAULT 0,
        tip_amount_minor      INTEGER NOT NULL DEFAULT 0,
        payment_method        TEXT CHECK (payment_method IN ('cash','lankaqr','onepay','cod')),
        payment_state         TEXT,
        created_at            INTEGER NOT NULL,
        updated_at            INTEGER NOT NULL,
        terminal_at           INTEGER,
        server_updated_at     INTEGER,
        synced_at             INTEGER,
        driver_phone_masked   TEXT
    );
    CREATE INDEX ix_prides_active ON rides(is_active, updated_at DESC);
    CREATE INDEX ix_prides_history ON rides(created_at DESC);

    CREATE TABLE location_requests (
        request_id          TEXT NOT NULL PRIMARY KEY,
        ride_id             TEXT,
        rider_phone_masked  TEXT,
        state               TEXT NOT NULL DEFAULT 'Pending'
                              CHECK (state IN ('Pending','Confirmed','Declined','Expired','RiderNotRegistered')),
        issued_at           INTEGER NOT NULL,
        ttl_seconds         INTEGER NOT NULL DEFAULT 300,
        resolved_lat        REAL,
        resolved_lng        REAL,
        resolved_accuracy_m REAL,
        resolved_at         INTEGER
    );
""".trimIndent()

/** §3.3, §3.4, §3.6 and §3.15 at version 1. */
internal val DRIVER_V1_DOWNGRADE = """
    DROP TABLE active_ride;
    DROP TABLE dispatch_offers;
    DROP TABLE proof_upload_queue;
    DROP TABLE credit_transfers;

    CREATE TABLE active_ride (
        id                  TEXT NOT NULL PRIMARY KEY,
        state               TEXT NOT NULL,
        kind                INTEGER NOT NULL DEFAULT 0,
        is_proxy            INTEGER NOT NULL DEFAULT 0,
        rider_name          TEXT,
        rider_phone_masked  TEXT,
        pickup_lat          REAL NOT NULL,
        pickup_lng          REAL NOT NULL,
        pickup_label        TEXT,
        dropoff_lat         REAL NOT NULL,
        dropoff_lng         REAL NOT NULL,
        dropoff_label       TEXT,
        package_size        TEXT CHECK (package_size IS NULL OR package_size IN ('S','M','L')),
        package_description TEXT,
        needs_pickup_otp    INTEGER NOT NULL DEFAULT 0,
        needs_delivery_otp  INTEGER NOT NULL DEFAULT 0,
        needs_proof         INTEGER NOT NULL DEFAULT 0,
        payment_method      TEXT CHECK (payment_method IN ('cash','lankaqr','onepay','cod')),
        payment_state       TEXT,
        fare_amount_minor   INTEGER,
        surcharge_minor     INTEGER NOT NULL DEFAULT 0,
        tip_amount_minor    INTEGER NOT NULL DEFAULT 0,
        created_at          INTEGER NOT NULL,
        updated_at          INTEGER NOT NULL,
        server_updated_at   INTEGER
    );

    CREATE TABLE dispatch_offers (
        id                   TEXT NOT NULL PRIMARY KEY,
        ride_id              TEXT NOT NULL,
        vehicle_type         TEXT NOT NULL,
        pickup_lat           REAL NOT NULL,
        pickup_lng           REAL NOT NULL,
        pickup_label         TEXT,
        dropoff_lat          REAL NOT NULL,
        dropoff_lng          REAL NOT NULL,
        dropoff_label        TEXT,
        est_fare_minor       INTEGER,
        distance_to_pickup_m INTEGER,
        kind                 INTEGER NOT NULL DEFAULT 0,
        is_proxy             INTEGER NOT NULL DEFAULT 0,
        rider_name           TEXT,
        rider_phone_masked   TEXT,
        package_size         TEXT CHECK (package_size IS NULL OR package_size IN ('S','M','L')),
        package_description  TEXT,
        status               TEXT NOT NULL DEFAULT 'OFFERED'
                               CHECK (status IN ('OFFERED','ACCEPTED','DECLINED','EXPIRED')),
        sent_at              INTEGER NOT NULL,
        expires_at           INTEGER NOT NULL
    );
    CREATE INDEX ix_offers_live ON dispatch_offers(status, expires_at);

    CREATE TABLE proof_upload_queue (
        id            TEXT NOT NULL PRIMARY KEY,
        ride_id       TEXT NOT NULL,
        kind          TEXT NOT NULL CHECK (kind IN ('delivery_photo','signature','pickup_photo')),
        local_path    TEXT NOT NULL,
        sha256_hex    TEXT,
        captured_lat  REAL,
        captured_lng  REAL,
        captured_at   INTEGER NOT NULL,
        state         TEXT NOT NULL DEFAULT 'PENDING'
                        CHECK (state IN ('PENDING','UPLOADING','UPLOADED','FAILED')),
        attempts      INTEGER NOT NULL DEFAULT 0,
        storage_url   TEXT,
        next_retry_at INTEGER
    );
    CREATE INDEX ix_proof_dispatch ON proof_upload_queue(state, next_retry_at);

    CREATE TABLE credit_transfers (
        id                        TEXT NOT NULL PRIMARY KEY,
        direction                 TEXT NOT NULL CHECK (direction IN ('incoming','outgoing')),
        counterparty_driver_id    TEXT,
        counterparty_name         TEXT,
        counterparty_phone_masked TEXT,
        amount_minor              INTEGER NOT NULL,
        status                    TEXT NOT NULL CHECK (status IN ('PENDING','APPROVED','REJECTED')),
        created_at                INTEGER NOT NULL,
        synced_at                 INTEGER
    );
    CREATE INDEX ix_credit_transfers_recent ON credit_transfers(created_at DESC);
""".trimIndent()
