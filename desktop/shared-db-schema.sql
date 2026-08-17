-- =====================================================================================
--  DevStrider — shared PostgreSQL schema
--
--  Complete, current, and self-contained. Run this whole file in your SQL editor, once.
--  DevStrider issues NO DDL, so this file is the only thing that defines its tables.
--
--  DevStrider is a Windows desktop app plus this database. There is no server and no web
--  client. Every machine reads and writes these tables directly.
-- =====================================================================================
--
--  ⚠ THIS DATABASE IS SHARED WITH THE COMPANY PORTAL.
--
--  The DROP statements below name DevStrider's eight tables explicitly and nothing else.
--  Never replace them with anything that enumerates tables — a wildcard drop here would
--  take out roughly fifty tables belonging to the portal.
--
--  `app_user` belongs to the portal. DevStrider only ever SELECTs from it: it is the
--  login, and it is where accounts are created. This file does not define it, does not
--  alter it, and adds no columns to it — everything DevStrider needs to remember about a
--  person lives in `ds_users`, keyed by `app_user.id`.
--
--  ⚠ EVERYONE WITH THIS LOGIN CAN READ EVERYTHING IN HERE.
--
--  These tables are not a stripped projection of anything. ds_bids.url,
--  ds_bids.job_description, ds_bids.gpt_resume_content and ds_bids.comment are the full
--  private values that used to stay on the author's machine. Treat every column here as
--  visible to every teammate.
--
--  ⚠ THESE TABLES ARE THE ONLY COPY.
--
--  Once a machine has been migrated off its local MongoDB, nothing else holds that
--  person's bids and interviews.
-- =====================================================================================


-- ── 0. Drop ─────────────────────────────────────────────────────────────────────────
-- Children before parents. CASCADE also removes the foreign keys pointing in.
DROP TABLE IF EXISTS ds_achievements   CASCADE;
DROP TABLE IF EXISTS ds_interviews     CASCADE;
DROP TABLE IF EXISTS ds_bids           CASCADE;
DROP TABLE IF EXISTS ds_experiences    CASCADE;
DROP TABLE IF EXISTS ds_certifications CASCADE;
DROP TABLE IF EXISTS ds_education      CASCADE;
DROP TABLE IF EXISTS ds_profiles       CASCADE;
DROP TABLE IF EXISTS ds_users          CASCADE;


-- ── About the shape of these tables ─────────────────────────────────────────────────
--
--  `id` columns are MongoDB ObjectId hex strings — 24 characters — carried over from the
--  local databases this replaces. Keeping the original identity means the one-time import
--  is an idempotent upsert and can be re-run without creating duplicates.
--
--  `user_id` is the person, and it is `app_user.id` rather than a name. Every table under
--  a profile carries it, and every query DevStrider issues filters on it. Bids and
--  interviews could have been reached through profile_id alone — ObjectIds are unique —
--  but the queries that are NOT profile-scoped (the achievement counters, which are per
--  person and not per bidding identity) would then have had no way to tell your rows from
--  a teammate's. In one shared database that is the difference between counting your bids
--  and counting everybody's.
--
--  Why the id and not the username: a username is display text and the user can change it.
--  The old design keyed rows by that string, so a rename orphaned every row behind it.
--
--  `profile_id` deliberately has NO foreign key, and holds '' rather than NULL for "none".
--  '' is how ObjectId.Empty round-trips, and rows legitimately sit there: the app repairs
--  unassigned rows by stamping the active profile onto them, which an FK would forbid.
--  The CV tables below DO have one — those rows are only ever created through a profile.


-- ── 1. The person ───────────────────────────────────────────────────────────────────
-- One row per app_user who has ever logged into DevStrider, created on first login.
-- The portal owns the account; this owns what DevStrider knows about it.
--
-- Goals live here rather than on a profile because they are per person: someone running
-- three bidding identities has one daily bid target, not three.
CREATE TABLE ds_users (
    user_id             BIGINT      PRIMARY KEY REFERENCES app_user(id) ON DELETE CASCADE,
    -- The devstrider user name — one per account. Lower-cased with spaces as dashes; the
    -- app normalises before writing, and this constraint is on the normalised form.
    username            TEXT        NOT NULL,
    bids_per_day        INTEGER     NOT NULL DEFAULT 0,
    interviews_per_week INTEGER     NOT NULL DEFAULT 0,
    offers_per_month    INTEGER     NOT NULL DEFAULT 0,
    created_at          TIMESTAMPTZ NOT NULL,
    updated_at          TIMESTAMPTZ NOT NULL,
    CONSTRAINT ds_users_username_key UNIQUE (username)
);


-- ── 2. Bidding identities ───────────────────────────────────────────────────────────
-- The title-bar profile switcher. One account has several; each represents a different
-- real person whose bids and interviews are tracked in isolation — which is why the CV
-- hangs off this table and not off ds_users.
--
-- word_doc_path and macro_name name a file on one Windows machine. They mean nothing on
-- another, but they travel with the profile so a reinstall restores them.
CREATE TABLE ds_profiles (
    id             TEXT        PRIMARY KEY,
    user_id        BIGINT      NOT NULL REFERENCES ds_users(user_id) ON DELETE CASCADE,
    name           TEXT        NOT NULL DEFAULT '',   -- real human name, shown in the switcher
    slug           TEXT        NOT NULL DEFAULT '',   -- FS-safe; used in snapshot filenames
    word_doc_path  TEXT        NOT NULL DEFAULT '',
    macro_name     TEXT        NOT NULL DEFAULT '',
    resume_prompt  TEXT        NOT NULL DEFAULT '',
    headline       TEXT        NOT NULL DEFAULT '',
    location       TEXT        NOT NULL DEFAULT '',
    phone          TEXT        NOT NULL DEFAULT '',
    -- The address that goes on the resume. NOT the login — that is app_user.email.
    personal_email TEXT        NOT NULL DEFAULT '',
    linkedin_url   TEXT        NOT NULL DEFAULT '',
    created_at     TIMESTAMPTZ NOT NULL,
    updated_at     TIMESTAMPTZ NOT NULL,
    -- Two people can both have a profile slugged "default"; one person cannot.
    CONSTRAINT ds_profiles_slug_key UNIQUE (user_id, slug)
);

CREATE INDEX ix_ds_profiles_user ON ds_profiles (user_id, created_at);


-- ── 3. The CV ───────────────────────────────────────────────────────────────────────
-- One row per entry, so the portal can query across them ("who holds an AWS cert").
--
-- sort_order is not decoration: a CV is an ordered document and rows have no inherent
-- order. DevStrider writes the list position; anything reading these must ORDER BY it or
-- the resume comes out shuffled.
--
-- Years are INTEGER and nullable — "2019" with no month is what a CV actually states, and
-- NULL is the ongoing role or the unfinished degree.

CREATE TABLE ds_education (
    id         TEXT    PRIMARY KEY,
    profile_id TEXT    NOT NULL REFERENCES ds_profiles(id) ON DELETE CASCADE,
    degree     TEXT    NOT NULL DEFAULT '',
    school     TEXT    NOT NULL DEFAULT '',
    location   TEXT    NOT NULL DEFAULT '',
    start_year INTEGER,
    end_year   INTEGER,
    sort_order INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX ix_ds_education_profile ON ds_education (profile_id, sort_order);


CREATE TABLE ds_certifications (
    id         TEXT    PRIMARY KEY,
    profile_id TEXT    NOT NULL REFERENCES ds_profiles(id) ON DELETE CASCADE,
    name       TEXT    NOT NULL DEFAULT '',
    issuer     TEXT    NOT NULL DEFAULT '',
    year       INTEGER,
    sort_order INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX ix_ds_certifications_profile ON ds_certifications (profile_id, sort_order);


CREATE TABLE ds_experiences (
    id         TEXT    PRIMARY KEY,
    profile_id TEXT    NOT NULL REFERENCES ds_profiles(id) ON DELETE CASCADE,
    company    TEXT    NOT NULL DEFAULT '',
    location   TEXT    NOT NULL DEFAULT '',
    start_year INTEGER,
    end_year   INTEGER,
    sort_order INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX ix_ds_experiences_profile ON ds_experiences (profile_id, sort_order);


-- ── 4. Bids ─────────────────────────────────────────────────────────────────────────
-- A bid and the job posting it was made against are one row.
--
-- They used to be two — a `links` collection with the URL, and a bid pointing at it — but
-- the relationship was always one-to-one, and a link with no bid behind it is precisely
-- what `status = 'draft'` already means. So: the row is created when the URL is captured,
-- and filled in when the bid is actually made.
--
-- That merge folded three link columns away. applied_company / applied_role /
-- applied_stacks duplicated company / role / primary_stacks with a fallback between them;
-- shared_job_description was a second snapshot of job_description that the JD viewer
-- already fell back to. One row, one answer.
CREATE TABLE ds_bids (
    id                 TEXT        PRIMARY KEY,
    user_id            BIGINT      NOT NULL REFERENCES ds_users(user_id) ON DELETE CASCADE,
    profile_id         TEXT        NOT NULL DEFAULT '',

    -- ── the posting ──
    url                TEXT        NOT NULL DEFAULT '',
    -- Canonical form for dedup: lower-cased href, trailing slash trimmed, query + hash
    -- kept. Different query strings are different postings, deliberately.
    url_norm           TEXT        NOT NULL DEFAULT '',
    -- Set when the posting is written off as not worth bidding on. Distinct from having
    -- no bid yet, which is simply status = 'draft'.
    marked_useless_at  TIMESTAMPTZ,

    -- ── the bid ──
    resume_id          TEXT        NOT NULL DEFAULT '',   -- the UID from the fast-feed line
    company            TEXT        NOT NULL DEFAULT '',
    role               TEXT        NOT NULL DEFAULT '',
    primary_stacks     TEXT[]      NOT NULL DEFAULT '{}',
    status             TEXT        NOT NULL DEFAULT 'draft',
    origin             TEXT        NOT NULL DEFAULT '',
    job_description    TEXT        NOT NULL DEFAULT '',
    gpt_resume_content TEXT        NOT NULL DEFAULT '',
    comment            TEXT        NOT NULL DEFAULT '',

    created_at         TIMESTAMPTZ NOT NULL,              -- when the URL was captured
    updated_at         TIMESTAMPTZ NOT NULL,
    -- First moment the row moved off 'draft'. Set once, then locked — anything counting
    -- real bids by when they were sent reads this, not created_at.
    applied_at         TIMESTAMPTZ
);

-- The bid board loads a profile's rows newest-first on every day change.
CREATE INDEX ix_ds_bids_profile     ON ds_bids (profile_id, created_at DESC);
-- Dedup lookup on every capture, from the UI and from the Chrome extension.
CREATE INDEX ix_ds_bids_urlnorm     ON ds_bids (profile_id, url_norm);
-- The Find-bid search window.
CREATE INDEX ix_ds_bids_profile_upd ON ds_bids (profile_id, updated_at DESC);
-- The achievement counters are per person, across every profile.
CREATE INDEX ix_ds_bids_user_upd    ON ds_bids (user_id, updated_at DESC);


-- ── 5. Interviews ───────────────────────────────────────────────────────────────────
-- bid_id and process_id hold '' rather than NULL for "none", matching how the app's
-- ObjectId.Empty round-trips. An interview that came from a LinkedIn chat genuinely has
-- no bid behind it. resume_object_key points at Cloudflare R2; the file itself is never
-- in any database.
CREATE TABLE ds_interviews (
    id                       TEXT        PRIMARY KEY,
    user_id                  BIGINT      NOT NULL REFERENCES ds_users(user_id) ON DELETE CASCADE,
    profile_id               TEXT        NOT NULL DEFAULT '',
    bid_id                   TEXT        NOT NULL DEFAULT '',
    parent_interview_id      TEXT,                          -- NULL when this is a first round
    -- Groups every round of one hiring process — HR, Tech 1, Tech 2, Offer — so a pipeline
    -- reads as one thing instead of as loose rounds.
    process_id               TEXT        NOT NULL DEFAULT '',
    meeting_link             TEXT        NOT NULL DEFAULT '',
    origin                   TEXT        NOT NULL DEFAULT '',
    interview_type           TEXT        NOT NULL DEFAULT '',
    company                  TEXT        NOT NULL DEFAULT '',
    role                     TEXT        NOT NULL DEFAULT '',
    recruiter                TEXT        NOT NULL DEFAULT '',
    additional_attendees     TEXT[]      NOT NULL DEFAULT '{}',
    resume_id                TEXT        NOT NULL DEFAULT '',
    scheduled_date           TIMESTAMPTZ,                   -- NULL until a date is set
    scheduled_time           TEXT        NOT NULL DEFAULT '',
    duration_minutes         INTEGER,                       -- NULL when unknown
    status                   TEXT        NOT NULL DEFAULT '',
    user_comment             TEXT        NOT NULL DEFAULT '',
    attached_job_description TEXT        NOT NULL DEFAULT '',
    attached_resume_content  TEXT        NOT NULL DEFAULT '',
    resume_object_key        TEXT        NOT NULL DEFAULT '',
    resume_file_name         TEXT        NOT NULL DEFAULT '',
    resume_size_bytes        BIGINT      NOT NULL DEFAULT 0,
    resume_uploaded_at       TIMESTAMPTZ,
    created_at               TIMESTAMPTZ NOT NULL,
    updated_at               TIMESTAMPTZ NOT NULL
);

CREATE INDEX ix_ds_ivs_profile  ON ds_interviews (profile_id, scheduled_date);
CREATE INDEX ix_ds_ivs_bid      ON ds_interviews (bid_id);
CREATE INDEX ix_ds_ivs_process  ON ds_interviews (process_id);
CREATE INDEX ix_ds_ivs_user_upd ON ds_interviews (user_id, updated_at DESC);


-- ── 6. Achievements ─────────────────────────────────────────────────────────────────
-- One row per goal actually hit, so a streak survives a reinstall. Per person and not per
-- profile, for the same reason the goals themselves are.
CREATE TABLE ds_achievements (
    id           TEXT        PRIMARY KEY,
    user_id      BIGINT      NOT NULL REFERENCES ds_users(user_id) ON DELETE CASCADE,
    kind         TEXT        NOT NULL,   -- daily_bids | weekly_interviews | monthly_offers
    period_key   TEXT        NOT NULL,   -- '2026-05-25' | '2026-W21' | '2026-05'
    metric_value INTEGER     NOT NULL DEFAULT 0,
    target       INTEGER     NOT NULL DEFAULT 0,
    achieved_at  TIMESTAMPTZ NOT NULL,
    -- One award per person per period. This is what makes the write an upsert.
    CONSTRAINT ds_achievements_period_key UNIQUE (user_id, kind, period_key)
);


-- ── 7. Verify ───────────────────────────────────────────────────────────────────────
-- Eight rows, with these column counts. Anything else means part of this file didn't run:
--     ds_achievements    7
--     ds_bids           18
--     ds_certifications  6
--     ds_education       8
--     ds_experiences     7
--     ds_interviews     27
--     ds_profiles       14
--     ds_users           7
SELECT table_name, count(*) AS columns
FROM information_schema.columns
WHERE table_schema = 'public'
  AND table_name IN ('ds_users', 'ds_profiles', 'ds_education', 'ds_certifications',
                     'ds_experiences', 'ds_bids', 'ds_interviews', 'ds_achievements')
GROUP BY table_name
ORDER BY table_name;

-- For a column-by-column drift check, run shared-db-verify.sql.


-- =====================================================================================
--  8. Retiring the peer_* tables — RUN LAST, AND ONLY WHEN EVERY MACHINE IS MIGRATED
-- =====================================================================================
--
--  peer_users / peer_bids / peer_interviews were a stripped mirror: each machine kept its
--  real data in a private local MongoDB and pushed summaries here for teammates to see.
--  With everything in this database, "what my teammates are doing" is just ds_bids and
--  ds_interviews filtered to other user_ids. A mirror of a table sitting next to it is
--  pure duplication, so these three go — and PeerSyncService's push, pull and delta marker
--  go with them.
--
--  ⚠ Do NOT run this section until every person has imported their local MongoDB into the
--  ds_* tables above. Until then peer_* is the only shared record of their work, and the
--  import reads from their machine, not from here.
--
--  Uncomment to run:
--
-- DROP TABLE IF EXISTS peer_interviews CASCADE;
-- DROP TABLE IF EXISTS peer_bids       CASCADE;
-- DROP TABLE IF EXISTS peer_users      CASCADE;
