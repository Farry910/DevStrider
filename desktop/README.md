# DevStrider Desktop

A local-first, Windows desktop app (.NET 8 / WPF) for tracking job **bids** and **interviews**,
auto-generating tailored **resumes** through ChatGPT, and sharing daily status with a team via a
shared MongoDB/Atlas cluster.

- **Desktop app version:** 3.4.0
- **Chrome extension version:** 2.3.0 (the "Bid Assistant")
- **Platform:** Windows 10/11 only (uses Word COM automation, DPAPI, the system tray, and Win32 interop)

---

## What it does

DevStrider is the local hub of a three-part system:

```
 Chrome extension  ──HTTP(127.0.0.1:8765)──►  DevStrider desktop  ──►  Local MongoDB
 (job pages +                                  (WPF app + tray)         (your bids/interviews)
  ChatGPT tab)                                        │
                                                      └──►  Shared MongoDB / Atlas (peers)
```

1. **Record bids** — browse a job posting, the extension extracts the JD and records a bid in the
   local database.
2. **Auto-generate resumes** — paste a batch of job links; for each one the extension scrapes the
   JD, drives ChatGPT (no clipboard, in the background), runs your Word macro to produce a tailored
   resume file, and **auto-records the bid** with the extracted company/role/stacks.
3. **Track interviews** — schedule interviews off a bid, carrying the JD + resume forward.
4. **Share with a team** — push your bid/interview summaries to a shared cluster and pull peers'
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
and runs the multi-profile migration. The title-bar pill shows the running version (e.g. `v3.4.0`)
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

…plus the **batch engine** (see *Resume auto-gen* below), which runs automatically while a ChatGPT
tab is open. The extension talks only to `http://127.0.0.1:8765` (loopback, no auth).

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

### Resume auto-gen
Paste job links → **Start** → walk away. Pipeline per URL (background, no clipboard, no focus theft):

1. Extension scrapes the JD in a throwaway tab.
2. Injects `prompt + JD` into your ChatGPT tab and harvests the reply.
3. App writes the resume to a bridge file and runs your Word macro by name (Word invisible).
4. App parses the trailing fast-feed line and **auto-records the bid**.

Statuses: `Queued → Generating → Resume Received → Done` (or `Failed`). Failed jobs are skipped;
**Retry failed** re-queues them.

> **Keep one logged-in ChatGPT tab open** for the whole batch — the engine lives in that tab. You
> can work in other apps/windows; just don't close the ChatGPT tab or switch the active profile
> mid-batch.

**Prompt contract** — ChatGPT's reply must end with these two lines, in order:

```
[FolderName]: <output_filename>        ← your Word macro reads this (unchanged)
UID, Company, Role, Stack1, Stack2     ← MUST be the last line; DevStrider strips it for the bid
```

Use **Profiles → Insert default** to get a working template.

### Overview / Stats
Aggregate counts and a bids-per-10-minute chart, for you and any synced peers, over a date range.

### Peers
Read-only view of peers' bids and interviews pulled from the shared cluster (company / role /
status / stacks / dates — private fields like URLs and JDs are never shared). Filter by date + owner.

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
- **Peer database** — shared MongoDB/Atlas URI + DB name, with **Test connection**
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
| `GET`  | `/resume/next-job` | Claim the next queued resume job for the active profile |
| `POST` | `/resume/result` | Deliver ChatGPT output → macro + auto-bid |
| `POST` | `/resume/fail` | Mark a resume job failed |
| `GET`  | `/browse-word` | Native file picker for the .docm path |

---

## Data model (local MongoDB, db `devstrider`)

| Collection | Holds |
|------------|-------|
| `bidProfiles` | Bidding identities (Profile) |
| `links` | Job-posting URLs (GroupLink), profile-scoped |
| `bids` | Your bids (UserBid), profile-scoped |
| `interviews` | Interviews, profile-scoped |
| `resumeJobs` | The resume-generation queue |
| `peerBids` / `peerInterviews` | Local mirror of peers' shared data |
| `settings` / `profiles` | App settings + the username singleton |

The shared cluster holds only `peerBids` + `peerInterviews`.

---

## Configuration via environment variables

Empty/default settings are seeded once at first launch from `DEVSTRIDER_*` variables (set with
`setx`, then restart). See the **About** tab for the full list — e.g.
`DEVSTRIDER_SHARED_MONGO_URI`, `DEVSTRIDER_USERNAME`, `DEVSTRIDER_LISTENER_PORT`,
`DEVSTRIDER_WORD_DOC_PATH`, `DEVSTRIDER_WORD_HOTKEY`.

Word macro path/hotkey are also mirrored to `HKCU\Software\DevStrider` so they survive a Mongo wipe.

---

## Troubleshooting

| Symptom | Fix |
|---------|-----|
| `MSB3027: DevStrider.exe locked` on build | The app is still running — tray → **Quit** (or `taskkill /F /IM DevStrider.exe`). |
| "MongoDB unreachable" on launch | Start the local MongoDB service. |
| Resume batch does nothing | Keep a logged-in ChatGPT tab open; confirm the profile has a Word doc path + macro name; check the **Activity** tab. |
| Shared cluster "unreachable / timeout" | Add your IP to the Atlas IP Access List; check firewall/VPN on port 27017; try the non-SRV URI. |
| Resume generates but no file | The Word macro must read the resume from the bridge file and emit the `[FolderName]:` filename. |
| ChatGPT automation stalls | ChatGPT changed its DOM — the injection/completion selectors in `extension/content.js` need updating. |

---

## Notes

- **Single user per machine**, but multiple **profiles** (identities). No login/auth — the local
  HTTP listener is loopback-only.
- Peer sync over Atlas is currently **plaintext over TLS**; the cluster's access control is the
  protection.
- Resume generation uses the **ChatGPT web session** (free tier), not an API — hence the
  keep-a-tab-open requirement and the inherent fragility to ChatGPT UI changes.

---

## History

DevStrider began as a multi-tenant web app (React + Express + Socket.IO + Atlas) and was rewritten
as this single-user local desktop app. The team-sync layer moved from a shared **GitHub repo** of
daily JSON snapshots to a shared **MongoDB/Atlas** cluster (`Sharing` tab → **Sync**). The standalone
Python "ResumeAuto" tool was folded into this app as the **Resume auto-gen** tab + the merged Chrome
extension.
