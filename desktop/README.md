# DevStrider Desktop

A local-first, Windows desktop app (.NET 8 / WPF) for tracking job **bids** and **interviews**,
auto-generating tailored **resumes** through ChatGPT, and sharing daily status with a team via a
shared PostgreSQL database.

- **Desktop app version:** 7.1.0
- **Chrome extension version:** 3.3.0 (the "Bid Assistant")
- **Platform:** Windows 10/11 only (uses Word automation, the system tray, and Win32 interop)

---

## What it does

DevStrider is the local hub of a three-part system:

```
 Chrome extension  ──HTTP(127.0.0.1:8765)──►  DevStrider desktop  ──►  Local MongoDB
 (job pages +                                  (WPF app + tray)         (your bids/interviews)
  ChatGPT tab)                                        │
                                                      └──►  Shared PostgreSQL (peers)
```

1. **Record bids** — browse a job posting, the extension extracts the JD and records a bid in the
   local database.
2. **Generate a tailored resume** — the extension sends the JD into ChatGPT, then one click runs
   your Word macro to produce the resume file and records the bid with the company/role/stacks
   parsed off the reply's fast-feed line.
3. **Track interviews** — schedule interviews off a bid, carrying the JD + resume forward.
4. **Share with a team** — push your bid/interview summaries to a shared PostgreSQL database and pull peers'
   so everyone can see daily activity.

---

## Requirements

- Windows 10 or 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build (or the Desktop Runtime to
  run a framework-dependent build; the self-contained build bundles it)
- **MongoDB running locally** at `mongodb://127.0.0.1:27017` (the Community MSI registers a Windows
  service named `MongoDB` on the default port)
- **Microsoft Word** (for the resume macro feature)
- **Google Chrome** + the Bid Assistant extension (in `../extension`)
- A **ChatGPT** account (free tier is fine) for resume generation

---

## Build & run

From `desktop/`:

```powershell
dotnet restore
dotnet run --project DevStrider.Desktop

# One-file executable (self-contained, ~150 MB, bundles the .NET runtime)
dotnet publish DevStrider.Desktop -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
# → DevStrider.Desktop\bin\Release\net8.0-windows\win-x64\publish\DevStrider.exe
```

> **Do not** add `-p:PublishTrimmed=true` — WPF's reflection breaks under the trimmer.

On first launch DevStrider creates the `devstrider` database, seeds a default profile + settings,
and runs the multi-profile migration. The title-bar pill shows the running version (e.g. `v7.1.0`)
so you can confirm a fresh build was picked up.

### Closing the app

The window's **X** hides to the system tray (the app keeps the HTTP listener alive). To fully quit,
right-click the tray icon → **Quit**. (Quit force-terminates the process so it never lingers and
locks `DevStrider.exe` for the next build.)

---

## The Chrome extension

Load `../extension` via `chrome://extensions` → Developer mode → **Load unpacked**.

It exposes two floating buttons:

- **Blue** (job pages) — sends the page's JD into your ChatGPT tab via DOM injection (no clipboard).
- **Purple** (ChatGPT) — runs the Word macro and records the bid from the last ChatGPT reply.

The extension talks only to `http://127.0.0.1:8765` (loopback, no auth). Purple runs the Word
refresh and the bid record **in parallel** — a slow or failed Word step never blocks the bid.

**Prompt contract** — ChatGPT's reply must end with these two lines, in order, or the purple
button has nothing to parse:

```
[FolderName]: <output_filename>        ← your Word macro reads this
UID, Company, Role, Stack1, Stack2     ← MUST be the last line; DevStrider strips it for the bid
```

---

## Tabs

### Bids
The day's bid board. Add a link, apply a fast-feed line (`UID, Company, Role, Stack1, …`), edit
status, schedule an interview, or bulk-select rows to set status / delete in one go.

### Interviews
Scheduled interviews in a date range, each carrying the source bid's resume ID + JD. Schedule a
next-step interview off an existing one. Types: HR, Assessment, Phone Call, Tech 1–3, Client
Interview, Final Interview, Offer.

### Find bid
Search your bids by company / role / stack / URL across a configurable window (default **last 60
days**, up to all-time).

### Overview / Stats
Aggregate counts and a bids-per-10-minute chart, for you and any synced peers, over a date range.

### Peers
Read-only view of peers' bids and interviews pulled from the shared cluster (company / role /
status / stacks / dates / job description — URLs, resume text and comments are not shared). Filter by date + owner.

### Profiles (Account)
Each **profile** is a distinct bidding identity (a real person). All workspace data — links, bids,
interviews — is scoped to its profile. Switch the active profile from the **title-bar dropdown**.

- Per profile: real name, **Word doc path** (.docm), **macro name**, **resume prompt**.
- Shared across profiles: Mongo URI, listener port, username, Word hotkey, shared-cluster connection.
- Import old config with **Import from ResumeAuto (profiles.json)**.

### Sharing (Account)
- **Sync now** — two-way delta sync with the shared cluster (push your updated bids/interviews,
  pull peers').
- **Import from legacy database** — one-time pull of your data from the old web-app collections
  (`users` / `userbids` / `interviews` / …) into local profiles, by email.
- **Reset shared database** — list + drop leftover collections in the shared cluster.

### Settings (Account)
- **MongoDB connection** (local)
- **Identity** — your username (filename prefix in the shared cluster)
- **Peer database** — shared cluster host / username / options / DB name, with **Test connection**
  and **Clear password**. See [Credentials](#credentials).
- **Cloud storage (Cloudflare R2)** — account ID, bucket, access key ID, secret access key
- **Bid-Assistant listener** — port (default 8765) + status
- **Word macro hotkey** — shared fallback when a profile has no macro name

### Activity
Live log of every extension request, sync, and macro run (success / warning / error). Rows are
copyable (Ctrl+C with headers).

### About
Version, data locations, and the `DEVSTRIDER_*` environment-variable reference.

---

## Local HTTP API (loopback only)

The extension drives the app through these endpoints on `http://127.0.0.1:8765`:

| Method | Path | Purpose |
|--------|------|---------|
| `GET`  | `/health` | Liveness check |
| `POST` | `/record-bid` | Record/update one bid (manual flow) |
| `POST` | `/refresh-word` | Run the active profile's Word macro (hotkey path) |
| `GET`  | `/browse-word` | Native file picker for the .docm path |

`/refresh-word` is **serialized app-wide**: Word only ever has one instance of the .docm open, so
concurrent calls (multiple Chrome profiles/windows bidding at once) queue behind each other rather
than retriggering the macro mid-run. Each caller's Chrome window handle is captured *before* it
waits its turn, so focus returns to the window that actually clicked. Callers should allow up to
~90s and must not gate their own work on the response — the extension records the bid in parallel.

---

## Data model (local MongoDB, db `devstrider`)

| Collection | Holds |
|------------|-------|
| `bidProfiles` | Bidding identities (Profile) |
| `links` | Job-posting URLs (GroupLink), profile-scoped |
| `bids` | Your bids (UserBid), profile-scoped |
| `interviews` | Interviews, profile-scoped |
| `peerBids` / `peerInterviews` | Local mirror of peers' shared data |
| `settings` / `profiles` | App settings (incl. all credentials) + the username singleton |

The shared cluster holds only `peerBids` + `peerInterviews`.

---

## Configuration via environment variables

Empty/default settings are seeded once at first launch from `DEVSTRIDER_*` variables (set with
`setx`, then restart). See the **About** tab for the full list — e.g.
`DEVSTRIDER_SHARED_MONGO_HOST`, `DEVSTRIDER_USERNAME`, `DEVSTRIDER_LISTENER_PORT`,
`DEVSTRIDER_WORD_DOC_PATH`, `DEVSTRIDER_WORD_HOTKEY`.

---

## Credentials

Every credential the app holds — the shared-cluster password and the Cloudflare R2 token — lives
on the singleton `AppSettings` row in the **local MongoDB**, in cleartext. There is no second
store: no registry, no keychain, no encrypted file.

[`SettingsService`](DevStrider.Desktop/Services/SettingsService.cs) loads that row **once at
startup** and serves every later read from memory. Before that, each of ~16 call sites re-queried
MongoDB — `/refresh-word` hit the database on every purple click just to read a hotkey, and
opening a shared-database connection cost two round-trips before sending a byte.

Because reads now share one instance, the rule is: `GetAsync()` returns the **cached object and
must not be mutated**; anything that edits settings takes `GetForEditAsync()` (a copy) and hands
the result to `SaveAsync`, which persists it and installs it as the new cache.

Consequence worth being explicit about: anything able to read the local `devstrider` database — a
backup, a synced folder, another account on this machine — gets the shared cluster password *and*
the R2 token. An R2 token with object-write permission can also delete objects, so any install
holding it can empty the bucket.

### Shared cluster

The shared PostgreSQL database uses **one login shared by every install**. That is a deliberate
trade for a small trusted team, and it has consequences worth stating plainly:

- Every user can read *and delete* everyone else's peer data — no row-level security is configured.
- One leaked password means rotating it for every installed client at once.
- `OwnerUsername` is self-asserted; nothing enforces that a row came from who it claims.

Two ways to describe the same server, chosen by a radio button in Settings → Peer database:

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
| "MongoDB unreachable" on launch | Start the local MongoDB service. |
| Resume batch does nothing | Keep a logged-in ChatGPT tab open; confirm the profile has a Word doc path + macro name; check the **Activity** tab. |
| Shared database "unreachable / timeout" | Check host and port; allow this machine's IP in the provider's firewall; confirm the SSL setting matches what the server expects. |
| Resume generates but no file | The Word macro must read the resume from the bridge file and emit the `[FolderName]:` filename. |
| ChatGPT automation stalls | ChatGPT changed its DOM — the injection/completion selectors in `extension/content.js` need updating. |

---

## Notes

- **Single user per machine**, but multiple **profiles** (identities). No login/auth — the local
  HTTP listener is loopback-only.
- Peer sync runs over TLS, but the database's own access control is the
  protection.
- Resume generation uses the **ChatGPT web session** (free tier), not an API — hence the
  keep-a-tab-open requirement and the inherent fragility to ChatGPT UI changes.

---

## History

DevStrider began as a multi-tenant web app (React + Express + Socket.IO + Atlas) and was rewritten
as this single-user local desktop app. The team-sync layer moved from a shared **GitHub repo** of
daily JSON snapshots to a shared **MongoDB/Atlas** cluster, and in 5.0.0 to a shared
**PostgreSQL** database (`Sharing` tab → **Sync**, plus hourly background sync). The standalone
Python "ResumeAuto" tool was folded in as a batch **Resume auto-gen** tab, then removed again in
4.0.0 — resume generation is now the manual blue/purple button flow only.
