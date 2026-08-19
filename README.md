# DevStrider 8.2

A Windows desktop app and a Chrome extension that track job bids for a team, backed by the
company portal's PostgreSQL database.

One button on a job page reads the description, has ChatGPT tailor a resume, builds it in Word
silently, and records the bid — while you stay on the page filling in the application.

## Architecture

```
Chrome extension  ──http://127.0.0.1:8765──▶  DevStrider.exe (WPF)  ──▶  PostgreSQL
   (Bid Assistant)         loopback only          Word via COM             (the portal's)
```

Three moving parts and no server of DevStrider's own:

| | |
|---|---|
| `desktop/` | The app. WPF on .NET 10. Owns every database write and the Word automation. |
| `extension/` | Manifest V3 Chrome extension. Talks only to the desktop app over loopback. |
| `desktop/shared-db-schema.sql` | The four `ds_*` tables. Run by hand, once, for the whole team. |

**There is no web app, no API server, and no local database.** Every machine reads and writes the
same PostgreSQL database directly, so a teammate's bid is visible the moment they save it. There
is nothing to sync and no mirror to fall behind.

Earlier versions were a React + Express + MongoDB monorepo with a local MongoDB per machine and
`peer_*` summary tables pushed to a shared cluster. All of that is gone: `client/`, `server/`, the
peer mirror, and the sync scheduler were deleted in the 8.0 migration.

## The database

DevStrider **shares the company portal's database.** It is not DevStrider's own.

- **The portal owns `app_user`.** DevStrider only ever `SELECT`s from it. Sign-in reads `email`,
  `password_hash` and `email_verified`. The app never creates an account, never sets a
  password, and offers no sign-up or reset — there is no way to become a DevStrider user without
  a portal account first.
- **DevStrider owns four `ds_*` tables**, keyed on `app_user.id`. It issues **no DDL at all**:
  run `desktop/shared-db-schema.sql` in your SQL editor once. If the tables are missing the app
  says so rather than inventing a schema.
- **Everything in those tables is visible to everyone with the login** — job URLs, job
  descriptions, generated resume text and your comments included. They are not a stripped
  projection of something more private; they are the only copy.

`desktop/shared-db-verify.sql` is the drift check — run it after any schema change, or when the
app reports SQLSTATE 42703.

### Tables

Four of them: `ds_users` (one row per account — the portal email, and nothing else worth
storing) · `ds_profiles` (the bidding identities you switch between) · `ds_bids` ·
`ds_interviews`.

**The CV is not in the database at all.** Education, certifications and work history used to be
three child tables off `ds_profiles`; they were dropped in 8.1.0, along with `ds_achievements`.
That material lives in each profile's `.docm`, which is where it was being written and maintained
anyway — a second copy in here only ever meant two versions of one CV, and the database's was the
one nobody updated. DevStrider never reads a CV and never renders one.

A captured job posting and the bid against it are **one row** in `ds_bids`. The relationship was
always one-to-one, and a posting with nothing bid on it is exactly what `status = 'draft'` means.

## Prerequisites

- **Windows 10/11** and the **.NET 10 SDK**
- **Microsoft Word** with a macro-enabled template per profile (see *Word template* below)
- Access to the portal's **PostgreSQL** database
- **Chrome**, for the extension
- MongoDB is *not* required. It is read once, if present, to carry an old install's settings
  across — see *Upgrading from 7.x*.

## Build and run

```powershell
cd desktop\DevStrider.Desktop
dotnet build
dotnet run
```

The built executable is `desktop\DevStrider.Desktop\bin\Debug\net10.0-windows\DevStrider.exe`.

## First run

1. **Create the tables.** Run `desktop/shared-db-schema.sql` against the portal's database. Once,
   for the team — not once per machine.
2. **Start the app.** It opens the sign-in window.
3. **Fill in the database connection.** The sign-in window has a *Database connection* panel —
   open by default when nothing is configured. Either paste a service URI
   (`postgresql://user:password@host:5432/devstrider?sslmode=require`) or give host / port /
   database / user / password. Press **Test connection**: it proves the server answers and reports
   any missing table. Settings are saved to `%LOCALAPPDATA%\DevStrider\settings.json`.

   This panel is on the login window rather than in Settings because Settings sits behind the
   login it would be configuring.
4. **Sign in** with your company portal account. On first successful sign-in DevStrider creates
   your `ds_users` row and a profile named *Default*.
5. **Set up the profile.** Profiles tab: point it at that person's `.docm`, leave the macro name
   blank to use `UpdateResumeAndSwitchOriginal`, and press *Insert default* for a resume prompt
   that emits every marker the macro expects.
6. **Load the extension.** `chrome://extensions` → Developer mode → Load unpacked → pick
   `extension/`. Keep one logged-in ChatGPT tab open in the background.

### Sign-in

Your portal email address **is** your DevStrider identity — it is what `ds_users.username` holds
and what teammates see on the Peers tab. There is no separate username to choose, and re-asserting
it on every login means a rename in the portal follows you here.

There is no persisted session by design: the password is asked for on every start, and nothing
about it reaches disk. A wrong address and a wrong password give the same message on purpose —
distinguishing them would report on who has a portal account to anyone holding the database
credential.

## Profiles

One profile per real person you bid as. Each carries its own Word template, resume prompt and
contact details; every bid and interview belongs to whichever profile was active when it was
created. Switch from the title-bar dropdown.

The CV belongs to the `.docm`, not to the profile row — DevStrider stores nothing about it at all:
it never reads a CV and never renders one.

## Word template

Each profile has its own `.docm`. It must contain nine bookmarks:

```
bmTitle   bmSummary   bmSkills
bmSubtitle1   bmExperience1
bmSubtitle2   bmExperience2
bmSubtitle3   bmExperience3
```

The macro is invoked headless over COM with the resume text as its single argument, so its
signature must take one `String`, and it must **not** read the clipboard:

```vb
Sub UpdateResumeAndSwitchOriginal(ByVal ClipText As String)
```

It should fill the bookmarks from the `[Section]:` labels in that text, save its `.docx` and
`.pdf`, and finish with `Application.Quit` — DevStrider treats Word closing as the success signal.
A macro that returns without quitting is reported as failed after 90 seconds. A `Sub` with a
parameter no longer appears in Word's Alt+F8 list; that is expected, since DevStrider drives it.

## The local listener

The desktop app binds `http://127.0.0.1:8765`, loopback only. That binding is what stands in for
authentication — nothing off the machine can reach it — so requests carry no credential and are
served as whoever is signed in. It therefore starts only **after** login.

| Endpoint | Purpose |
|---|---|
| `GET /health`, `GET /` | Liveness, so the extension popup can tell you the app is up. |
| `GET /active-profile` | The active profile's resume prompt, for the extension to send to ChatGPT. |
| `POST /prewarm` | Launch Word and open the template while ChatGPT is still writing. |
| `POST /generate-resume` | Run the macro and record the bid, in one call. |
| `POST /record-bid` | Record a bid without the macro. `/record-devstrider` is an alias. |
| `POST /refresh-word` | Re-run the macro against text already on the page. |
| `POST /trigger-paste-submit` | Paste and submit into the ChatGPT tab. |
| `GET /browse-word` | Open a file picker for the profile's `.docm`. |

Capture is keyed on the strict-normalized URL — lowercased, trailing slash trimmed, query and
hash **kept**. Two tracking links to the same posting are two rows, deliberately: merging them
would hide that you bid the same job twice.

### The fast-feed line

The ChatGPT reply must end with a bare comma-separated line:

```
UID, Company, Role, Stack1, Stack2, Stack3
```

That line fills in the bid's resume id, company, role and stacks, and flips its status to
`applied`. Without it the bid is still recorded, just bare.

The same line is what the macro names its output folder with, which makes the folder name a
complete bid. **Paste it into the box at the top of the Bids tab** and the row is recorded — that
is how bids are added by hand. There is no URL field: by the time a folder exists the resume has
been generated, and the posting it came from is not what you are entering.

## Environment variables

Empty or still-default settings fields are seeded from `DEVSTRIDER_*` variables at launch, which
is useful when bootstrapping a machine. After the first run the settings file owns them and the
variables stop mattering. The full list is in the app's About tab; the ones that matter most:

| Variable | Seeds |
|---|---|
| `DEVSTRIDER_SHARED_DB_URI` | Service URI (selects URI mode) |
| `DEVSTRIDER_SHARED_DB_HOST` / `_PORT` / `_NAME` / `_USER` / `_PASSWORD` | Host mode |
| `DEVSTRIDER_LISTENER_PORT` | Listener port, default 8765 |
| `DEVSTRIDER_WORD_DOC_PATH` | Word template for the seeded *Default* profile |

There is no username variable: the account name comes from `app_user`.

## Upgrading from 7.x

Before you start, back up the machine's local MongoDB — after the migration the shared database is
the only copy of that person's bids and interviews.

Two things move across, and neither writes to MongoDB.

**Settings, automatically.** On first launch with no `settings.json`, the app reads the old
MongoDB once and carries the saved values over — database credentials, R2 keys, listener port,
Word path — so nothing has to be retyped.

**Your history, on request.** Settings → *Import this machine's history* → **Look for legacy
data**, then **Import**. It lifts your profiles, captured postings, bids and interviews into the
shared database under the account you are signed in as.

- Only *your own* work moves. The `peerBids` / `peerUsers` / `peerInterviews` collections that
  older versions downloaded are skipped: they were a copy of what teammates published, not yours
  to re-publish, and the originals are still in the shared database's `peer_*` tables.
- Links and bids were two collections joined one-to-one and are one row now. The link is the
  spine, so a posting you captured but never bid on arrives as a draft.
- **Safe to run twice.** Every row keeps the ObjectId it had in MongoDB and each write is an
  upsert on it, so a re-run finishes an interrupted import rather than duplicating what landed.
  A profile you have already edited in 8.x is left alone.
- The old CV (education, certifications, experience) is not imported — that lives in the `.docm`
  now, and DevStrider keeps no copy of it.

Once both have happened the MongoDB service can be stopped and uninstalled.

## Storage of credentials

The shared-database password and the Cloudflare R2 token are stored in cleartext in
`%LOCALAPPDATA%\DevStrider\settings.json`, alongside the rest of the settings. The database login
is one credential the whole team shares, and an R2 token with write permission can also delete —
so every machine holding this file can wipe the bucket. Treat the file accordingly.

## Version

**8.2.0** — see `<Version>` in `desktop/DevStrider.Desktop/DevStrider.Desktop.csproj`. The app
shows it in the title bar so you can tell at a glance whether a build picked up the latest source.
