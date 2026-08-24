# DevStrider ChatGPT UI Automation

Updated: 2026-08-23

## Product decision

DevStrider uses two persistent WebView2 workspaces: one for signed-in ChatGPT and one for signed-in
job sites. It does not call an OpenAI API and does not request an API key. Model and reasoning-effort
selection remain in the ChatGPT UI because those controls belong to the user's ChatGPT plan.

This is **user-approved automatic application processing**. After the user approves the queue,
DevStrider can navigate, extract, prompt, generate, fill, upload, and submit through the selected
job-site adapter. Missing JDs, sign-in/MFA/bot interstitials, protected questions, uncertain
selectors, changed site markup, and submission results that cannot be confirmed remain visible
recovery points rather than reasons to guess.

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
   - validates the visible step with the selected apply adapter and advances intermediate
     Next/Continue actions;
   - clicks final Submit through WebView2's browser input pipeline;
   - captures any site-rendered errors for one targeted ChatGPT correction and resubmits; and
   - marks the bid `applied` and opens the next URL when the site confirms submission.
5. If the site shows neither a readable validation error nor a reliable confirmation, the item pauses
   at **Ready for review**. The user can inspect the visible result, submit manually if necessary,
   and choose **Mark submitted & next**.

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

The URL is resolved specific-first through `JobSiteApplyAdapters`. Each specific apply adapter owns
its application-entry selectors, action selectors, and error selectors. If no host matches, the
Default adapter is selected automatically. `JobSiteFormAdapters` supplies the corresponding
host-specific core field selectors and the guarded generic field matcher.

Implemented apply adapters are:

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

Adapters type every normal text input and textarea character by character through WebView2 browser
input, including long GPT prose answers, and move focus with Tab so controlled forms retain and validate the values. They
skip disabled, read-only, pre-filled, and file inputs during normal fill. Government identifiers and
financial-account details are never filled. Other sensitive questions require an explicit saved or
GPT-returned answer; the app does not invent one. Settings > Application defaults provides an optional
salary/compensation expectation, including currency and period. When it is blank, salary questions stay
unanswered for human review.

Choice extraction covers native radio/checkbox groups, visually hidden native radios with visible
labels, semantic `[role="radio"]` controls, and grouped single-select buttons using `aria-pressed`.
For Ashby native radios without a `fieldset` or ARIA group, the surrounding one-question
`data-field-entry-id`/`data-field-path` element is the group boundary. Long eligibility and
sponsorship questions retain their explanatory notes rather than being truncated in the GPT prompt.
The complete option set is attached to the exact question sent to ChatGPT. Filling matches the returned
answer to one exact visible option, measures its viewport coordinates, and commits it through
WebView2's browser-level mouse input. The same selected-state check is used by extraction, filling,
and final validation, so one chosen option marks the whole group answered.

Ashby yes/no fields are a special button-group shape: two `aria-pressed` buttons and one hidden
checkbox sit below the question label without a semantic group role. The adapter groups the buttons
by their smallest shared option container, resolves the question from the surrounding
`data-field-entry-id`/`data-field-path` field entry, and ignores the checkbox because it is only the
widget's mirrored state. Consequently ChatGPT receives exactly `Yes` and `No`, and filling must click
the matching visible button.

Native and custom dropdowns contribute their available options to the ChatGPT question prompt.
Every custom dropdown is activated and its final exact option is committed through browser-level
mouse input; keyboard-backed fallbacks use browser-level Enter. The app then focuses the search input
revealed by that interaction and polls while asynchronously rendered options mount. It collects the complete,
unfiltered focused menu before typing any candidate; candidate typing is only a fallback for backend
autocomplete controls that return no choices until searched. Menus are scanned through their scroll
range so virtualized choices are included. During fill, the adapter activates the control the same
way, chooses an exact returned option, and confirms the control renders that choice; leftover dropdown
search text is not counted as a fill.

Dropdown ownership is deliberately strict. Options are read only from the activated control's
`aria-controls`/`aria-owns` menu or a menu that became visible because of that activation; ordinary
page list items are never used as options. Extraction stops after repeated reads return a stable option
set even if a site reports a scroll range that does not advance, then sends Escape, blur, and a neutral
outside click before opening the next dropdown. This prevents Greenhouse controls from remaining open
and blocking the following field. Placeholder values such as `No options` are not sent as autocomplete
search terms.

Resume upload runs before text entry because some controlled forms rerender after a file changes.
For Ashby, static select candidates come from the job page's read-only application-form schema because
its rendered menus do not consistently expose listbox/option roles. Autocomplete controls without a
finite static list are queried using the candidate's saved value and their returned suggestions are
attached to the question.

Application fields use one bounded two-pass correction. The primary pass types the best grounded
values from personal/reference data and the first ChatGPT answer, then physically clicks the visible
Next or final Submit action. Only errors rendered by the job site after that click enter the second
pass. The adapter's DOM outstanding-field scan remains diagnostic and never adds speculative questions
to the GPT correction payload. Each rejected question carries its primary answer, the exact site error,
and any options gathered from the live control. ChatGPT must choose an exact supplied option when
options exist; otherwise it returns a corrected grounded value that addresses the failure. Corrected
answers are merged into the work item and only failed fields are refilled. The failed-question inventory
remains persisted until that refill begins, so a correct-looking old DOM value cannot cause the control
to be skipped. Rejected grouped choices are deliberately transitioned to another option and back to
the requested answer; text and dropdown fields are cleared/recommitted through browser input. There is no retry loop: one
correction pass is allowed, then the application is resubmitted. It returns to human review only when
fields remain unresolved or the job site's submission outcome cannot be confirmed.

Controlled text fields are processed sequentially rather than in one browser task. After each blur,
the host waits for the job site's asynchronous state update, reads the field back, and retries once
if the value did not persist. A delayed final scan identifies anything still empty before submission.

File upload uses WebView2's browser protocol because page JavaScript cannot assign a local file.
It inspects flattened DOM nodes, prefers resume/CV inputs, rejects likely cover-letter/photo inputs,
assigns only PDF/DOC/DOCX, and dispatches file-input notifications. If no suitable input or generated
file exists, the review checkpoint asks for manual attention.

After filling, the selected apply adapter calls the form's browser validation and reads its
site-specific rendered errors. It locates intermediate Next/Continue and final Submit actions, while
the host clicks their visible coordinates using WebView2 browser-level mouse events. After final
Submit, the app polls for asynchronously rendered React errors and sends structured question/message
pairs directly to the second ChatGPT answer pass. Corrected controls are committed and submitted once
more. A site confirmation—or disappearance of the form after the bounded confirmation delay—completes
the queue item; an ambiguous result pauses for review with a read-only validation observer still
active. A narrowly matched Apply/Apply now/Start application control may be opened so the form itself
becomes available. CAPTCHA and MFA remain human recovery points.

Live job sites change frequently. “Implemented” means the engine and current selector contracts are
present; it is not a guarantee that every provider variant or iframe works forever. Failures keep
the item recoverable and are recorded in Activity.

## Persistent state and recovery

Each queued item persists:

- URL, intent, timestamps, and detailed pipeline status;
- extracted JD and form questions;
- current answers and generated resume path;
- the ChatGPT answer-conversation ID and reopenable `/c/...` URL;
- second-pass attempt count and any pending correction questions;
- adapter, local bid ID, last error, and attempt count.

Statuses distinguish loading, extraction, missing JD, ChatGPT generation, document creation, form
fill, human review, submitted, failed, and skipped. Old `In progress`/`Completed` queue values migrate
to `Queued`/`Submitted` when loaded.

The answer-conversation URL is saved as soon as ChatGPT assigns it, before Word generation begins.
A dynamic-field correction always returns to that exact conversation. If the process is restarted
and the queue item is retried, its question phase also reopens the persisted answer conversation
instead of silently creating a different chat without the earlier application-answer context.

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
