# DevStrider Desktop

Windows desktop app (.NET 10 / WPF) for tracking job **bids** and **interviews**, generating
tailored **resumes** through ChatGPT and Word, and giving a team one shared view of the day.

- **Desktop app version:** 9.0.0
- **Chrome extension version:** 3.5.1 (the "Bid Assistant")
- **Platform:** Windows 10/11 only (uses Word automation, the system tray, and Win32 interop)

The repo root's [README](../README.md) is the project overview and setup guide. This file is the
reference for the desktop app itself.

---

## What it does

DevStrider is the hub of a three-part system:

```
 Chrome extension  ──HTTP(127.0.0.1:8765)──►  DevStrider desktop  ──HTTPS──►  hr-system
 (job pages +                                  (WPF app + tray)              (/api/devstrider/*,
  ChatGPT tab)                                        │                       PostgreSQL behind it)
                                                      └──►  Word, over COM
```

1. **Record bids** — on a job posting, the extension extracts the JD and captures the posting.
2. **Generate a tailored resume** — it sends the JD into a background ChatGPT tab, then runs your
   Word macro to produce the file and records the bid with the company / role / stacks parsed off
   the reply's fast-feed line.
3. **Track interviews** — schedule interviews off a bid, carrying the JD and resume id forward.
4. **See the team** — everyone reads and writes the same tables, so a teammate's bid shows up the
   moment they save it.

**There is no local database and no database credential on this machine.** DevStrider holds only a
bearer token. Every account and every `ds_*` row lives behind hr-system's `/api/devstrider/*` HTTP
API — see [`HrApiClient`](DevStrider.Desktop/Services/HrApi/HrApiClient.cs) — and hr-system is the
only thing that ever opens a Postgres connection.

---

## Requirements

- Windows 10 or 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) to build (or the Desktop Runtime
  to run a framework-dependent build; the self-contained build bundles it)
- Network access to an **hr-system** deployment (default `https://triospace.org/hr`) with the
  `shared-db-schema.sql` tables already applied to *its* database
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

Launch tries to restore a session from the bearer token saved on the last sign-in; only when there
is none (or it has expired) does the **sign-in window** appear. Nothing else is built until a
session exists either way: every HTTP repository call is rejected before it leaves the process if
no token is installed. On the first sign-in for an account, hr-system creates its `ds_users` row
and DevStrider seeds a profile named *Default*.

The title-bar pill shows the running version (e.g. `v9.0.0`) so you can confirm a fresh build was
picked up.

### Closing the app

The window's **X** hides to the system tray (the app keeps the HTTP listener alive). To fully quit,
right-click the tray icon → **Quit**. Quit force-terminates the process so it never lingers and
locks `DevStrider.exe` for the next build.

---

## Sign-in

DevStrider signs in against hr-system's `POST /api/devstrider/auth/login` — email and password over
HTTPS, nothing checked locally. hr-system never creates an account, never sets a password, and has
no sign-up or reset from this endpoint — being an hr-system user is the only way to become a
DevStrider user. The scrypt check, the `app_user.id`/`email_verified` column-type quirks, all of it
now happens once, in hr-system's own `lib/auth.js` — this app no longer re-implements any of it.

**The session is a week-long bearer token**, not a browser-style short one. Login returns a signed
JWT (hr-system's `lib/jwt.js`) alongside your identity; DevStrider saves it in `settings.json` and
sends it as `Authorization: Bearer <token>` on every `/api/devstrider/*` call after that. On launch
it is restored silently — `GET /api/devstrider/auth/session` re-validates it — and refreshed once it
is inside its last day, so someone who opens DevStrider daily is never asked for a password again.
Settings → hr-system → **Sign out** drops it early.

- Your hr-system email **is** your identity here. It is what `ds_users.username` holds and what
  teammates see on the Peers tab; there is no separate name to pick.
- Every login (and every silent restore) re-asserts it, so a rename in hr-system follows you here.
- A wrong address and a wrong password give the same message on purpose. Distinguishing them would
  tell anyone who can reach the login endpoint who has an account.
- Verification is checked *after* the password, for the same reason.

There is no more **database connection** form on the sign-in window — there is no database
connection left to configure on this machine. The only thing Settings needs for hr-system is the
server address, and it defaults to the production one.

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

### Recording a day from resume folders

**Bids → From folder…** is the back door: pick a profile, point at the directory the
macro wrote its resume folders into, and every folder named like a fast-feed line becomes a bid,
timed by when that folder was created.

Use it when the extension didn't record the work — the machine was offline, the app wasn't running,
the bidding happened somewhere else, or it predates the app. The folders on disk are a record that
outlives any database, which is why this replaced the one-time MongoDB migration rather than
sitting alongside it.

**Scan** first: it lists what it found, marking each folder recognised or skipped, and only then
does **Record bids** light up. Folders that aren't named like a fast-feed line are ignored, not
guessed at.

What these rows do *not* have is a job URL or a job description — a folder name carries the resume
id, company, role and stacks and nothing else. They therefore take no part in duplicate-URL
detection and the JD viewer has nothing to show for them. That is the accepted trade for recording
bids that would otherwise not exist at all.

Each bid is timed by **its own folder's creation time** — the moment the macro wrote it, which is
the moment the bid was made. That precision matters: the bids-per-10-minute chart buckets on
applied time, and one date for a whole batch would stack every bid into a single bar.

The timestamps are only as true as the folders, so the scan reports the range it found before you
commit: directories that were copied or restored carry the date of the copy, and a batch that all
reads "today" is the tell.

Re-running on the same folder is safe. These rows have no prior identity, so each one's id is
derived from the profile and the folder name — deliberately not the timestamp, so a folder whose
creation time shifted still matches its existing row. A second pass updates rather than doubles,
which matters because "did that work?" is exactly when someone clicks twice.

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
  machine's saved settings across automatically on first launch. It holds no bid history the app
  wants: there is no data migration, and none is planned — see [Recording a day from resume folders](#recording-a-day-from-resume-folders).
- **Identity** — read-only: the hr-system account you are signed in as
- **hr-system** — server address (default `https://triospace.org/hr`) and **Sign out**. See
  [Credentials](#credentials).
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
listener starts only **after** a session exists, restored silently or via the login window.

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

Four tables, defined by [`shared-db-schema.sql`](shared-db-schema.sql) — applied once, by hand,
against **hr-system's** database. DevStrider itself no longer opens a connection to run that file
or anything else against it; every read and write goes through hr-system's `/api/devstrider/*` API
(see [`Data/Http`](DevStrider.Desktop/Data/Http)), which is the only thing on the other end that
still speaks SQL to these tables.

| Table | Holds |
|-------|-------|
| `ds_users` | One row per account — the hr-system email, and nothing else worth storing |
| `ds_profiles` | Bidding identities |
| `ds_bids` | Job postings and the bids made against them — one row each |
| `ds_interviews` | Interviews |

`app_user` belongs to hr-system. DevStrider never reads or writes it directly — it only ever sees
what `/api/devstrider/auth/*` hands back about the signed-in account.

**Four and not eight.** `ds_education`, `ds_certifications`, `ds_experiences` and
`ds_achievements` were dropped in 8.1.0. The first three held a CV that the profile's `.docm`
already held — and the .docm was the copy people actually edited, so the database's went stale the
day it was written. `ds_achievements` had no reader at all: the goal counters it fed lost their UI
with the web client and never got another one. Nothing of the CV survives in the database — a
`highest_education` column was added in 8.1.0 and dropped again in 8.2.0, on the
grounds that nothing read it either. Re-running `shared-db-schema.sql` drops all four tables.

Row ids are 24-character MongoDB ObjectId hex strings, carried over from the local databases these
tables replaced — keeping the original identity is what made the one-time import an idempotent
upsert. `user_id` is `app_user.id`, a BIGINT, and hr-system scopes every `/api/devstrider/*` query
to whichever account the bearer token belongs to — DevStrider never puts a user id on the wire
itself.

[`shared-db-verify.sql`](shared-db-verify.sql) is the drift check — run it against hr-system's
database after any schema change, or when hr-system's own logs show a missing-column error on one
of these tables.

---

## Configuration via environment variables

Empty/default settings are seeded once at first launch from `DEVSTRIDER_*` variables (set with
`setx`, then restart). See the **About** tab for the full list — e.g.
`DEVSTRIDER_HR_API_BASE_URL`, `DEVSTRIDER_LISTENER_PORT`, `DEVSTRIDER_WORD_DOC_PATH`,
`DEVSTRIDER_WORD_HOTKEY`.

There is no username variable: the account name comes from hr-system's `app_user`.

---

## Credentials

Every credential the app holds — the hr-system bearer token and the Cloudflare R2 token — lives in
`%LOCALAPPDATA%\DevStrider\settings.json`, in cleartext. There is no second store: no registry, no
keychain, no encrypted file. There is also no database password on this machine any more: DevStrider
holds nothing that can reach Postgres directly, only a token hr-system will honour for up to a week.

Writes go to a temp file and are then moved over the target, so a crash mid-write leaves the
previous settings intact rather than a half-written file that fails to parse on next launch.

[`SettingsService`](DevStrider.Desktop/Services/SettingsService.cs) loads it **once at startup** and
serves every later read from memory. Before that, each of ~16 call sites re-queried the database —
`/refresh-word` hit it on every click just to read a hotkey.

Because reads share one instance, the rule is: `GetAsync()` returns the **cached object and must
not be mutated**; anything that edits settings takes `GetForEditAsync()` (a copy) and hands the
result to `SaveAsync`, which persists it and installs it as the new cache.

Consequence worth being explicit about: anything able to read that file — a backup, a synced
folder, another account on this machine — gets the bearer token *and* the R2 token. The bearer token
is good for whatever it was signed for (up to a week, refreshed on use) and nothing longer; Settings
→ hr-system → **Sign out** revokes it locally, though hr-system's stateless tokens mean it stays
technically valid server-side until it expires on its own. An R2 token with object-write permission
can also delete objects, so any install holding it can empty the bucket.

### hr-system's API

DevStrider no longer opens a database connection itself — every read and write is one HTTP call to
hr-system's `/api/devstrider/*` API (see
[`HrApiClient`](DevStrider.Desktop/Services/HrApi/HrApiClient.cs) and
[`Data/Http`](DevStrider.Desktop/Data/Http)), authenticated with the bearer token above. That moves
where the trust boundary sits, but not what is behind it — hr-system's own database still holds the
same tables with the same properties, worth stating plainly:

- **Everything is visible to everyone on the team.** These tables are not a stripped projection of
  something more private: `ds_bids.url`, `job_description`, `gpt_resume_content` and `comment` are
  the full values that used to stay on the author's machine.
- Authorship is **identification, not authentication**: `user_id` is whoever the bearer token
  belonged to when the row was written. Nothing downstream should treat it as proof.
- These tables are the only copy. Once a machine has been migrated off its local MongoDB, nothing
  else holds that person's bids and interviews.
- What changed from the direct-Postgres era: a caller here can no longer even construct a request
  naming another account's `user_id` — hr-system reads it off the token's signature, never off
  anything DevStrider sends. The row-level trust question moved from "does the app behave" to "does
  hr-system's routing," which is hr-system's concern now, not this file's.

Settings' **server address** field (default `https://triospace.org/hr`) is the only address
DevStrider needs to know. There is no URI/host-port split any more — that distinction was about
describing a Postgres connection, and there is not one left here to describe.

---

## Troubleshooting

| Symptom | Fix |
|---------|-----|
| `MSB3027: DevStrider.exe locked` on build | The app is still running — tray → **Quit** (or `taskkill /F /IM DevStrider.exe`). |
| "That email and password don't match an account" | Check the address against hr-system. The message is deliberately the same for an unknown address and a wrong password. |
| "No hr-system server address is set" | Fill in Settings → hr-system → **Server address** and save. |
| "Couldn't reach hr-system" | Check the server address, and that the machine can reach it — a corporate VPN or firewall is the usual cause. |
| "This account's email address hasn't been verified" | Confirm the address in hr-system, then sign in here. |
| Signed out unexpectedly / asked to sign in again | The saved bearer token expired, was revoked (a **Sign out**, a server key rotation, or the account being deleted), or hr-system rejected it — sign in again. |
| Every screen empty after signing in | No active profile — create one in the **Profiles** tab. |
| Resume batch does nothing | Keep a logged-in ChatGPT tab open; confirm the profile has a Word doc path + macro name; check the **Activity** tab. |
| Resume generates but no file | The Word macro must fill the bookmarks from the `[Section]:` labels and finish with `Application.Quit`. |
| `Macro call failed: …` after upgrading | DevStrider now calls the macro with two arguments (resume text, job description) — a template's macro still declared with one fails every run. See [`macro.md`](macro.md). |
| Bid hangs after the resume is written | The reply finished but the extension never sent it. Reload the ChatGPT tab (a tab opened before the extension was loaded has no content script), then check `isStreaming()` in `extension/content.js` against the live composer. |
| ChatGPT automation stalls | ChatGPT changed its DOM — the injection/completion selectors in `extension/content.js` need updating. |

---

## Notes

- **One account per running app**, with multiple **profiles** (identities) under it. The password is
  asked for once; the week-long bearer token is what carries the session across later launches.
- The hr-system connection runs over HTTPS, but hr-system's own access control is the protection —
  DevStrider has nothing left that reaches Postgres, so there is no database credential on this
  machine to protect in the first place.
- Resume generation uses the **ChatGPT web session** (free tier), not an API — hence the
  keep-a-tab-open requirement and the inherent fragility to ChatGPT UI changes.

---

## History

DevStrider began as a multi-tenant web app (React + Express + Socket.IO + Atlas) and was rewritten
as this Windows desktop app. The team-sync layer moved from a shared **GitHub repo** of daily JSON
snapshots, to a shared **MongoDB/Atlas** cluster, to a shared **PostgreSQL** database in 5.0.0 —
each machine still keeping its real data in a local MongoDB and pushing stripped `peer_*` summaries
up on an hourly schedule.

**8.0.0 ended that.** There was one database, shared with the company portal, and every machine read
and wrote it directly. The local MongoDB, the `peer_*` mirror, the sync scheduler, the Sharing tab,
and the web client and API server all went with it. Sign-in against the portal's `app_user` arrived
in the same release — with one database holding the whole team, "my rows" became a predicate rather
than a given, and that predicate needs an account behind it.

**9.0.0 went one step further and removed the direct connection too.** DevStrider no longer holds a
Postgres credential at all: hr-system grew an `/api/devstrider/*` HTTP API built for exactly this,
and every account read and `ds_*` read/write now goes through it on a week-long bearer token instead
of a connection string sitting in `settings.json`. The sign-in window's database-connection form is
gone with it. The resume macro also picked up a second parameter in this release, so it can save the
job description as a text file alongside the resume it writes — see [`macro.md`](macro.md).

The standalone Python "ResumeAuto" tool was folded in as a batch **Resume auto-gen** tab, then
removed again in 4.0.0 — resume generation is the one-button extension flow only.
