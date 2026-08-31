# DevStrider 10.28

A Windows desktop app that tracks job bids for a team, backed by the company portal's API.

It reads a job posting, has ChatGPT tailor a resume, builds it in Word silently, fills the
application form, and records the bid — with a review step before anything is submitted.

## Architecture

```
DevStrider.exe (WPF) ──HTTPS──▶ hr-system ──▶ PostgreSQL
  embedded WebView2   bearer    (the portal)
  Word via COM        token
```

Two moving parts, and the database is behind neither of them:

| | |
|---|---|
| `desktop/` | The app. WPF on .NET 10. Owns the Word automation and nothing else's data. |
| the portal | `hr-system`, which owns the `ds_*` tables and serves `/api/devstrider/*`. |

**There is no Chrome extension any more.** The *Bid Assistant* extension was deleted in 9.1.0,
once the embedded ChatGPT and job-site workspaces did its job from inside the app. The loopback
listener it used to talk to is still there — see [The local listener](#the-local-listener) — but
it is a developer and scripting surface now, not a component.

**There is no local database and no local copy.** A teammate's bid is visible the moment they save
it; there is nothing to sync and no mirror to fall behind.

### 10.0: the app stopped being a database client

Until 10.0 every install held the portal's PostgreSQL password, opened its own connection, and
issued its own SQL — including verifying `app_user.password_hash` in C#, a hand-port of the
portal's scrypt that nothing kept in step with the original. Three things were wrong with that,
and they were the same thing three times: **the app was doing the server's job.**

- A **database credential on every laptop**, in cleartext in `settings.json`, with rights over the
  whole team's data. Rotating it meant visiting every machine; losing a laptop meant rotating it.
- **Authentication implemented twice**, in two languages. A rule changed in the portal — a lockout,
  a password policy, a disabled account — did not reach DevStrider, because DevStrider was not
  asking the portal anything. It was reading a hash and deciding for itself.
- **The `ds_*` tables owned by nobody.** They were created by hand from a `.sql` file the app
  shipped but would not run, so "is the schema current" was a question you answered by reading a
  drift-check query.

Now: the app signs in at `/api/devstrider/auth/login`, gets a **bearer token good for a week**, and
reads and writes everything through `/api/devstrider/*`. It holds no database credential, contains
no SQL, and has no crypto library. The portal creates and migrates the tables like any of its own.

Earlier versions were a React + Express + MongoDB monorepo with a local MongoDB per machine and
`peer_*` summary tables pushed to a shared cluster. All of that went in the 8.0 migration.

## The data

DevStrider's rows live in the company portal's database, and DevStrider reaches them only through
the portal's API.

- **The portal owns accounts.** It is the only thing that checks a password. The app never creates
  an account, never sets a password, and offers no sign-up or reset — there is no way to become a
  DevStrider user without a portal account first.
- **The portal owns the five `ds_*` tables**, keyed on `app_user.id`, created and migrated by
  `hr-system/migrations/postgres/011_devstrider_api.sql` on boot. `desktop/shared-db-schema.sql` is
  the retired hand-run version, kept as a readable description of the shape. Its `DROP TABLE …
  CASCADE` lines are commented out as of 10.18 — they would have taken the whole team's only copy
  with them, and a README note is not a safeguard against a paste.
- **Every write is pinned to the token's own account.** A request cannot name a user id; the server
  takes it off the signature. Reads across the team are deliberate and confined to
  `/api/devstrider/peers/*`, which is the Peers tab.
- **Everything stored is visible to everyone on the team** — job URLs, job descriptions, generated
  resume text and your comments included. They are not a stripped projection of something more
  private; they are the only copy.

### Tables

Five: `ds_users` (one row per account — the portal email, and nothing else worth storing) ·
`ds_profiles` (the bidding identities you switch between) · `ds_bids` · `ds_interviews` ·
`ds_person_facts`.

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
- A **portal account**. The portal's address is compiled in (see *The portal address* below), so
  there is nothing to configure — but you need an account on it before you can sign in.
- The **WebView2 runtime**, which ships with Windows 11 and current Windows 10.
- MongoDB is not used at all. The driver went in 9.3.0 along with the last thing that read it.

### The portal address

`https://triospace.org/hr`, as `PortalApi.Url` — a `const`, not a setting. There is no *Portal
address* panel, no `DEVSTRIDER_PORTAL_URL`, and no field for it in Settings; earlier versions of
this README described all three, and none of them have existed for several releases. Changing
which portal a build talks to means editing that constant and rebuilding.

## Build and run

```powershell
cd desktop\DevStrider.Desktop
dotnet build
dotnet run
```

The built executable is `desktop\DevStrider.Desktop\bin\Debug\net10.0-windows\DevStrider.exe`.

## First run

1. **Start the app.** It opens the sign-in window. Nothing has to be created first: the portal
   brings its own tables up when it boots.
2. **Sign in** with your company portal account. There is nothing to configure first — the portal
   address is compiled in. On first successful sign-in the portal creates your `ds_users` row, and
   the app seeds a profile named *Default*.
3. **Set up the profile.** Profiles tab: point it at that person's `.docm`, leave the macro name
   blank to use `UpdateResumeAndSwitchOriginal`, and press *Insert default* for a resume prompt
   that emits every marker the macro expects.
4. **Fill in the personal facts.** Profiles → personal data: education, career dates, and any
   custom fields (work authorisation, citizenship, clearance, licences). This is not optional
   paperwork — it is the grounding set. Anything not stated here, the app will refuse to answer
   on a form rather than guess. See [Grounded answers](#grounded-answers).
5. **Sign in to ChatGPT** once in the app's own Resume Studio browser. It keeps its own WebView2
   profile, so that session persists across restarts.

### Sign-in, and the week

Your portal email address **is** your DevStrider identity — it is what `ds_users.username` holds
and what teammates see on the Peers tab. There is no separate username to choose, and re-asserting
it on every sign-in means a rename in the portal follows you here.

**You sign in about once a week.** The portal answers a sign-in with a token that is good for seven
days, and the app keeps it in `%LOCALAPPDATA%\DevStrider\session.dat`, encrypted with DPAPI under
your Windows account — copied to another machine, or read by another Windows user on this one, it
does not decrypt. On every launch the app puts that session back and asks the portal whether it is
still good, and once the token is inside its last day it trades it for a fresh week. So in ordinary
daily use the sign-in window is something you see when you first set the machine up, and then not
again.

This is a straight improvement on what it replaced. The old build asked for a password on every
start *because* the thing it would otherwise have had to keep was a database password with rights
over everyone's data. What is kept now is scoped to DevStrider, expires on its own, and can be
declined by the portal at any point before that — change `DEVSTRIDER_JWT_SECRET` on the server and
every outstanding token dies at once. Settings → *Sign out on this machine* deletes the local copy.

A wrong address and a wrong password give the same message on purpose, and now they do so because
the portal answers both the same way rather than because the app chose not to look.

## Profiles

One profile per real person you bid as. Each carries its own Word template, resume prompt and
contact details; every bid and interview belongs to whichever profile was active when it was
created. Switch from the title-bar dropdown.

The CV belongs to the `.docm`, not to the profile row — DevStrider stores nothing about it at all:
it never reads a CV and never renders one.

## ChatGPT accounts and the two workspaces

Automatic runs and manual bids each drive **their own ChatGPT browser**, so a manual bid's resume
can be written while the queue is mid-generation. They appear as **Resume Studio** and **Resume
Studio · manual**.

An account *is* a browser profile folder — that's where the cookies live. Two browsers on one
folder are one signed-in user; two on different folders are two. Settings → **ChatGPT accounts**
manages them:

- **Add** — creates an empty profile. Open that workspace and sign in once; the session persists
  because the folder does.
- **Rename** — display name only. The folder never moves, because moving it would sign the account
  out.
- **Remove** — forgets the account. The folder is left on disk, so adding it back recovers the
  session. Lanes pointing at it fall back to a remaining account rather than silently changing
  identity.
- **Assign** — which lane signs in as which account. Takes effect when that workspace's browser
  next starts.

The seeded *Default* adopts the folder the single pre-10.21 browser used, so upgrading signs nobody
out.

### Sharing one account

**Separate accounts** is the clean case: separate conversations, separate rate limits, nothing to
arbitrate.

**One account** still runs both at once, but they share a conversation list — and that needed real
work. The remembered chat used to be keyed on profile id alone, so both lanes read the same URL at
startup and both continued it: two jobs' descriptions interleaved into one thread, each reading the
other's reply as its own, and nothing about the result looking wrong.

The key is now profile **and** lane. `ChatGptConversationRegistry` covers what that key can't — the
same conversation reaching both lanes anyway, via ChatGPT's own sidebar or a hand-edited settings
file. First claim wins; the loser starts a fresh chat, which costs one profile prompt and is always
safe. The Settings card tells you which case you're in and how many conversations are claimed.

## Automatic runs in the background

By default the queue works in workspaces that are **not on screen**, so it doesn't move the page out
from under you while you're filling a manual bid. Settings → *Automatic runs* turns it off, and then
each stage brings its workspace to the front as it used to.

This was measured, not assumed. The old behaviour was justified partly on throttling — *"the stage
that is actually driving a page belongs on screen: it runs unthrottled"*. On the running app a
hidden WebView2 keeps a real viewport (590×625, not 0×0) and its timers run at full rate: **131 of
132 expected ticks over six seconds**, against 132 for a visible one. Only `requestAnimationFrame`
stops — 0 frames against 397 — which is animation rather than logic, and the run reads pages by
script from outside rather than by anything the page schedules for itself.

**What it costs:** a finished application no longer puts itself in front of you. It parks in its tab
exactly as it already did, and the review count on the Job Browser is where you see it waiting.

Switches a *person* asks for are unaffected — pressing something that opens a workspace still opens
it, because a button that silently does nothing is a broken button.

## Manual bids

Greenhouse, Lever and Ashby apply correctly on their own. For everything else — a board with no
adapter, where hunting for the form is a guess and filling it is a worse one — there is the
**manual bid board**: you fill the forms, the app writes the resumes beside you.

Several bids sit on the board at once, each with its own tab and its own form. Per row:

1. **Open its tab** — navigates there and stops. No adapter, nothing typed, nothing pressed.
2. **Paste the job description** into that row's box.
3. **Queue resume** — generates in the background. The form doesn't have to be open; a description
   is all a resume needs, so you can line several up and then work through them.
4. **Attach resume** when it lands, or **Show file** to pick it by hand.
5. **I submitted this** — moves the bid draft → applied and takes the row off the board.

The point is that you are never waiting. While one resume is being written you are filling the next
form, and the board tells you where each one is: *"3 open · 1 resume being written · 2 waiting."*

**Generation is serial per lane** — one ChatGPT browser each — which is exactly why this is a board
you can watch rather than a panel that makes you wait. Automatic runs and manual bids each get one
resume in flight, concurrently, since 10.21 gave them separate browsers.

### Links the automatic run couldn't apply

They come here. A **posting-level** failure — an unreadable description, a form no adapter matches,
a page behind a human gate — means the automation is done with that link and retrying changes
nothing. It moves to the board with its error on the row, and **whatever the run already got comes
with it**: most fail at opening the form, which is after the description was read, so the row
usually arrives with its job description filled in and its resume one button away.

No tab is opened for it — the run may still be going, and popping a tab per skipped link would bury
whatever it's actually working on. The row waits until you ask for it.

**Machinery failures don't come here.** ChatGPT signed out, Word unavailable, no profile — those
break the manual lane in exactly the same way, so handing them over would hand over the problem.
They stay in the Failed list and stay retryable, which is what fixing the machinery makes possible.

Two deliberate choices, unchanged: **the resume is offered, not attached** (a controlled form can
rerender when its file input changes, and doing that underneath you mid-field is how a half-filled
application gets lost), and **you confirm the submission** (the app isn't driving the page, so it
can't watch you press submit and shouldn't claim to).

Manual rows survive a restart — the status is a decision about the link, not work a run was holding.
A resume that was queued or mid-generation drops back to waiting, with your description still on it.

## Finding the application form

Five sites have written adapters — `greenhouse.io`, `ashbyhq.com`, `lever.co`, `applytojob.com`,
`teamtailor.com` — matched on host. Everything else falls to the generic path, which is most
postings in practice.

The generic path is a loop, not a guess. It surveys the live page for its tabs, its fillable
controls and everything clickable on it; scores each candidate on apply wording, `href` and
attributes; clicks the best untried one; and surveys again to find out whether that helped. Up to
four hops, so `posting → apply → form` works, and a hop that changed nothing is remembered so the
next one tries something else.

What that buys over the old single-shot click:

- **The button doesn't have to say "Apply."** *I'm interested*, *Express interest*, *Register your
  interest*, *Join our team*, *Quick apply*, *Start your application* all score.
- **Tabs are searched first.** An `Overview` / `Application` split leaves the form on the page but
  unpainted, so a control count taken on arrival counts the wrong panel. Unselected tabs are opened
  and checked before any navigation.
- **Multi-step flows work.** A posting page with only a description and a button leads to the
  application page, which is where filling starts.
- **It won't wander.** Structural shape ("looks like a button", "short label") only breaks ties —
  it can't qualify a candidate with no apply signal. Without that rule a job index page offers its
  first job title as the way in and the run applies to a different job.
- **It can't press send.** `Submit`, `Send application`, `Next`, `Continue` and friends score −100
  and are never clicked. This loop finds the way in; it is not allowed to become the way out.
- **Dead ends say something useful.** `1 control` was true of all seven Deel failures and told
  nobody what to do. Now the trace names the page, the candidates it saw and what it tried.

Descriptions are gathered the same way: each tab is opened and its panel appended, so a posting
split across `Overview` / `Responsibilities` / `Benefits` reaches ChatGPT whole rather than as
whichever panel loaded first. Panels containing form controls are skipped — an `Application` tab is
a form, and its field labels are exactly what the description test exists to keep out of the prompt.

Adding a site-specific adapter is still worth it where one exists; this is what happens when one
doesn't.

## Grounded answers

**The app does not make up facts about you.** Application forms mix two kinds of question, and
they are not the same kind of thing:

- **Facts an employer verifies** — work authorisation, visa sponsorship, citizenship, security
  clearance, degrees, licences, certifications, employment dates and years of experience, criminal
  history, salary history, references, date of birth. These are answered **only** from your profile
  and personal facts. Where those are silent, the question is **not answered**: it is lifted out,
  the field is left blank, and it appears in **Quick answers** with whatever the model wanted to
  say, so you can see what it was about to claim.
- **Choices that are yours to make** — consent to a background check, willingness to relocate or
  travel, availability, notice period, acknowledgements, desired salary — plus free-text questions.
  These are answered as before.
- **Voluntary demographic questions** (gender, race, veteran status, disability) default to
  *Prefer not to say* unless your facts state otherwise.

Until 10.17 the answer prompt said, of citizenship and work authorisation and degrees and
clearances: *where the reference data is silent, still answer, and answer so that this application
stays eligible.* That produced confident claims nobody had checked, under a real person's name, to
employers — and it did not even work on its own terms, since an invented clearance fails the
background check it was invented to get past. The claim just fails later, with the applicant's name
on it.

Two things enforce this. The prompt asks for a sentinel value on anything the data does not settle;
and because a model that ignores an instruction is exactly the failure being defended against,
`ApplicationQuestionPolicy.Screen` independently checks that a factual answer has support in your
reference data before it is allowed near a form.

**This gets quieter with use.** Answering a held-back question in Quick answers writes it to the
profile's personal facts, so the next form that asks it is grounded and fills automatically. The
Profiles → personal data tab is where to front-load that.

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

The desktop app binds `http://127.0.0.1:8765`, loopback only, and starts **after** login. Requests
carry no credential and are served as whoever is signed in.

**Loopback is not the whole trust boundary, and it never was.** It keeps other machines out; it
does not keep a *browser* out, because every page the user visits can reach `127.0.0.1`, and a
`POST` with a safelisted content type arrives with no preflight to refuse. Until 10.17 this
listener answered `Access-Control-Allow-Origin: *`, which handed the reply back to whoever asked —
so any open tab could run the Word macro, synthesize a Ctrl+V into the foreground window, or (with
developer tools on) execute script in the signed-in ChatGPT browser. It now refuses any request
carrying a cross-origin `Origin` header, and echoes an origin back only when it is loopback. A
request with no `Origin` — curl, a script, the app itself — is still served.

| Endpoint | Purpose |
|---|---|
| `GET /health`, `GET /` | Liveness. |
| `GET /active-profile` | The active profile's resume prompt. |
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
| `DEVSTRIDER_LISTENER_PORT` | Listener port, default 8765 |
| `DEVSTRIDER_WORD_DOC_PATH` | Word template for the seeded *Default* profile |
| `DEVSTRIDER_WORD_HOTKEY` | Macro hotkey, default `F9` |
| `DEVSTRIDER_R2_*` | Account id, bucket, access key id, secret — see `SettingsBootstrap` |

**There is no `DEVSTRIDER_PORTAL_URL`.** Earlier revisions of this file listed one; it has never
existed in the code. The portal address is the compiled-in `PortalApi.Url`.

The six `DEVSTRIDER_SHARED_DB_*` variables are gone with the direct database connection they
configured, one of them a password — provisioning a machine no longer involves a secret.

There is no username variable: the account name comes from `app_user`, and no environment on any
machine gets to name a user.

## Upgrading from 7.x

Before you start, back up the machine's local MongoDB — after the migration the shared database is
the only copy of that person's bids and interviews.

**Settings do not carry across any more.** Earlier versions read the old MongoDB once on first
launch to copy saved values over; that import went in 9.3.0 with the rest of the MongoDB support,
so database credentials, R2 keys, the listener port and the Word path are entered once in Settings.
The MongoDB service can be stopped and uninstalled.

**Old bids do not carry across, by design.** There is no data migration in the app. A one-time
importer was built and then removed: it existed to solve a problem the folder back door already
solves without depending on anyone's old database still being installed, reachable, and holding
what they think it holds.

To get historical bidding onto the board, use **Bids → From folder…** — see
[Recording a day from resume folders](desktop/README.md#recording-a-day-from-resume-folders). It
reads the resume folders the Word macro already wrote to disk, which is a record that outlives any
database.

## Storage of credentials

`%LOCALAPPDATA%\DevStrider\settings.json` holds no secret any more. The shared-database password
that used to sit there in cleartext — one credential, shared by the whole team, with rights over
everyone's data — went with the direct connection in 10.0. What is left in that file is the portal
address, a listener port, and Word paths.

Two things are still worth knowing about:

- **`session.dat`**, beside it: the week-long bearer token, encrypted with DPAPI under your Windows
  account. It does not decrypt on another machine or for another user, but anything running *as
  you* can read it — that is unavoidable, since the app must. It is scoped to DevStrider and dies
  on its own within a week.
- **The Cloudflare R2 token**, still in cleartext in `settings.json`. A token with write permission
  can also delete, so every machine holding this file can wipe the bucket. Treat it accordingly.

## Version

**10.28.0** — see `<Version>` in `desktop/DevStrider.Desktop/DevStrider.Desktop.csproj`. The app
shows it in the title bar so you can tell at a glance whether a build picked up the latest source.
