# DevStrider ChatGPT UI Automation

Updated: 2026-08-21

## Product decision

DevStrider uses two persistent WebView2 workspaces: one for signed-in ChatGPT and one for signed-in
job sites. It does not call an OpenAI API and does not request an API key. Model and reasoning-effort
selection remain in the ChatGPT UI because those controls belong to the user's ChatGPT plan.

This is intentionally **assisted automation**. DevStrider can navigate, extract, prompt, generate,
fill, and upload after the user approves the queue. It always stops before a job site's final Submit
action. Missing JDs, sign-in/MFA/bot interstitials, protected questions, uncertain selectors, and
changed site markup are visible recovery points rather than reasons to guess.

## Implemented user experience

### One unified application queue

1. Paste one or many HTTP(S) job URLs in Application Queue.
2. DevStrider parses, normalizes, de-duplicates, and persists them for the active profile.
3. Choose **Approve & start automatic flow** once.
4. For each URL, DevStrider:
   - opens the job page;
   - detects Generic, Greenhouse, Ashby, Lever, ApplyToJob, or Teamtailor (including custom-domain career sites);
   - extracts visible JD text and unanswered, non-protected form questions;
   - opens an unambiguous Apply/Apply now/Start application control when the form is not already visible;
   - sends the JD to the persistent ChatGPT resume conversation;
   - asks ChatGPT for safe unanswered fields in a separate conversation when needed;
   - captures the resume as a local draft and runs the configured Word macro;
   - fills deterministic profile values, reusable answers, and ChatGPT answers;
   - uploads the generated PDF/DOC/DOCX when the configured output path resolves; and
   - pauses at **Ready for review**.
5. The user reviews every field and the resume, clicks Submit on the actual job site, then chooses
   **Mark submitted & next**. Only then does the local bid move from `draft` to `applied` and the
   next queued URL starts.

The URL input and ordinary action controls are unavailable while the automatic portion is active.
Manual and recovery tools remain available when automation is stopped or reaches a checkpoint.

### JD fallback uses the same pipeline

Some sites render the description in inaccessible frames or do not keep it on the application page.
When no usable JD is extracted, the work item enters `Needs JD` and a contextual paste box appears.
Pasting the JD and choosing **Continue automatic flow** resumes at resume generation; there is no
second workflow or duplicate permanent JD input.

### Recruiter-provided JD

Resume Studio contains only the recruiter case: an optional company/role label and a pasted JD.
Choosing **Generate resume** uses the same ChatGPT and Word engine but does not navigate a job page,
fill a form, upload a file, or create an application bid. The result path is reported when the Word
output convention is configured.

There is no permanent Job URL, duplicate generated-reply box, or Open ChatGPT button in Resume
Studio. ChatGPT is always loaded on the right. Raw prompt/reply controls appear only in Manual
recovery because the conversation itself is the canonical visible transcript.

## ChatGPT conversation rotation

A “resume session” means one fresh ChatGPT conversation, not one desktop session or one resume.

```text
fresh conversation: whole profile resume prompt + JD 1
same conversation:  JD 2
same conversation:  JD 3
...
same conversation:  JD N
rotate automatically: whole profile resume prompt + next JD
```

The default is 10 successful resumes per conversation. **Settings > Resume automation > Maximum
resumes per ChatGPT conversation** accepts 1–50. The count increments only after ChatGPT returns a
resume and Word succeeds. Profile changes reset the in-memory conversation counter. The app starts
a fresh conversation automatically at the limit; Manual recovery also offers an explicit reset.

This reduces repeated profile-prompt input while bounding the risk that a long conversation drifts
or damages the requested format. DevStrider does not claim exact token savings because the ChatGPT
website does not expose reliable request-token accounting to this workflow.

## Form adapters

`JobSiteFormAdapters` contains host-specific core selectors for:

- Greenhouse
- Ashby
- Lever
- ApplyToJob
- Teamtailor, including career sites hosted on a custom company domain
- Default/generic fallback for every other host

All adapters then use the guarded generic matcher for remaining fields. Matching considers visible
labels, accessible names, names, IDs, placeholders, and nearby legends. The value priority is:

1. active profile identity;
2. saved reusable answers;
3. answers generated for the current form.

Adapters dispatch `input`, `change`, and `blur` so controlled forms see changes. They skip hidden,
disabled, read-only, pre-filled, and file inputs during normal fill. They do not answer or change
legal/consent/signature, demographic, disability, veteran, salary/compensation, work-authorization,
sponsorship, or visa fields. Radio and checkbox changes require an explicit matching answer.

File upload uses WebView2's browser protocol because page JavaScript cannot assign a local file.
It inspects flattened DOM nodes, prefers resume/CV inputs, rejects likely cover-letter/photo inputs,
assigns only PDF/DOC/DOCX, and dispatches file-input notifications. If no suitable input or generated
file exists, the review checkpoint asks for manual attention. No adapter clicks Submit, Next,
Continue, CAPTCHA, MFA, or legal declarations. A narrowly matched Apply/Apply now/Start application
control may be opened so the form itself becomes available.

Live job sites change frequently. “Implemented” means the engine and current selector contracts are
present; it is not a guarantee that every provider variant or iframe works forever. Failures keep
the item recoverable and are recorded in Activity.

## Persistent state and recovery

Each queued item persists:

- URL, intent, timestamps, and detailed pipeline status;
- extracted JD and form questions;
- current answers and generated resume path;
- adapter, local bid ID, last error, and attempt count.

Statuses distinguish loading, extraction, missing JD, ChatGPT generation, document creation, form
fill, human review, submitted, failed, and skipped. Old `In progress`/`Completed` queue values migrate
to `Queued`/`Submitted` when loaded.

The two WebView controls are created once by the main window and hidden rather than recreated when
tabs change. ChatGPT and job sites use separate WebView2 data folders, preserving their own signed-in
state and avoiding the “initialized with a different CoreWebView2Environment” failure caused by
initializing the same control twice with conflicting environments.

Manual recovery includes fresh ChatGPT conversation, prepared-prompt copy, reply-from-clipboard,
Word retry, visible page/JD extraction, question extraction, field fill, resume selection/upload,
and queue retry/skip.

A failure no longer ends the batch. The failing link is recorded with its error and attempt count,
and the automatic flow continues to the next link, so one bad page cannot strand the rest of the run.
Three failures in a row still stop the queue, because a streak usually means the network, the machine,
or the profile is at fault rather than any single link. Collected failures appear in a Failed links
card offering three actions: requeue them all (attempt counts are kept, so a link that keeps failing
stays identifiable), copy them out with their errors, or remove them from the queue.

## Resume output settings

Automatic upload needs the same output convention as the Word macro:

- `ResumeOutputRoot`: parent directory containing the generated fast-feed folder;
- `ResumeOutputFileBase`: filename without extension, default `Resume`.

DevStrider looks for `<root>\<fast-feed folder>\<base>.pdf`, then `.docx`, then `.doc`. If no path
is configured or no file exists, Word generation can still succeed; upload becomes a manual review
item.

## Job Operations assisted automation

Job Operations provides scoped prompts for **Check inbox**, **Check calendar**, or **Review both**.
The user runs the copied prompt in signed-in ChatGPT, where Gmail/Calendar access depends on the
connections and capabilities the user enabled. ChatGPT returns structured proposals; DevStrider
does not receive Gmail or Calendar credentials.

DevStrider requires concise source evidence and exact company + role matching. The user reviews and
selects every local data change. Supported local actions are:

- `update_bid_status`: screening, phone screening, interview, offer, or rejected;
- `mark_bid_rejected`: compatibility shortcut;
- `create_interview`: validated interview type, schedule, link, and evidence;
- `update_interview_status`: scheduled, completed, passed, failed, or cancelled.

`draft_reply` and `calendar_conflict` are displayed as review-only suggestions. This build does not
send email, create/change external calendar events, poll in the background, or silently apply a
proposal. Official OpenAI examples describe ChatGPT using connected Gmail to search/triage mail and
prepare drafts; actual availability remains account/workspace dependent:
https://learn.chatgpt.com/use-cases/manage-your-inbox

## Safety and correctness boundaries

- No OpenAI API key or ChatGPT password is stored by DevStrider.
- ChatGPT UI automation is best-effort and can break when the website DOM changes.
- Generated content and proposed operational changes are untrusted until reviewed.
- Exact bid/interview matching and explicit selection are required for local Job Operations writes.
- Final job submission is always a direct human action on the job site.
- CAPTCHA, MFA, login, protected fields, and ambiguous controls always remain human work.
- Only a *rendered* challenge counts as a CAPTCHA. Greenhouse and Ashby load score-based reCAPTCHA on
  every application, which renders a hidden `grecaptcha-badge` and an invisible anchor frame that no
  human ever touches; treating those as a challenge would gate every application on both providers.
  A real challenge is an advisory on the review checkpoint, not a blocker: the engine never submits,
  so the challenge is the user's to solve at submit time and filling can finish first.
- Existing saved form values are never overwritten by one-time ChatGPT answers unless the user
  explicitly saves them as reusable answers.

## Implementation map

| Concern | Code |
|---|---|
| Unified queue and state machine | `Models/AppSettings.cs`, `ViewModels/JobBrowserViewModel.cs` |
| Job WebView orchestration and upload | `Views/JobBrowserView.xaml`, `Views/JobBrowserView.xaml.cs` |
| Site-aware field mapping | `Services/JobSiteFormAdapters.cs` |
| Resume conversation rotation and Word handoff | `ViewModels/ResumeStudioViewModel.cs` |
| Persistent ChatGPT automation | `Views/ResumeStudioView.xaml`, `Views/ResumeStudioView.xaml.cs` |
| Cross-workspace coordination | `ViewModels/MainWindowViewModel.cs`, `Views/MainWindow.xaml` |
| Job Operations proposals | `ViewModels/AssistedAutomationViewModel.cs`, `Views/AssistedAutomationView.xaml` |
| Limits and Word output convention | `Models/AppSettings.cs`, `ViewModels/SettingsViewModel.cs`, `Views/SettingsView.xaml` |

## Verification status

- Desktop project: builds successfully on .NET 10 Windows.
- Static adapter contracts: implemented for the five requested adapter choices plus Teamtailor custom-domain detection.
- Signed-in live forms and current ChatGPT DOM: require end-to-end verification by the user because
  they depend on private accounts, provider-specific forms, and changing third-party markup.
- Build warnings still identify vulnerable transitive `SharpCompress` and `Snappier` versions plus
  older graphics-package compatibility warnings; dependency remediation is separate from this
  workflow implementation.
