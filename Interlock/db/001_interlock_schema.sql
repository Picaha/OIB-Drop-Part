-- Interlock schema (design spec section 4). Safe to run again: every statement is IF NOT EXISTS.
-- Every object is qualified with "interlock."; the integration tests run this script with that prefix replaced.
-- All timestamps are timestamptz and hold UTC instants. Enum columns hold the upper-case texts of Interlock.Core DbEnum.
-- target_norm and uid_norm are written by the service (Normalizer.Norm: trim every kind of whitespace, upper-case),
-- not computed here: SQL trim strips spaces only, and scanners add CR/LF.

CREATE SCHEMA IF NOT EXISTS interlock;

CREATE TABLE IF NOT EXISTS interlock.lock (
    id             bigserial   PRIMARY KEY,
    lock_type      text        NOT NULL CONSTRAINT lock_lock_type_ck CHECK (lock_type IN ('FEEDER', 'PU', 'PN', 'MPN', 'SUPPLIER', 'CONVEYOR')),
    target         text        NOT NULL,
    target_norm    text        NOT NULL,
    lanes          text        NULL     CONSTRAINT lock_lanes_ck CHECK (lanes IN ('LEFT', 'RIGHT', 'BOTH')),
    line_path      text        NULL,
    reason         text        NOT NULL,
    source         text        NOT NULL CONSTRAINT lock_source_ck CHECK (source IN ('MANUAL', 'API', 'SETUPCENTER')),
    created_by     text        NOT NULL,
    created_at     timestamptz NOT NULL,
    status         text        NOT NULL CONSTRAINT lock_status_ck CHECK (status IN ('ACTIVE', 'RELEASED')),
    released_by    text        NULL,
    released_at    timestamptz NULL,
    release_reason text        NULL,
    enforce_state  text        NOT NULL CONSTRAINT lock_enforce_state_ck CHECK (enforce_state IN ('PENDING', 'APPLIED', 'FAILED', 'NOT_NEEDED')),
    enforce_error  text        NULL
);

-- The PUs a PN/MPN/SUPPLIER sweep locked, so a release unlocks exactly those. An open row has released_at NULL.
-- uid_norm is not in the spec's column list: it is Normalizer.Norm(uid), used for matching.
CREATE TABLE IF NOT EXISTS interlock.lock_applied_pu (
    id          bigserial   PRIMARY KEY,
    lock_id     bigint      NOT NULL REFERENCES interlock.lock (id),
    uid         text        NOT NULL,
    uid_norm    text        NOT NULL,
    applied_at  timestamptz NOT NULL,
    released_at timestamptz NULL
);

-- One row per OIB call; lock_id is NULL for calls that belong to no lock.
CREATE TABLE IF NOT EXISTS interlock.action_log (
    id           bigserial   PRIMARY KEY,
    lock_id      bigint      NULL,
    action       text        NOT NULL,
    request_json text        NULL,
    success      boolean     NOT NULL,
    error        text        NULL,
    attempt      integer     NOT NULL,
    created_at   timestamptz NOT NULL
);

-- One row per External Control answer.
CREATE TABLE IF NOT EXISTS interlock.decision_log (
    id               bigserial   PRIMARY KEY,
    method           text        NOT NULL,
    reason_code      integer     NULL,
    uid              text        NULL,
    pn               text        NULL,
    mpn              text        NULL,
    supplier         text        NULL,
    line_path        text        NULL,
    machine_id       text        NULL,
    feeder_id        text        NULL,
    result           text        NOT NULL,
    message          text        NULL,
    matched_lock_ids text        NULL,
    created_at       timestamptz NOT NULL
);

CREATE TABLE IF NOT EXISTS interlock.app_user (
    id         bigserial   PRIMARY KEY,
    username   text        NOT NULL,
    pin_hash   text        NOT NULL,
    role       text        NOT NULL CONSTRAINT app_user_role_ck CHECK (role IN ('OPERATOR', 'QA', 'ENGINEER', 'ADMIN')),
    active     boolean     NOT NULL,
    created_at timestamptz NOT NULL
);

CREATE TABLE IF NOT EXISTS interlock.api_key (
    id         bigserial   PRIMARY KEY,
    name       text        NOT NULL,
    key_hash   text        NOT NULL,
    can_unlock boolean     NOT NULL,
    active     boolean     NOT NULL,
    created_at timestamptz NOT NULL
);

-- At most one ACTIVE lock per identity: type, normalised target, line compared ignoring case and surrounding whitespace
-- (blank = plant-wide, like NULL), and lanes. The line expression is the one FindActive uses. A violation is reported to
-- the service as DuplicateActiveLockException.
CREATE UNIQUE INDEX IF NOT EXISTS lock_active_uq ON interlock.lock (lock_type, target_norm, upper(btrim(coalesce(line_path, ''), E' \t\r\n')), coalesce(lanes, '')) WHERE status = 'ACTIVE';
CREATE INDEX IF NOT EXISTS lock_status_ix ON interlock.lock (status);

CREATE INDEX IF NOT EXISTS lock_applied_pu_open_lock_ix ON interlock.lock_applied_pu (lock_id) WHERE released_at IS NULL;
CREATE INDEX IF NOT EXISTS lock_applied_pu_open_uid_ix ON interlock.lock_applied_pu (uid_norm) WHERE released_at IS NULL;
-- At most one open row per lock and PU, so recording a PU again is a no-op (ON CONFLICT DO NOTHING).
CREATE UNIQUE INDEX IF NOT EXISTS lock_applied_pu_open_uq ON interlock.lock_applied_pu (lock_id, uid_norm) WHERE released_at IS NULL;

CREATE INDEX IF NOT EXISTS action_log_lock_ix ON interlock.action_log (lock_id);

CREATE INDEX IF NOT EXISTS decision_log_created_ix ON interlock.decision_log (created_at);
-- The same expression as the decision log search on uid, so the search can use it.
CREATE INDEX IF NOT EXISTS decision_log_uid_ix ON interlock.decision_log (upper(btrim(uid, E' \t\r\n')));

-- Names are unique ignoring case; SaveUser upserts on this index.
CREATE UNIQUE INDEX IF NOT EXISTS app_user_username_uq ON interlock.app_user (lower(username));
CREATE UNIQUE INDEX IF NOT EXISTS api_key_name_uq ON interlock.api_key (lower(name));
-- One key per hash; also the index for the lookup on every API request.
CREATE UNIQUE INDEX IF NOT EXISTS api_key_hash_uq ON interlock.api_key (key_hash);
