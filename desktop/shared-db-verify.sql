-- Drift check for the DevStrider tables in the shared PostgreSQL database.
--
-- Run this after any schema change, and any time sync fails with SQLSTATE 42703
-- ("column ... does not exist"). It compares the live tables against shared-db-schema.sql
-- and reports what is missing.
--
-- Why this file exists: a filtered listing of information_schema.columns cannot tell
-- "nullable, no default" apart from "not there at all" — both are simply absent from the
-- result. peer_interviews.bid_id was missing for days and read as correctly-nullable.
-- This query names the missing columns instead of leaving you to spot a gap.


-- ── 1. Columns the app writes that the database does not have ───────────────
-- Anything returned here WILL fail a sync with SQLSTATE 42703.
WITH expected(table_name, column_name) AS (VALUES
    ('peer_users','id'), ('peer_users','username'), ('peer_users','profile_slug'),
    ('peer_users','profile_name'), ('peer_users','email'),
    ('peer_users','created_at'), ('peer_users','updated_at'),

    ('peer_bids','id'), ('peer_bids','owner_user_id'), ('peer_bids','company'),
    ('peer_bids','role'), ('peer_bids','status'), ('peer_bids','origin'),
    ('peer_bids','resume_id'), ('peer_bids','primary_stacks'), ('peer_bids','job_description'),
    ('peer_bids','created_at'), ('peer_bids','updated_at'), ('peer_bids','first_created_at'),
    ('peer_bids','applied_at'),

    ('peer_interviews','id'), ('peer_interviews','owner_user_id'), ('peer_interviews','bid_id'),
    ('peer_interviews','process_id'), ('peer_interviews','company'), ('peer_interviews','role'),
    ('peer_interviews','interview_type'), ('peer_interviews','status'),
    ('peer_interviews','recruiter'), ('peer_interviews','resume_id'),
    ('peer_interviews','job_description'), ('peer_interviews','scheduled_date'),
    ('peer_interviews','scheduled_time'), ('peer_interviews','duration_minutes'),
    ('peer_interviews','resume_object_key'), ('peer_interviews','resume_file_name'),
    ('peer_interviews','created_at'), ('peer_interviews','updated_at')
)
SELECT e.table_name, e.column_name AS missing_column
FROM expected e
LEFT JOIN information_schema.columns c
       ON c.table_schema = 'public'
      AND c.table_name   = e.table_name
      AND c.column_name  = e.column_name
WHERE c.column_name IS NULL
ORDER BY e.table_name, e.column_name;


-- ── 2. Every column, unfiltered ─────────────────────────────────────────────
-- Nullability and defaults for the whole set. Unlike a filtered listing, a column that is
-- absent here is genuinely absent.
SELECT table_name, ordinal_position AS pos, column_name, data_type, is_nullable, column_default
FROM information_schema.columns
WHERE table_schema = 'public'
  AND table_name IN ('peer_users','peer_bids','peer_interviews')
ORDER BY table_name, ordinal_position;


-- ── 3. Foreign keys and indexes ─────────────────────────────────────────────
SELECT tc.table_name, tc.constraint_name, tc.constraint_type
FROM information_schema.table_constraints tc
WHERE tc.table_schema = 'public'
  AND tc.table_name IN ('peer_users','peer_bids','peer_interviews')
ORDER BY tc.table_name, tc.constraint_type, tc.constraint_name;

SELECT tablename, indexname FROM pg_indexes
WHERE schemaname = 'public'
  AND tablename IN ('peer_users','peer_bids','peer_interviews')
ORDER BY tablename, indexname;
