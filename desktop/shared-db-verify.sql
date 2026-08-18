-- Drift check for the DevStrider tables in the shared PostgreSQL database.
--
-- Run this after any schema change, and any time the app fails with SQLSTATE 42703
-- ("column ... does not exist"). It compares the live tables against shared-db-schema.sql
-- and names what is missing.
--
-- Why this file exists: a filtered listing of information_schema.columns cannot tell
-- "nullable, no default" apart from "not there at all" — both are simply absent from the
-- result. On the old peer_* tables a bid_id column was missing for days and read as
-- correctly-nullable.
-- This query names the missing columns instead of leaving you to spot a gap.

-- ── 0. The portal's table, which DevStrider depends on but does not own ──────────
-- Login reads app_user. If this returns rows, the portal's schema has moved and the
-- login will fail — that is a conversation with the portal's owner, not a fix here.
WITH expected(table_name, column_name) AS (VALUES
    ('app_user','id'), ('app_user','email'), ('app_user','password_hash'),
    ('app_user','email_verified')
)
SELECT e.table_name, e.column_name AS missing_column
FROM expected e
LEFT JOIN information_schema.columns c
       ON c.table_schema = 'public'
      AND c.table_name   = e.table_name
      AND c.column_name  = e.column_name
WHERE c.column_name IS NULL
ORDER BY e.table_name, e.column_name;

-- ── 1. Columns the app writes that the database does not have ───────────────
-- Anything returned here WILL fail at runtime with SQLSTATE 42703.
WITH expected(table_name, column_name) AS (VALUES
    ('ds_users','user_id'), ('ds_users','username'),
    ('ds_users','created_at'), ('ds_users','updated_at'),

    ('ds_profiles','id'), ('ds_profiles','user_id'), ('ds_profiles','name'),
    ('ds_profiles','slug'), ('ds_profiles','word_doc_path'), ('ds_profiles','macro_name'),
    ('ds_profiles','resume_prompt'), ('ds_profiles','headline'), ('ds_profiles','location'),
    ('ds_profiles','phone'), ('ds_profiles','personal_email'), ('ds_profiles','linkedin_url'),
    ('ds_profiles','highest_education'),
    ('ds_profiles','created_at'), ('ds_profiles','updated_at'),

    ('ds_bids','id'), ('ds_bids','user_id'), ('ds_bids','profile_id'), ('ds_bids','url'),
    ('ds_bids','url_norm'), ('ds_bids','marked_useless_at'), ('ds_bids','resume_id'),
    ('ds_bids','company'), ('ds_bids','role'), ('ds_bids','primary_stacks'),
    ('ds_bids','status'), ('ds_bids','origin'), ('ds_bids','job_description'),
    ('ds_bids','gpt_resume_content'), ('ds_bids','comment'), ('ds_bids','created_at'),
    ('ds_bids','updated_at'), ('ds_bids','applied_at'),

    ('ds_interviews','id'), ('ds_interviews','user_id'), ('ds_interviews','profile_id'),
    ('ds_interviews','bid_id'), ('ds_interviews','parent_interview_id'),
    ('ds_interviews','process_id'), ('ds_interviews','meeting_link'),
    ('ds_interviews','origin'), ('ds_interviews','interview_type'),
    ('ds_interviews','company'), ('ds_interviews','role'), ('ds_interviews','recruiter'),
    ('ds_interviews','additional_attendees'), ('ds_interviews','resume_id'),
    ('ds_interviews','scheduled_date'), ('ds_interviews','scheduled_time'),
    ('ds_interviews','duration_minutes'), ('ds_interviews','status'),
    ('ds_interviews','user_comment'), ('ds_interviews','attached_job_description'),
    ('ds_interviews','attached_resume_content'), ('ds_interviews','resume_object_key'),
    ('ds_interviews','resume_file_name'), ('ds_interviews','resume_size_bytes'),
    ('ds_interviews','resume_uploaded_at'), ('ds_interviews','created_at'),
    ('ds_interviews','updated_at')
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
  AND table_name IN ('ds_users','ds_profiles','ds_bids','ds_interviews')
ORDER BY table_name, ordinal_position;

-- ── 3. Foreign keys and indexes ─────────────────────────────────────────────
SELECT tc.table_name, tc.constraint_name, tc.constraint_type
FROM information_schema.table_constraints tc
WHERE tc.table_schema = 'public'
  AND tc.table_name IN ('ds_users','ds_profiles','ds_bids','ds_interviews')
ORDER BY tc.table_name, tc.constraint_type, tc.constraint_name;

SELECT tablename, indexname FROM pg_indexes
WHERE schemaname = 'public'
  AND tablename IN ('ds_users','ds_profiles','ds_bids','ds_interviews')
ORDER BY tablename, indexname;

-- ── 4. Orphans ──────────────────────────────────────────────────────────────
-- profile_id carries no foreign key (rows legitimately sit at '' until the app stamps the
-- active profile onto them), so nothing stops a bid pointing at a profile that is gone.
-- Rows here are invisible in the UI — they belong to no profile the switcher offers.
SELECT 'ds_bids' AS table_name, count(*) AS orphaned_rows
FROM ds_bids b
WHERE b.profile_id <> '' AND NOT EXISTS (SELECT 1 FROM ds_profiles p WHERE p.id = b.profile_id)
UNION ALL
SELECT 'ds_interviews', count(*)
FROM ds_interviews i
WHERE i.profile_id <> '' AND NOT EXISTS (SELECT 1 FROM ds_profiles p WHERE p.id = i.profile_id);
