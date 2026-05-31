# DevStrider — Windows desktop (WPF)

Single-user **local** rewrite of DevStrider, in C# / WPF. No web server, no auth, no roles —
just your own data on your own machine, with optional **team-shared GitHub repo** for swapping
day snapshots between teammates.

## Requirements

- **Windows 10 / 11**
- **.NET 8 SDK** (build only) — <https://dotnet.microsoft.com/download>
- **MongoDB Community Server 7.0** — <https://www.mongodb.com/try/download/community>
  - The MSI installer registers a Windows service called `MongoDB`. Default port `27017` is what DevStrider expects.

## Build & run

From a `cmd` or PowerShell in this directory:

```powershell
dotnet restore
dotnet build -c Release
dotnet run --project DevStrider.Desktop -c Release
```

On first launch DevStrider auto-creates the `devstrider` database in your local MongoDB and
seeds an empty profile + settings doc.

### Single-file publish (optional — for shipping a `.exe` without `dotnet` installed)

```powershell
dotnet publish DevStrider.Desktop -c Release -r win-x64 ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true
```

The resulting `DevStrider.exe` (~80–120 MB) lives in
`DevStrider.Desktop/bin/Release/net8.0-windows/win-x64/publish`. Copy it anywhere; double-click
to launch. MongoDB still needs to be running locally.

## First-run setup inside the app

1. **Settings →**
   - Set your **username** (used as your filename in the team repo, e.g. `joshua`).
   - Set the **shared GitHub repo URL** (e.g. `https://github.com/your-team/devstrider-sync`).
   - Paste a **personal access token** with `repo` scope. The token is encrypted with Windows DPAPI before it touches disk.
   - Save.
2. **Profile →** fill in your resume header, education, experiences, certifications.
3. **Bids →** add links and bid rows.
4. **Settings → Push today's snapshot** to upload your data to the team repo.
5. **Import peers →** the panel lists day folders in the repo; pick a day, then import any peer
   whose `username.json` is there.
6. **Stats** + **Overview** automatically include imported peers as additional lines/rows.

## Team repo layout (what the GitHub repo will look like)

```
your-team/devstrider-sync/
├── 2026-05-25/
│   ├── joshua.json
│   ├── priya.json
│   └── diego.json
├── 2026-05-26/
│   ├── joshua.json
│   └── priya.json
└── …
```

Each `<username>.json` is the full export of that user's data for that day. New pushes overwrite
the same file under that day's folder (one snapshot per user per day).

## Storage

- Your own data: local MongoDB at `mongodb://127.0.0.1:27017/devstrider`
- Imported peer snapshots: stored read-only in the `importedSnapshots` collection, never merged
  with your own writes. Removing a snapshot from **Import peers → Remove** doesn't touch the repo.

## Differences from the web app

- No login / no JWT / no roles. The local install is single-user.
- No bid duplicate / company-role / company-interview detection across users — your warnings are
  computed against your own data only. Peer data is read-only and only powers comparison.
- No Socket.IO; updates after a save are pulled with an explicit Refresh.
