# DevStrider Desktop

Windows desktop app (.NET 10 / WPF) for tracking job **bids** and **interviews**, generating
tailored **resumes** through ChatGPT and Word, and giving a team one shared view of the day.

- **Desktop app version:** 10.1.0
- **Platform:** Windows 10/11 only (uses Word automation, the system tray, and Win32 interop)

The repo root's [README](../README.md) is the project overview and setup guide. This file is the
reference for the desktop app itself.

---

## What it does

DevStrider is the hub of a three-part system:

```
 Chrome extension  ──HTTP(127.0.0.1:8765)──►  DevStrider desktop  ──HTTPS──►  the company
 (job pages +                                  (WPF app + tray)     bearer      portal
  ChatGPT tab)                                        │             token     (owns the DB)
                                                      └──►  Word, over COM
```

1. **Record bids** — on a job posting, the extension extracts the JD and captures the posting.
2. **Generate a tailored resume** — it sends the JD into a background ChatGPT tab, then runs your
   Word macro to produce the file and records the bid with the company / role / stacks parsed off
   the reply's fast-feed line.
3. **Track interviews** — schedule interviews off a bid, carrying the JD and resume id forward.
4. **See the team** — everyone reads and writes the same tables, so a teammate's bid shows up the
   moment they save it.

**There is no local database, and since 10.0 no database connection either.** The company portal is
the only store and the only way in: this app signs in at `/api/devstrider/auth/login`, holds a
week-long bearer token, and reads and writes through `/api/devstrider/*`. It contains no SQL, no
database driver, and no database credential — the portal owns the `ds_*` tables and migrates them
itself.

---

## Requirements

- Windows 10 or 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) to build (or the Desktop Runtime
  to run a framework-dependent build; the self-contained build bundles it)
- The portal's **address** — a URL, not a credential
- **Microsoft Word** (for the resume macro feature)
- **Google Chrome** + the Bid Assistant extension (in `../extension`)
- A **ChatGPT** account (free tier is fine) for resume generation

MongoDB is not used at all — the driver and the last import that read it went in 9.3.0.

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

The title-bar pill shows the running version (e.g. `v10.1.0`) so you can confirm a fresh build was
picked up.

### Closing the app

The window's **X** hides to the system tray (the app keeps the HTTP listener alive). To fully quit,
right-click the tray icon → **Quit**. Quit force-terminates the process so it never lingers and
locks `DevStrider.exe` for the next build.

---

## Sign-in

**This app does not check passwords.** The email and password go to
`POST /api/devstrider/auth/login` and the portal answers with a token. DevStrider never creates an
account, never sets a password, and has no sign-up or reset — being a portal user is the only way
to become a DevStrider user.

Until 10.0 it did check them, here, in C#: it read `app_user.password_hash` off a direct database
connection and re-derived the portal's scrypt with BouncyCastle at Node's defaults, including a
guess at which of two readings of the salt the portal had meant. That is one authentication rule
implemented twice, in two languages, shipped to every laptop, with nothing to notice when the two
drifted — and a database credential sitting beside it to make the read possible. Both are gone. The
one place that can decide whether a password is right is the one that owns the account.

- Your portal email **is** your identity here. It is what `ds_users.username` holds and what
  teammates see on the Peers tab; there is no separate name to pick.
- Every sign-in re-asserts it, so a rename in the portal follows you here.
- A wrong address and a wrong password give the same message — now because the portal answers both
  the same way, rather than because this app chose not to look.
- Verification is checked *after* the password, for the same reason.

### The week

The token is good for **seven days** and is kept in `%LOCALAPPDATA%\DevStrider\session.dat`,
encrypted with DPAPI under your Windows account — it does not decrypt on another machine, or for
another Windows user on this one. On launch the app puts the session back and asks the portal
whether it is still valid; if the portal cannot be reached, the session is used anyway and the
first real call will say so, because being on a train is not the same as being signed out. Inside
the last day it trades the token for a fresh week, at startup and again every six hours for a
window nobody ever closes.

So there is a persisted session now, where there deliberately wasn't one before. The reason there
wasn't is worth stating: the only thing that could have been persisted was a database password with
rights over everyone's data, and asking for it on every start was the lesser evil. What is kept
instead is scoped to DevStrider, expires on its own, is re-checked at every launch, and can be
revoked server-side — changing `DEVSTRIDER_JWT_SECRET` on the portal invalidates every outstanding
token at once. Settings → **Sign out on this machine** deletes the local copy.

The sign-in window also carries the **portal address** field. That looks like scope creep and
isn't — signing in *is* a call to the portal, and the address otherwise lives behind Settings,
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
- **Identity** — read-only: the portal account you are signed in as
- **Company portal** — the address, with **Test connection**, plus how long this machine's session
  has left and **Sign out on this machine**. See [Credentials](#credentials).
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

Five tables, owned and migrated by the portal
(`hr-system/migrations/postgres/011_devstrider_api.sql`). This app never sees them: it sees
`/api/devstrider/*`, whose payloads are these rows in camelCase.

| Table | Holds |
|-------|-------|
| `ds_users` | One row per account — the portal email, and nothing else worth storing |
| `ds_profiles` | Bidding identities |
| `ds_bids` | Job postings and the bids made against them — one row each |
| `ds_interviews` | Interviews |
| `ds_person_facts` | Education, career history and custom fields — what ChatGPT is told |

`app_user` belongs to the portal, and since 10.0 so do these.

**Four and not eight.** `ds_education`, `ds_certifications`, `ds_experiences` and
`ds_achievements` were dropped in 8.1.0. The first three held a CV that the profile's `.docm`
already held — and the .docm was the copy people actually edited, so the database's went stale the
day it was written. `ds_achievements` had no reader at all: the goal counters it fed lost their UI
with the web client and never got another one. Nothing of the CV survives in the database — a
`highest_education` column was added in 8.1.0 and dropped again in 8.2.0, on the
grounds that nothing read it either.

Row ids are 24-character hex strings in the MongoDB ObjectId format, carried over from the local databases these
tables replaced — keeping the original identity is what made the one-time import an idempotent
upsert. `user_id` is `app_user.id`, a BIGINT, and every query DevStrider issues filters on it.

[`shared-db-schema.sql`](shared-db-schema.sql) and [`shared-db-verify.sql`](shared-db-verify.sql)
are the retired hand-run schema and its drift check, kept as the historical record. **Do not run
the schema file** — its `DROP`s are still live and these tables are still the only copy.

---

## Configuration via environment variables

Empty/default settings are seeded once at first launch from `DEVSTRIDER_*` variables (set with
`setx`, then restart). See the **About** tab for the full list — e.g.
`DEVSTRIDER_PORTAL_URL`, `DEVSTRIDER_LISTENER_PORT`, `DEVSTRIDER_WORD_DOC_PATH`,
`DEVSTRIDER_WORD_HOTKEY`.

The six `DEVSTRIDER_SHARED_DB_*` variables went with the direct database connection, one of them a
password. `DEVSTRIDER_PORTAL_URL` replaces all of them and is not a secret, which is what makes
provisioning a machine something you can put in a script.

There is no username variable: the account name comes from `app_user`.

---

## Credentials

`%LOCALAPPDATA%\DevStrider\settings.json` holds **no database credential any more**. It used to
hold the shared PostgreSQL password in cleartext — one login, shared by the whole team, with rights
over everyone's data, on every laptop — and that went with the direct connection in 10.0. What is
left there is the portal's address, a listener port, Word paths, and the Cloudflare R2 token, which
is still in cleartext.

Beside it sits `session.dat`: the week-long bearer token, encrypted with DPAPI under the Windows
account that wrote it, so it does not decrypt on another machine or for another user here. It is a
separate file on purpose — settings describe this machine and are worth reading in an editor, this
is a credential, and signing out has to be able to delete it without taking a listener port and a
Word path with it.

Settings are a file rather than a row because they say how to *reach* the store, so reading them
from the store would be circular. Writes go to a temp file and are then moved over the target, so a
crash mid-write leaves the previous file intact rather than a half-written one that fails to parse
on next launch. `session.dat` is written the same way, for the same reason: a truncated token costs
a sign-in.

[`SettingsService`](DevStrider.Desktop/Services/SettingsService.cs) loads settings **once at
startup** and serves every later read from memory. Before that, each of ~16 call sites re-queried
the store — `/refresh-word` hit it on every click just to read a hotkey.

Because reads share one instance, the rule is: `GetAsync()` returns the **cached object and must
not be mutated**; anything that edits settings takes `GetForEditAsync()` (a copy) and hands the
result to `SaveAsync`, which persists it and installs it as the new cache.

Consequence worth being explicit about: anything able to read `settings.json` — a backup, a synced
folder, another account on this machine — gets the R2 token. A token with object-write permission
can also delete objects, so any install holding it can empty the bucket. That used to be true of
the database password too, and is the single largest thing 10.0 removed.

### What the token can and can't do

The portal takes the account off the signature on every request and pins every write to it, so a
token is authority over **your own rows and nothing else**. What it does not narrow is reading:

- **Everything stored is visible to everyone on the team.** These tables are not a stripped
  projection of something more private — `ds_bids.url`, `job_description`, `gpt_resume_content` and
  `comment` are the full values that used to stay on the author's machine, and
  `/api/devstrider/peers/*` returns them. That is the Peers tab working as intended.
- What changed is writes. Under the old direct connection every install could read *and delete*
  everyone else's rows; the app scoped its own SQL by `user_id`, but that was the app being
  well-behaved, not anything enforcing it. There is now no peer write route at all, and no request
  can name a user id.
- **Revocation is possible now.** A leaked token expires within a week on its own, and changing
  `DEVSTRIDER_JWT_SECRET` on the portal invalidates every outstanding token at once. A leaked
  database password meant rotating it on every installed client by hand.
- Authorship is still **identification, not authentication**: `user_id` is whoever was signed in
  when the row was written. Nothing downstream should treat it as proof.
- These tables are still the only copy of anyone's bids and interviews.

### The portal address

One field, on the sign-in window and in Settings.
[`PortalApi.ParseBaseUrl`](DevStrider.Desktop/Services/PortalApi.cs) normalises it: a bare host gets
`https://` (defaulting to plain HTTP would put a password on the wire), a trailing slash is
trimmed, and a trailing `/api` is stripped because that is what people paste when they have seen an
endpoint rather than the site.

It is kept as a **string** and never as a `Uri`. `new Uri("https://host").ToString()` hands back
`https://host/`, so joining it to a path that starts with `/` produced `https://host//api/me` —
which is a different number of path segments, matches no route, falls through to the portal's
static handler, and redirects. Every call in 10.0's first build came back 302. Keeping the joined
form as text is what makes that unrepresentable.

Errors pass through [`Safe.Redact`](DevStrider.Desktop/Services/Safe.cs) before reaching the
Activity log, which strips bearer tokens and any credentials inline in a URL.

---

## Troubleshooting

| Symptom | Fix |
|---------|-----|
| `MSB3027: DevStrider.exe locked` on build | The app is still running — tray → **Quit** (or `taskkill /F /IM DevStrider.exe`). |
| "That email and password don't match an account" | Check the address against the portal. The message is deliberately the same for an unknown address and a wrong password. |
| "The portal has no such endpoint" | The address points at a portal build without `/api/devstrider/*` — deploy the server side of 10.0 first. |
| "answered with something that isn't JSON" | The address lands on a proxy or a login page in front of the portal, not the portal. |
| "This account's email address hasn't been verified" | Confirm the address in the portal, then sign in here. |
| "Your DevStrider session has expired" | The week ran out, or the portal's `DEVSTRIDER_JWT_SECRET` was changed. Sign in again. |
| "didn't answer in time" / "Couldn't reach the portal" | Check the address; confirm the portal is up and reachable from this machine (open it in a browser). |
| Asked for a password on every launch | `session.dat` isn't being written — DPAPI needs a loaded user profile, so a service account or a sandboxed session won't keep one. Check the Activity tab. |
| Every screen empty after signing in | No active profile — create one in the **Profiles** tab. |
| Resume batch does nothing | Keep a logged-in ChatGPT tab open; confirm the profile has a Word doc path + macro name; check the **Activity** tab. |
| Resume generates but no file | The Word macro must fill the bookmarks from the `[Section]:` labels and finish with `Application.Quit`. |
| ChatGPT automation stalls | ChatGPT changed its DOM — the send-button and stop-button selectors in `Views/ResumeStudioView.xaml.cs` need updating against the live composer. |

---

## Notes

- **One account per running app**, with multiple **profiles** (identities) under it. The password is
  asked for about once a week — see [The week](#the-week).
- Requests run over TLS, and the portal's own authorization is the protection: it takes the account
  off the token's signature and pins every write to it.
- Resume generation uses the **ChatGPT web session** (free tier), not an API — hence the
  keep-a-tab-open requirement and the inherent fragility to ChatGPT UI changes.

---

## History

DevStrider began as a multi-tenant web app (React + Express + Socket.IO + Atlas) and was rewritten
as this Windows desktop app. The team-sync layer moved from a shared **GitHub repo** of daily JSON
snapshots, to a shared **MongoDB/Atlas** cluster, to a shared **PostgreSQL** database in 5.0.0 —
each machine still keeping its real data in a local MongoDB and pushing stripped `peer_*` summaries
up on an hourly schedule.

**8.0.0 ended that.** There was one database from then on, shared with the company portal, and every
machine read and wrote it directly. The local MongoDB, the `peer_*` mirror, the sync scheduler, the
Sharing tab, and the web client and API server all went with it. Sign-in against the portal's
`app_user` arrived in the same release — with one database holding the whole team, "my rows" became
a predicate rather than a given, and that predicate needs an account behind it.

**10.0.0 finished the job it started.** 8.0 was right that there should be one store and no mirror;
what it left in place was every laptop holding the database password and checking passwords for
itself. The store is still one, but the app reaches it the way anything else would — over HTTP,
with a token, through a server that owns the schema and decides who anyone is. So the API server
8.0 deleted is back, in the sense that matters: it is the portal, it was always there, and it was
the thing this app should have been talking to all along.

The standalone Python "ResumeAuto" tool was folded in as a batch **Resume auto-gen** tab, then
removed again in 4.0.0 — resume generation is the one-button extension flow only.
