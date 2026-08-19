# DevStrider Desktop

Windows desktop app (.NET 10 / WPF) for tracking job **bids** and **interviews**, generating
tailored **resumes** through ChatGPT and Word, and giving a team one shared view of the day.

- **Desktop app version:** 8.2.0
- **Chrome extension version:** 3.5.0 (the "Bid Assistant")
- **Platform:** Windows 10/11 only (uses Word automation, the system tray, and Win32 interop)

The repo root's [README](../README.md) is the project overview and setup guide. This file is the
reference for the desktop app itself.

---

## What it does

DevStrider is the hub of a three-part system:

```
 Chrome extension  ──HTTP(127.0.0.1:8765)──►  DevStrider desktop  ──►  PostgreSQL
 (job pages +                                  (WPF app + tray)         (the company
  ChatGPT tab)                                        │                  portal's)
                                                      └──►  Word, over COM
```

1. **Record bids** — on a job posting, the extension extracts the JD and captures the posting.
2. **Generate a tailored resume** — it sends the JD into a background ChatGPT tab, then runs your
   Word macro to produce the file and records the bid with the company / role / stacks parsed off
   the reply's fast-feed line.
3. **Track interviews** — schedule interviews off a bid, carrying the JD and resume id forward.
4. **See the team** — everyone reads and writes the same tables, so a teammate's bid shows up the
   moment they save it.

**There is no local database.** The shared PostgreSQL database is the only store, and it belongs to
the company portal — DevStrider adds four `ds_*` tables to it and reads the portal's `app_user`
for sign-in. It issues no DDL.

---

## Requirements

- Windows 10 or 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) to build (or the Desktop Runtime
  to run a framework-dependent build; the self-contained build bundles it)
- Access to the portal's **PostgreSQL** database, with `shared-db-schema.sql` already applied
- **Microsoft Word** (for the resume macro feature)
- **Google Chrome** + the Bid Assistant extension (in `../extension`)
- A **ChatGPT** account (free tier is fine) for resume generation

MongoDB is *not* required. If a machine still has the old local one, it is read once to carry that
install's saved settings across — see [Upgrading from 7.x](../README.md#upgrading-from-7x).

---

## Build & run

From `desktop/`:

```powershell
dotnet restore
dotnet run --project DevStrider.Desktop

# One-file executable (self-contained, ~150 MB, bundles the .NET runtime)
dotnet publish DevStrider.Desktop -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
# → DevStrider.Desktop\bin\Release\net10.0-windows\win-x64\publish\DevStrider.exe
```

> **Do not** add `-p:PublishTrimmed=true` — WPF's reflection breaks under the trimmer.

Launch opens the **sign-in window**. Nothing else is built until an account is established: every
repository scopes its queries to the signed-in `app_user.id`, and a query issued before login
throws rather than quietly reading the whole team's rows. On the first sign-in for an account,
DevStrider creates its `ds_users` row and seeds a profile named *Default*.

The title-bar pill shows the running version (e.g. `v8.2.0`) so you can confirm a fresh build was
picked up.

### Closing the app

The window's **X** hides to the system tray (the app keeps the HTTP listener alive). To fully quit,
right-click the tray icon → **Quit**. Quit force-terminates the process so it never lingers and
locks `DevStrider.exe` for the next build.

---

## Sign-in

Credentials are checked against the portal's `app_user`: email, `password_hash`, and
`email_verified`. DevStrider never creates an account, never sets a password, and has no sign-up or
reset — being a portal user is the only way to become a DevStrider user.

**The hash is scrypt, not bcrypt.** `password_hash` stores `<saltHex>:<keyHex>` — a 16-byte salt
and a 64-byte derived key, 161 characters in total, which is what Node's
`crypto.scryptSync(password, salt, 64)` produces. Verification uses BouncyCastle's scrypt at
Node's defaults (N=16384, r=8, p=1). Two of this app's columns are also the portal's types, not
ours: `app_user.id` is `integer` and `email_verified` is `integer`-as-flag, so both are read
through a widening coercion rather than `GetInt64`/`GetBoolean`, which throw on them.

- Your portal email **is** your identity here. It is what `ds_users.username` holds and what
  teammates see on the Peers tab; there is no separate name to pick.
- Every login re-asserts it, so a rename in the portal follows you here.
- A wrong address and a wrong password give the same message on purpose. Distinguishing them would
  tell anyone holding the database credential who has a portal account.
- Verification is checked *after* the password, for the same reason.
- There is no persisted session: the password is asked for on every start and nothing about it
  reaches disk.

The sign-in window also carries the **database connection** form. That looks like scope creep and
isn't — signing in *is* a database query, and the connection details otherwise live behind Settings,
which is behind the login. On a fresh install that circle has to be broken somewhere.

---

## The Chrome extension

Load `../extension` via `chrome://extensions` → Developer mode → **Load unpacked**.

One floating button on job pages. Clicking it scrapes the JD, sends it into a background ChatGPT
tab with the active profile's resume prompt, prewarms Word while ChatGPT writes, runs the macro
silently when the reply lands, and records the bid — without ever leaving the job page or
activating the ChatGPT tab. Ctrl+click uses text you selected by hand instead of the scraper.

The extension talks only to `http://127.0.0.1:8765`.

**Prompt contract** — ChatGPT's reply must end with these two lines, in order, or there is nothing
to parse:

```
[FolderName]: <output_filename>        ← your Word macro reads this
UID, Company, Role, Stack1, Stack2     ← MUST be the last line; DevStrider strips it for the bid
```

Without the last line the bid is still recorded, just bare. If the macro fails outright the bid is
still recorded — you lose the file, never the record.

---

## Tabs

### Bids
The day's bid board. Edit a row's status, schedule an interview off a bid, or bulk-select rows to
set status / delete in one go.

**Adding a bid by hand: paste the folder name.** The macro names its output folder with the
fast-feed line — `UID, Company, Role, Stack1, Stack2` — so that folder name *is* the bid. Paste it
into the box at the top and press Enter. Resume id, company, role and stacks are filled in and the
row lands as `applied`; there is no URL on it, because at that point the resume already exists and
the posting it came from is not what you are recording.

Anything that doesn't start with a short alphanumeric resume id is rejected rather than guessed at.
That rule is what stops a pasted sentence full of commas from being filed as a bid at company "QA".

Bids captured by the extension still arrive with their URL, and a posting captured but not yet bid
on is simply `status = draft`. The warning column flags three things: this exact URL captured
before, a different listing for the same company + role, and a company you already have an
interview at. Hand-added rows have no URL, so they take part in no URL dedup.

### Batched submission

Bids are not written to the database one at a time. They queue, and go up as a batch on whichever
comes first: **5 bids**, **1 hour**, the app exiting, or **Submit now**. A banner shows the count
whenever anything is waiting.

**The queue is on disk, not only in memory** — `%LOCALAPPDATA%\DevStrider\pending-bids-<id>.json`,
written before the caller is told the bid was recorded and cleared only once the rows are in
Postgres. That is not belt-and-braces: this process ends by calling `Process.Kill()` on itself
(see `App.OnExit`), so a buffer that lived only in a field would lose an hour of work to an
ordinary quit, with nothing to show it had ever existed. On the next launch the file is replayed
before the listener opens.

A failed batch keeps exactly the bids that didn't land and retries them on the next trigger, so a
database outage delays bids rather than losing them. Queued rows are merged over what the database
returns, so the board, the duplicate-URL check and the edit path all behave as though the write had
already happened.

The file is named per account. A shared machine must never flush one person's queued bids under the
next person's login — the repositories stamp the signed-in account onto every row, so that would
silently reassign their work.

### Interviews
Scheduled interviews in a date range, each carrying the source bid's resume id and JD. Schedule a
next-step interview off an existing one. Types: HR, Assessment, Phone Call, Tech 1–3, Client
Interview, Final Interview, Offer.

### Find bid
Search your bids by company / role / stack / URL / JD across a configurable window (default **last
60 days**).

### Overview / Stats
Aggregate counts and a bids-per-10-minute chart, for you and your teammates, over a date range.

### Peers
Read-only view of teammates' bids and interviews, read live from the same tables as your own —
there is no sync and nothing to wait for. Filter by date, then by teammate, then by one of their
profiles. A teammate's attached resume can be downloaded here: only the R2 object key travels
through the database, and the file is fetched with *your* credentials, so it works even if they are
offline.

### Profiles (Account)
Each **profile** is a distinct bidding identity (a real person). All workspace data — bids,
interviews — is scoped to its profile. Switch the active profile from the **title-bar dropdown**.

- Per profile: real name, contact details, **Word doc path** (.docm),
  **macro name**, **resume prompt**.
- The CV itself is in the .docm and nowhere else. DevStrider stores one line about it and never
  reads or renders the rest.

### Settings (Account)
- **Legacy MongoDB (import only)** — the old local database, read and never written to. Carries this
  machine's settings across automatically, and its profiles / postings / bids / interviews on demand
  via **Look for legacy data** → **Import**. Idempotent; see [Upgrading from 7.x](../README.md#upgrading-from-7x).
- **Identity** — read-only: the portal account you are signed in as
- **Shared database (PostgreSQL)** — service URI or host / port / database / user / password, with
  **Test connection** and **Clear password**. See [Credentials](#credentials).
- **Cloud storage (Cloudflare R2)** — account ID, bucket, access key ID, secret access key
- **Bid-Assistant listener** — port (default 8765) + status
- **Word macro hotkey** — shared fallback when a profile has no macro name

### Activity
Live log of every extension request, sign-in, and macro run (success / warning / error). Rows are
copyable (Ctrl+C with headers).

### About
Version, data locations, a how-to walkthrough, and the `DEVSTRIDER_*` environment-variable
reference.

---

## Local HTTP API (loopback only)

The extension drives the app through these endpoints on `http://127.0.0.1:8765`:

| Method | Path | Purpose |
|--------|------|---------|
| `GET`  | `/health`, `/` | Liveness check |
| `GET`  | `/active-profile` | The active profile's resume prompt |
| `POST` | `/prewarm` | Launch Word and open the template while ChatGPT is still writing |
| `POST` | `/generate-resume` | Run the macro and record the bid, in one call |
| `POST` | `/record-bid` | Record/update one bid without the macro (`/record-devstrider` is an alias) |
| `POST` | `/refresh-word` | Re-run the active profile's Word macro |
| `POST` | `/trigger-paste-submit` | Paste and submit into the ChatGPT tab |
| `GET`  | `/browse-word` | Native file picker for the .docm path |

The loopback binding is what stands in for authentication: nothing off this machine can reach it.
Requests therefore carry no credential and are served as whoever is signed in — which is why the
listener starts only **after** login.

`/refresh-word` is **serialized app-wide**: Word only ever has one instance of the .docm open, so
concurrent calls (multiple Chrome profiles/windows bidding at once) queue behind each other rather
than retriggering the macro mid-run. Each caller's Chrome window handle is captured *before* it
waits its turn, so focus returns to the window that actually clicked. Callers should allow up to
~90s and must not gate their own work on the response — the extension records the bid in parallel.

Capture is keyed on the strict-normalized URL: lowercased, trailing slash trimmed, query and hash
**kept**. Two tracking links to the same posting are two rows, deliberately — merging them would
hide that you bid the same job twice.

---

## Data model

Four tables, defined by [`shared-db-schema.sql`](shared-db-schema.sql) and created by hand.

| Table | Holds |
|-------|-------|
| `ds_users` | One row per account — the portal email, and nothing else worth storing |
| `ds_profiles` | Bidding identities |
| `ds_bids` | Job postings and the bids made against them — one row each |
| `ds_interviews` | Interviews |

`app_user` belongs to the portal. DevStrider only ever `SELECT`s from it.

**Four and not eight.** `ds_education`, `ds_certifications`, `ds_experiences` and
`ds_achievements` were dropped in 8.1.0. The first three held a CV that the profile's `.docm`
already held — and the .docm was the copy people actually edited, so the database's went stale the
day it was written. `ds_achievements` had no reader at all: the goal counters it fed lost their UI
with the web client and never got another one. Nothing of the CV survives in the database — a
`highest_education` column was added in 8.1.0 and dropped again in 8.2.0, on the
grounds that nothing read it either. Re-running `shared-db-schema.sql` drops all four tables.

Row ids are 24-character MongoDB ObjectId hex strings, carried over from the local databases these
tables replaced — keeping the original identity is what made the one-time import an idempotent
upsert. `user_id` is `app_user.id`, a BIGINT, and every query DevStrider issues filters on it.

[`shared-db-verify.sql`](shared-db-verify.sql) is the drift check — run it after any schema change,
or when the app reports SQLSTATE 42703.

---

## Configuration via environment variables

Empty/default settings are seeded once at first launch from `DEVSTRIDER_*` variables (set with
`setx`, then restart). See the **About** tab for the full list — e.g.
`DEVSTRIDER_SHARED_DB_URI`, `DEVSTRIDER_SHARED_DB_HOST`, `DEVSTRIDER_LISTENER_PORT`,
`DEVSTRIDER_WORD_DOC_PATH`, `DEVSTRIDER_WORD_HOTKEY`.

There is no username variable: the account name comes from `app_user`.

---

## Credentials

Every credential the app holds — the shared-database password and the Cloudflare R2 token — lives
in `%LOCALAPPDATA%\DevStrider\settings.json`, in cleartext. There is no second store: no registry,
no keychain, no encrypted file.

It is a file rather than a table because it holds the credentials needed to *reach* the database,
so reading it from the database would be circular. Writes go to a temp file and are then moved over
the target, so a crash mid-write leaves the previous settings intact rather than a half-written
file that fails to parse on next launch.

[`SettingsService`](DevStrider.Desktop/Services/SettingsService.cs) loads it **once at startup** and
serves every later read from memory. Before that, each of ~16 call sites re-queried the database —
`/refresh-word` hit it on every click just to read a hotkey.

Because reads share one instance, the rule is: `GetAsync()` returns the **cached object and must
not be mutated**; anything that edits settings takes `GetForEditAsync()` (a copy) and hands the
result to `SaveAsync`, which persists it and installs it as the new cache.

Consequence worth being explicit about: anything able to read that file — a backup, a synced
folder, another account on this machine — gets the database password *and* the R2 token. An R2
token with object-write permission can also delete objects, so any install holding it can empty the
bucket.

### The shared database

One database login, shared by every install. That is a deliberate trade for a small trusted team,
and it has consequences worth stating plainly:

- **Everything is visible to everyone.** These tables are not a stripped projection of something
  more private: `ds_bids.url`, `job_description`, `gpt_resume_content` and `comment` are the full
  values that used to stay on the author's machine.
- Every user can read *and delete* everyone else's rows — no row-level security is configured. The
  app scopes its own queries by `user_id`, but that is the app being well-behaved, not the database
  enforcing anything.
- One leaked password means rotating it for every installed client at once.
- Authorship is **identification, not authentication**: `user_id` is whoever was signed in to the
  app that wrote the row. Nothing downstream should treat it as proof.
- These tables are the only copy. Once a machine has been migrated off its local MongoDB, nothing
  else holds that person's bids and interviews.

Two ways to describe the same server, chosen by a radio button on the sign-in window and in
Settings:

1. **Service URI** — `postgresql://user:pass@host:5432/devstrider?sslmode=require`, what hosted
   providers hand you. `postgres://` is accepted too.
2. **Parts** — host, port, database, user, password, for anything self-hosted.

Whichever is selected is the one used; the other keeps what you typed, so switching back and forth
loses nothing. Both end up as one Npgsql connection string built by
[`SharedDbCredentials`](DevStrider.Desktop/Services/SharedDbCredentials.cs), which percent-decodes
the credentials out of a URI — generated Postgres passwords routinely contain `@`, `:`, `/` and
`?`, and arrive encoded.

**SSL** defaults to on (`Require`: encrypt, don't demand a chain the machine can verify — hosted
providers commonly present one it can't). Unchecking gives `Prefer`, so a local server without TLS
still works. An explicit `sslmode` in the URI overrides the checkbox.

Driver errors pass through `SharedDbCredentials.Redact` before reaching the Activity log, since a
service URI carries the password inline.

---

## Troubleshooting

| Symptom | Fix |
|---------|-----|
| `MSB3027: DevStrider.exe locked` on build | The app is still running — tray → **Quit** (or `taskkill /F /IM DevStrider.exe`). |
| "That email and password don't match an account" | Check the address against the portal. The message is deliberately the same for an unknown address and a wrong password. |
| "The database is reachable but has no app_user table" | The connection points at some database other than the portal's. |
| "these tables are missing: ds_…" | Run `shared-db-schema.sql` against that database — DevStrider does not create them. |
| "This account's email address hasn't been verified" | Confirm the address in the portal, then sign in here. |
| Shared database "unreachable / timeout" | Check host and port; allow this machine's IP in the provider's firewall; confirm the SSL setting matches what the server expects. |
| Every screen empty after signing in | No active profile — create one in the **Profiles** tab. |
| Resume batch does nothing | Keep a logged-in ChatGPT tab open; confirm the profile has a Word doc path + macro name; check the **Activity** tab. |
| Resume generates but no file | The Word macro must fill the bookmarks from the `[Section]:` labels and finish with `Application.Quit`. |
| ChatGPT automation stalls | ChatGPT changed its DOM — the injection/completion selectors in `extension/content.js` need updating. |

---

## Notes

- **One account per running app**, with multiple **profiles** (identities) under it. The password
  is asked for on every start.
- The database connection runs over TLS, but the database's own access control is the protection.
- Resume generation uses the **ChatGPT web session** (free tier), not an API — hence the
  keep-a-tab-open requirement and the inherent fragility to ChatGPT UI changes.

---

## History

DevStrider began as a multi-tenant web app (React + Express + Socket.IO + Atlas) and was rewritten
as this Windows desktop app. The team-sync layer moved from a shared **GitHub repo** of daily JSON
snapshots, to a shared **MongoDB/Atlas** cluster, to a shared **PostgreSQL** database in 5.0.0 —
each machine still keeping its real data in a local MongoDB and pushing stripped `peer_*` summaries
up on an hourly schedule.

**8.0.0 ended that.** There is one database now, shared with the company portal, and every machine
reads and writes it directly. The local MongoDB, the `peer_*` mirror, the sync scheduler, the
Sharing tab, and the web client and API server all went with it. Sign-in against the portal's
`app_user` arrived in the same release — with one database holding the whole team, "my rows" became
a predicate rather than a given, and that predicate needs an account behind it.

The standalone Python "ResumeAuto" tool was folded in as a batch **Resume auto-gen** tab, then
removed again in 4.0.0 — resume generation is the one-button extension flow only.
