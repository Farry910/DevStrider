# DevStrider: WebView, Resume Studio, and Communication Automation Plan

## Purpose

Move resume generation out of the browser-driven Chrome extension workflow and into
DevStrider. The desktop app will generate and review a resume directly, run the
existing Word macro, and use an embedded browser only for job-site tasks such as
extracting a job description and filling an application form.

The plan also introduces a safe path for Gmail and Google Calendar automation. It
does **not** make a paid ChatGPT subscription an application API: the native generation
workflow requires an OpenAI API project and API key. Store that key with Windows
Credential Manager, not in `settings.json` or PostgreSQL.

## Implementation decision — ChatGPT UI only

The current implementation deliberately uses the user's signed-in ChatGPT UI rather than
the OpenAI API. Resume Studio embeds one persistent ChatGPT WebView2 profile and keeps
the interaction user-driven: DevStrider copies the profile prompt or JD; the user pastes
it into ChatGPT and pastes the completed answer back into DevStrider. This supports a
single conversation for several JDs without attempting to automate or scrape ChatGPT's
private UI.

The implemented session default is five resumes, configurable from one through ten per
profile, and the Word-macro automation toggle is stored as a non-secret local preference.
API-specific generation, credential storage for API keys, streaming, token accounting,
and background Gmail/Calendar polling remain deferred unless an API/back-end integration
is later chosen.

### Assisted automation workflow

`Assisted automation` is the name for the Gmail/Calendar path in this release. It means
ChatGPT performs the connected-app research in its own UI, while DevStrider applies only
the proposals a user explicitly selects.

1. The user connects Gmail and Calendar to ChatGPT in their own ChatGPT settings.
2. In **Job Operations**, DevStrider copies a fixed review prompt for the user to paste
   into ChatGPT.
3. ChatGPT returns only a JSON `actions` array, including company, role, evidence, and
   optional interview time/link.
4. The user pastes the JSON into DevStrider.
5. DevStrider exact-matches company + role against the active profile's bids, shows each
   result, and disables unmatched or ambiguous proposals.
6. The user selects actions to apply. Applied actions are recorded in Activity; an
   interview also preserves the ChatGPT-provided evidence in its user comment.

Supported first-release actions are `mark_bid_rejected` and `create_interview`. Inbox
polling, hidden browser scraping, automatic application submission, and automatic status
changes are explicitly out of scope for assisted automation.

### Embedded job browser

The desktop app also contains an embedded **Job Browser** with an isolated persistent
WebView2 profile. It opens job sites in the user's own signed-in session, extracts visible
page text for review, and copies that text to the active ChatGPT resume conversation. This
replaces the browser-extension requirement for JD capture in the current flow.

Individual site adapters and field filling are deliberately disabled until a target site
is named and its form is tested. A generic browser must not guess which fields are safe to
write or submit; each adapter will need a separately reviewed selector contract.

## Product boundaries

| Workflow | What is automated | What remains explicit |
|---|---|---|
| Resume Studio | Generate, validate, save, and optionally run the Word macro | Final review when automatic macro is off |
| Job Browser | Extract a JD and fill known form fields | Job-site submission by default |
| Mail automation | Detect messages and propose changes | Low-confidence updates and outbound replies |
| Calendar automation | Propose or create interview events after a matched message | Conflict resolution and ambiguous scheduling |

### Automatic macro toggle

Add a profile-level setting named **Automatically generate Word resume after validation**.

- Default: disabled.
- Disabled: `Generate -> Review/Edit -> Generate Word Resume`.
- Enabled: `Generate -> Validate -> Save draft -> Run Word macro`.
- This setting must never submit a job application. Site submission is a distinct,
  future per-site opt-in capability.
- A failed validation, cancelled generation, unavailable Word template, or failed macro
  leaves the draft intact and shows a visible failure state.
- Every automatic macro invocation is activity-logged with the bid, profile, prompt
  version, model, effort, and result.

### Reusable generation sessions

Resume generation must not rebuild the profile's full resume prompt from scratch for
every JD. Add a bounded **generation session** that loads the profile prompt once and
processes several JDs using the same prompt-cache group.

- Default maximum resumes per session: **5**.
- User setting: editable per profile, from 1 to 10; 10 is the initial hard safety cap.
- The Resume Studio shows `3 of 5 resumes generated in this session`, plus **End session**
  and **Start new session** actions.
- A session ends automatically when it reaches its configured limit, its profile/prompt,
  model, or effort changes, the cache window expires, the user signs out, or the user
  explicitly ends it.
- The shared profile prompt is cacheable context. Each JD and its output remain an
  independent generation request so one job's facts cannot contaminate another job's
  resume.
- Record session ID, prompt version, model, effort, sequence number, input/output token
  totals, and cached-token totals for every generation. The UI should report observed
  cost and cache effectiveness rather than promise a fixed saving.

This is the API equivalent of keeping one ChatGPT tab open, but without browser rendering
or fragile DOM automation. Do not blindly chain every prior resume through one long chat:
that grows context, can dilute instructions, and can leak details from an earlier JD.

## Target architecture

```text
                           +-------------------+
                           | Resume Studio UI  |
                           +---------+---------+
                                     |
                     JD + profile + selected model/effort
                                     |
                           +---------v---------+
                           | ResumeGeneration  |
                           | Service           |
                           +----+---------+----+
                                |         |
                    API response|         |validated ResumeDraft
                                |         v
                         Credential Store  +--------------------+
                                |          | Bid + Word services|
                                |          +--------------------+
                         OpenAI Responses API

 +------------------+        +---------------------+        +------------------+
 | Embedded WebView |<------>| IJobSiteAdapter     |------->| Job-site forms   |
 +------------------+        +---------------------+        +------------------+

 Gmail / Calendar -> Google integration -> classifier + matcher -> review queue -> audited update
```

The existing `WordMacroService`, `BidBoardService`, `FastFeed`, profiles, and local
activity log remain the integration points. The first release can continue to transform
a validated draft into the current label-based macro input. A later release should
replace trailing fast-feed parsing with a structured response contract.

## Phase 0 — prerequisites and decisions

1. Create an OpenAI API project, enable API billing, and create a project-scoped key.
   ChatGPT subscription billing is separate from API billing.
2. Select the initial supported model list and effort levels. Do not let users type an
   arbitrary model identifier: model capabilities and accepted effort values vary.
3. Decide whether the API key is per user or owned by a server:
   - **Per user/local key:** fastest first release; store each key in Credential Manager.
   - **Backend-owned key:** preferred for a shared team; the desktop app authenticates to
     a backend, and the key never reaches client machines.
4. Establish a small anonymized JD test set and success criteria before selecting a
   default model/effort.

**Exit criteria:** an API credential strategy, supported generation choices, and a
representative quality test set are approved.

## Phase 1 — secure credential storage

### New services

- `ICredentialStore`
- `WindowsCredentialStore`
- `OpenAiSettings` (non-secret preferences only: model, effort, automatic macro toggle)

### Credential names

Use stable, account-scoped names, for example:

- `DevStrider/OpenAI/<app-user-id>`
- `DevStrider/GoogleOAuth/<app-user-id>`

Do not write secret values into `AppSettings`, `settings.json`, activity logs, exception
text, SQL, or telemetry. Read a secret only at the call site that needs it and redact it
from errors.

**Exit criteria:** a credential can be saved, read, replaced, and deleted without a
secret appearing in application settings or logs.

## Phase 2 — Resume Studio

### UI

Add a `Resume Studio` navigation item containing:

- profile selector (defaulting to the active profile);
- job URL, company, role, and JD input;
- model and reasoning-effort selectors;
- Generate, Cancel, Retry, Save draft, and Generate Word Resume actions;
- streaming, editable resume preview;
- validation/errors panel;
- automatic-macro toggle in the profile settings page.

The Generate button remains disabled until the selected profile has a resume prompt and
a JD is present. The macro action remains disabled until the draft validates and the
profile has a valid Word document/macro configuration.

### New types

- `ResumeDraft`: canonical structured response model.
- `ResumeGenerationRequest`: profile snapshot, JD, job metadata, selected model/effort,
  prompt version, and generation-session context.
- `ResumeGenerationResult`: draft, response identifier, usage summary, duration, and
  validation issues.
- `ResumeDraftValidator`: validates section completeness, metadata, size limits, and
  macro compatibility.
- `IResumeGenerationService`: streams output and supports cancellation.
- `ResumeGenerationSession`: profile/prompt snapshot, selected model/effort, configured
  generation limit, completed count, cache key, and expiry state.

### Generation contract

Request structured fields for title, summary, skills, subtitles, three experience blocks,
folder metadata, and fast-feed metadata. Then locally render that data into the existing
`[Section]:` format consumed by the Word macro. Retain `FastFeed.SplitTrailing` only as
a backward-compatible parser for legacy extension-generated content.

For API requests, keep the complete profile prompt as a stable prefix and apply a stable,
profile-and-prompt-version-specific `prompt_cache_key`. Start each JD as an independent
request with that same cached prefix. Measure `cached_tokens` and `cache_write_tokens`
from each response. Use `previous_response_id` only for an intentional multi-turn edit
of the **same** resume, not as the default way to generate separate resumes for separate
JDs.

On successful validation:

1. Create or update the bid as `draft` with its JD and generated content.
2. Save generation metadata.
3. If automatic macro is enabled, invoke `WordMacroService`.
4. Never switch the bid to `applied` merely because a resume was generated. That change
   occurs only after an application was actually submitted or explicitly confirmed.

**Exit criteria:** an operator can generate, edit, validate, save, and manually run a
resume; the automatic-macro setting performs the same macro step after validation.

## Phase 3 — persistence and migrations

Do not re-run `shared-db-schema.sql` on a database containing data: it intentionally
drops DevStrider tables. Add numbered, non-destructive migration scripts instead.

### Proposed tables

`ds_resume_generations`

- `id`, `user_id`, `profile_id`, `bid_id`
- `prompt_version`, `model`, `reasoning_effort`
- `request_hash`, `response_id`, `status`, `error_code`
- `generation_session_id`, `session_sequence`, `prompt_cache_key`
- `input_tokens`, `cached_input_tokens`, `cache_write_tokens`, `output_tokens`
- `generated_at`, `completed_at`, `duration_ms`
- `output_json` or a normalized snapshot reference

`ds_automation_events`

- `id`, `user_id`, `profile_id`
- `provider` (`gmail`, `calendar`, `job-site`, `system`)
- `source_external_id` (unique with provider for idempotency)
- `target_kind`, `target_id`, `proposed_action`, `applied_action`
- `confidence`, `rule_version`, `model`, `reasoning_effort`
- `state` (`proposed`, `approved`, `applied`, `rejected`, `reverted`, `failed`)
- `created_at`, `reviewed_at`, `applied_at`, `reversed_at`

Add indexes for user/profile/date, source idempotency, and target lookups. Keep the
existing `gpt_resume_content` as the current rendered content until migration and UI
readers are complete.

**Exit criteria:** migrations apply to an existing populated database without deleting
or rewriting unrelated rows; duplicate events cannot apply twice.

## Phase 4 — embedded job browser and adapters

1. Add the WebView2 package and a persistent user-data folder that is separate from app
   settings and secrets.
2. Add `IJobSiteAdapter` with:
   - `CanHandle(Uri)`;
   - `ExtractJobAsync`;
   - `GetCapabilities`;
   - `FillApplicationAsync`;
   - `ValidateBeforeFillAsync`.
3. Create adapters in a dedicated `JobSites` folder, one class per supported site.
4. Begin with one target site and these capabilities only: extract JD, create a draft,
   and fill fields after user confirmation.
5. Keep a selector health report and adapter version in the activity log so UI changes
   on a job site are diagnosable.

Use script messages with explicit schemas between WebView2 and C#. Never interpolate
untrusted JD or user data directly into JavaScript strings.

**Exit criteria:** one signed-in job site can extract a JD and fill a reviewed application
without the Chrome extension.

## Phase 5 — Gmail and Calendar integration

Use the app's own Google OAuth authorization, stored in Credential Manager. A ChatGPT
Google connection is useful for interactive ChatGPT work but is not an API credential
for DevStrider.

Start in review-only mode:

1. Ingest an email/event with provider message/event ID.
2. Classify it into a limited action taxonomy: bid rejected, interview invitation,
   interview changed, interview cancelled, request for availability, or no action.
3. Match it to a bid/interview using multiple signals: recipient profile email, sender
   domain, company, role, application ID, recruiter, and date.
4. Create an immutable `ds_automation_events` proposal.
5. Present the proposal in an Automation Review screen.
6. Apply only after user approval.

Automatic updates may be enabled only for explicit high-confidence rules, with an audit
record and a Revert action. Ambiguous emails must never mark a bid rejected or schedule
a calendar event automatically.

**Exit criteria:** duplicate emails are idempotent, every update can be traced to its
source, and users can review/revert changes.

## Phase 6 — controlled automation and rollout

1. Feature-flag Resume Studio by account/profile.
2. Pilot direct generation with a five-resume session limit and manual macro execution.
   Compare cached versus uncached token usage and generated-resume quality.
3. Enable the automatic-macro toggle for test profiles.
4. Let users configure the session limit after the default has been validated; retain
   the initial 1-10 bound until cost, latency, and quality data supports a change.
5. Pilot one job-site adapter with fill-only behavior.
6. Enable mail/calendar suggestions.
7. Consider automatic status changes only after measured matching accuracy and a clear
   rollback workflow.
8. Consider per-site automatic submission only after a site-specific policy and
   reliability review; it is not implied by the macro toggle.

## Testing and acceptance criteria

- Unit tests: response validation, structured-to-macro rendering, fast-feed compatibility,
  credential redaction, generation-session expiry/limits, cache-key isolation, status
  transitions, event idempotency, and matching confidence.
- Integration tests: API cancellation/retry, Word macro failure handling, and migration on
  a populated schema copy.
- WebView tests: adapter URL matching, extraction fixtures, missing selector fallbacks,
  and fill confirmation.
- Manual acceptance: generated resumes render correctly through the existing Word macro;
  five successive JDs reuse the correct profile-prompt cache without cross-job content;
  no generated draft becomes `applied` without confirmation; no automatic event changes
  a record without an audit entry.

## Explicit non-goals for the first release

- Automating the ChatGPT web UI.
- Storing OpenAI or Google secrets in the database or `settings.json`.
- Automatic job-site submission.
- Fully autonomous mailbox decisions.
- Replacing the existing Word macro or profile prompt format before Resume Studio is stable.
