# DevStrider 9.0

A Windows desktop app and a Chrome extension that track job bids for a team, backed by
**hr-system's** `/api/devstrider/*` HTTP API.

One button on a job page reads the description, has ChatGPT tailor a resume, builds it in Word
silently, and records the bid — while you stay on the page filling in the application.

## Architecture

```
Chrome extension  ──http://127.0.0.1:8765──▶  DevStrider.exe (WPF)  ──HTTPS──▶  hr-system
   (Bid Assistant)         loopback only          Word via COM                  (/api/devstrider/*,
                                                                                   PostgreSQL behind it)
```

Three moving parts and no server of DevStrider's own:

| | |
|---|---|
| `desktop/` | The app. WPF on .NET 10. Talks to hr-system over HTTPS and drives Word automation. It holds no database credential — only a bearer token. |
| `extension/` | Manifest V3 Chrome extension. Talks only to the desktop app over loopback. |
| `desktop/shared-db-schema.sql` | The four `ds_*` tables. Run by hand, once, against **hr-system's** database — not something this app ever touches directly any more. |

**There is no web app, no API server of DevStrider's own, and no local or direct database.** Every
account and every `ds_*` row is reached through hr-system's `/api/devstrider/*` API, so a teammate's
bid is visible the moment they save it. There is nothing to sync and no mirror to fall behind.

Earlier versions were a React + Express + MongoDB monorepo with a local MongoDB per machine and
`peer_*` summary tables pushed to a shared cluster. All of that is gone: `client/`, `server/`, the
peer mirror, and the sync scheduler were deleted in the 8.0 migration. **9.0** went further and
removed the direct Postgres connection that 8.0 introduced — see [Version history](#version-history).

## The database

DevStrider **shares hr-system's database — through hr-system's API, not a connection of its own.**
It is not DevStrider's own, and DevStrider no longer even holds a credential that could reach it
directly.

- **hr-system owns `app_user`.** DevStrider never sees it directly — sign-in is one call to
  `POST /api/devstrider/auth/login`, which hr-system answers with a bearer token and your identity.
  The app never creates an account, never sets a password, and offers no sign-up or reset — there is
  no way to become a DevStrider user without an hr-system account first.
- **hr-system's `/api/devstrider/*` routes own four `ds_*` tables**, keyed on `app_user.id`, and
  DevStrider talks to nothing else. It issues **no DDL at all**: `desktop/shared-db-schema.sql` is
  run once against hr-system's database, by whoever operates it — not per DevStrider install.
- **Everything in those tables is visible to everyone with an account** — job URLs, job
  descriptions, generated resume text and your comments included. They are not a stripped
  projection of something more private; they are the only copy.

`desktop/shared-db-verify.sql` is the drift check — run it against hr-system's database after any
schema change.

### Tables

Four of them: `ds_users` (one row per account — the hr-system email, and nothing else worth
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
- Network access to an **hr-system** deployment (default `https://triospace.org/hr`), with
  `desktop/shared-db-schema.sql` already applied to *its* database
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

1. **Create the tables.** Run `desktop/shared-db-schema.sql` against hr-system's database. Once,
   for the team — not once per machine, and not something a DevStrider install does itself.
2. **Start the app.** It tries to restore a saved session first; on a fresh install there is none,
   so it opens the sign-in window.
3. **Point it at hr-system, if it isn't the default.** Settings → hr-system → **Server address**
   defaults to `https://triospace.org/hr`. There is nothing else to configure here — no database
   host, port, or password; DevStrider holds none of those any more.
4. **Sign in** with your hr-system account. On first successful sign-in hr-system creates your
   `ds_users` row and DevStrider seeds a profile named *Default*. The session is a week-long bearer
   token, saved locally, so this is not repeated on every launch.
5. **Set up the profile.** Profiles tab: point it at that person's `.docm`, leave the macro name
   blank to use `UpdateResumeAndSwitchOriginal`, and press *Insert default* for a resume prompt
   that emits every marker the macro expects.
6. **Load the extension.** `chrome://extensions` → Developer mode → Load unpacked → pick
   `extension/`. Keep one logged-in ChatGPT tab open in the background.

### Sign-in

Your hr-system email address **is** your DevStrider identity — it is what `ds_users.username` holds
and what teammates see on the Peers tab. There is no separate username to choose, and re-asserting
it on every login means a rename in hr-system follows you here.

The session is a signed, week-long bearer token (`/api/devstrider/auth/login`), saved to
`%LOCALAPPDATA%\DevStrider\settings.json` and refreshed automatically once it is inside its last
day — so the password is asked for once, not on every start. A wrong address and a wrong password
give the same message on purpose — distinguishing them would report on who has an account to anyone
who can reach the login endpoint.

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

The macro is invoked headless over COM with the resume text and the job description as its two
arguments, so its signature must take two `String`s, and it must **not** read the clipboard:

```vb
Sub UpdateResumeAndSwitchOriginal(ByVal ClipText As String, ByVal JobDescription As String)
```

It should fill the bookmarks from the `[Section]:` labels in the first argument, save its `.docx`
and `.pdf` (and, optionally, the job description as a text file in the same folder — see
[`desktop/macro.md`](desktop/macro.md)), and finish with `Application.Quit` — DevStrider treats Word
closing as the success signal. A macro that returns without quitting is reported as failed after 90
seconds. A `Sub` with parameters no longer appears in Word's Alt+F8 list; that is expected, since
DevStrider drives it. A template still on the one-parameter signature will fail every run — see
[`desktop/macro.md`](desktop/macro.md) for how to update it.

## The local listener

The desktop app binds `http://127.0.0.1:8765`, loopback only. That binding is what stands in for
authentication — nothing off the machine can reach it — so requests carry no credential and are
served as whoever is signed in. It therefore starts only **after** a session exists, restored
silently from the saved bearer token or established through the sign-in window.

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
| `DEVSTRIDER_HR_API_BASE_URL` | hr-system server address, default `https://triospace.org/hr` |
| `DEVSTRIDER_LISTENER_PORT` | Listener port, default 8765 |
| `DEVSTRIDER_WORD_DOC_PATH` | Word template for the seeded *Default* profile |

There is no username variable: the account name comes from hr-system's `app_user`.

## Upgrading from 7.x

Before you start, back up the machine's local MongoDB — after the migration the shared database is
the only copy of that person's bids and interviews.

**Settings carry across automatically.** On first launch with no `settings.json`, the app reads
the old MongoDB once and copies the saved values over — R2 keys, listener port, Word path — so
nothing has to be retyped. It never writes to MongoDB. After that the service can be stopped and
uninstalled.

**Old bids do not carry across, by design.** There is no data migration in the app. A one-time
importer was built and then removed: it existed to solve a problem the folder back door already
solves without depending on anyone's old database still being installed, reachable, and holding
what they think it holds.

To get historical bidding onto the board, use **Bids → From folder…** — see
[Recording a day from resume folders](desktop/README.md#recording-a-day-from-resume-folders). It
reads the resume folders the Word macro already wrote to disk, which is a record that outlives any
database.

## Storage of credentials

The hr-system bearer token and the Cloudflare R2 token are stored in cleartext in
`%LOCALAPPDATA%\DevStrider\settings.json`, alongside the rest of the settings. There is no database
credential in this file at all any more — DevStrider holds nothing that can reach Postgres directly.
The bearer token is good for up to a week and is revocable from Settings → hr-system → **Sign out**;
an R2 token with write permission can also delete — so every machine holding this file can wipe the
bucket. Treat the file accordingly.

## Version history

**9.0.0** — see `<Version>` in `desktop/DevStrider.Desktop/DevStrider.Desktop.csproj` for the full
changelog comment. The app shows the version in the title bar so you can tell at a glance whether a
build picked up the latest source. The headline change in 9.0.0: DevStrider no longer holds a
Postgres credential or opens a database connection of its own — sign-in and every `ds_*` read/write
go through hr-system's `/api/devstrider/*` HTTP API on a week-long bearer token instead. The resume
macro also gained a second parameter so it can save the job description alongside the resume it
writes — see [`desktop/macro.md`](desktop/macro.md).
