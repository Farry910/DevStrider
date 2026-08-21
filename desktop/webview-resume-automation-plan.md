# DevStrider WebView and Assisted Automation

Updated: 2026-08-21

## Decision

This release uses the signed-in ChatGPT website inside WebView2. It does not call an
OpenAI model endpoint and does not require an OpenAI API key. A user signs in to ChatGPT
in the embedded browser and uses the features available to that account.

DevStrider does not automate or scrape ChatGPT's private page structure. Prompt and result
transfer is intentionally user-driven through copy and paste. This is called **assisted
automation**: the app prepares context, validates structured results, and performs explicit
local actions after user review.

Model and reasoning-effort selection remain in ChatGPT's own UI. DevStrider cannot reliably
select or guarantee those options without a supported application interface.

## Current implementation status

| Area | Status | Current behavior |
|---|---|---|
| Embedded ChatGPT | Implemented | Persistent WebView2 profile with the user's signed-in ChatGPT session. |
| Reusable resume sessions | Implemented | One profile prompt is copied once, followed by several JDs in the same conversation. Default limit is 5; per-profile range is 1-10. |
| One-click bid handoff | Implemented, assisted | Start bid extracts the current JD, prepares the ChatGPT prompt, switches to Resume Studio, and focuses ChatGPT. |
| Automatic ChatGPT bid flow | Implemented, opt-in | The embedded ChatGPT page receives the prompt, sends it, waits for a new reply, saves the draft, and runs Word; job-site submission remains manual. |
| Resume result handling | Implemented, assisted | User pastes ChatGPT's complete response into Resume Studio; DevStrider saves a draft and parses existing fast-feed metadata when present. |
| Word resume generation | Implemented | User can run the Word macro explicitly, or enable the per-profile automatic macro toggle after a draft is saved. |
| Job browser | Implemented | Separate persistent WebView2 profile for signed-in job sites, visible-text extraction, and URL-based adapter selection. |
| Application-link queue | Implemented | Per-profile local queue accepts pasted job URLs and opens one active application at a time. |
| Default form adapter | Initial implementation | Best-effort matching by labels, accessible names, names, IDs, and placeholders. It intentionally skips uncertain and protected fields. |
| Greenhouse adapter | Initial implementation | Host detection and selectors for core candidate fields, with generic matching for reviewed custom answers. |
| Ashby adapter | Initial implementation | Host detection and selectors for core candidate fields, with generic matching for reviewed custom answers. |
| Lever adapter | Initial implementation | Host detection and selectors for core candidate fields, with generic matching for reviewed custom answers. |
| ApplyToJob adapter | Initial implementation | Host detection and selectors for core candidate fields, with generic matching for reviewed custom answers. |
| Resume file upload | Implemented, explicit | User chooses a local PDF/Word file and clicks Upload. DevStrider targets the most likely resume file input through WebView2's browser protocol. |
| Gmail/Calendar workflow | Implemented, assisted | ChatGPT reviews the connections authorized in its UI; the user pastes structured proposals into DevStrider. |
| Proposal application | Implemented | Required evidence and exact company + role matching; selected actions can reject a bid or create an interview. |
| Application submission | Not implemented | DevStrider never clicks Submit or advances the application. |
| Live-site adapter certification | Not completed | The code compiles, but every supported site still needs fixture and signed-in live-form verification. |
| Durable automation audit | Not completed | Relevant actions appear in the in-memory Activity feed; there is no immutable database audit table yet. |
| Dependency hygiene | Requires follow-up | The build reports known vulnerabilities in transitive `SharpCompress` and `Snappier` packages, plus older graphics-package compatibility warnings. |

“Initial implementation” is deliberate wording. Job sites change markup and frequently use
custom controls, frames, and multi-step forms. The adapters are useful starting points, not a
claim that every form variant is covered.

## Implemented workflow

```text
Profile prompt ──copy once──> signed-in ChatGPT conversation
     JD 1..N ─────copy───────> same conversation
ChatGPT reply ───paste───────> basic check/save draft ──> optional Word macro

Job page ──extract──> reviewed questions ──copy──> ChatGPT
ChatGPT JSON ──paste──> current answers ──review──> site adapter fills fields
Chosen resume file ──explicit upload──────────────> detected resume input

Job links from another app ──paste──> per-profile queue ──open next──> Job Browser

Gmail/Calendar in ChatGPT ──JSON proposals──> exact bid match ──user selection──> update
```

The ChatGPT and job-site WebViews use separate local WebView2 data folders. This keeps each
browser session persistent without placing ChatGPT credentials in DevStrider settings.

## Resume Studio

### One-click bid handoff

1. In Job Browser, **Start bid** extracts the visible JD and opens Resume Studio.
2. For the first job, DevStrider copies the profile prompt plus JD together; later jobs copy
   only the new JD into the same ChatGPT conversation.
3. The user pastes that clipboard content into ChatGPT and copies ChatGPT's completed reply.
4. **Finish from clipboard** saves the draft directly from that copied reply and runs Word
   automatically when the macro toggle is enabled.

This removes the extra manual transfer between Job Browser and Resume Studio. The ChatGPT paste
and copy actions can be automated by enabling **Automate ChatGPT bid flow and finish Word
resume**. This mode uses visible-page selectors and reports a clear failure if ChatGPT is signed
out or its page layout changes. Job-site submission remains manual.

1. The active profile supplies the whole resume prompt.
2. **Start session** copies that prompt and resets the saved-resume counter.
3. The user pastes it into one ChatGPT conversation.
4. For each job, **Copy JD for ChatGPT** copies the new description.
5. The user pastes the response into Resume Studio and reviews it.
6. **Save draft** records the content with bid status `Draft`.
7. The Word macro runs only when the user clicks it or enables **Automatically generate
   Word resume after validation**. At present, that validation is limited to non-empty resume
   content; structural validation is listed below as remaining work.

The session limit counts saved drafts, not messages sent to ChatGPT. When the limit is
reached, Resume Studio blocks another JD/save until the user starts a new session. A profile
change also resets the session. The app does not calculate token use because the ChatGPT UI
does not expose reliable request token accounting to this workflow.

## Job Browser and adapters

### Application-link queue

DevStrider does not need to search job boards. Paste one or more HTTP(S) job links from the
separate job-gathering app, one per line, and save them to the active profile's local queue.
**Open next** starts or returns to the one in-progress link; after the user has finished their
manual application review, **Mark completed** or **Skip** records the result and unlocks the
next queued link. Queue items survive restart and do not mark a bid as submitted by themselves.

### Value priority

The fill payload is assembled in this order, with later values overriding earlier ones:

1. Active profile: full/first/last name, personal email, phone, location, LinkedIn URL,
   and headline.
2. Per-profile saved answers for repetitive questions.
3. Current application answers pasted from ChatGPT as JSON.

The accepted answer format is either a direct JSON object or:

```json
{
  "answers": {
    "exact question text": "reviewed answer"
  }
}
```

### Fill behavior

- Detect the adapter from the current URL.
- Try site-specific selectors for core candidate fields first.
- Use normalized visible labels and accessible attributes for remaining reviewed answers.
- Dispatch `input`, `change`, and `blur` events so controlled form code sees updates.
- Skip hidden, disabled, read-only, pre-filled, and file controls during normal field fill.
- Skip declarations involving agreements, attestation, certification, consent, privacy,
  signatures, terms, or truthfulness.
- Do not click Submit, Continue, Next, or CAPTCHA controls.
- Record successful fills and failures in Activity.

Checkboxes and radio buttons are changed only when the reviewed answer has an explicit
boolean or matching option value. Users must review every populated form before submission.

### Resume upload

File upload is a separate explicit action because browsers do not allow normal page
JavaScript to assign a local path. The user chooses the exact file in a native file picker,
then clicks **Upload selected resume**. DevStrider uses the WebView2 browser protocol to:

1. inspect file inputs, including flattened frame/shadow-DOM nodes where available;
2. prefer controls described as `resume` or `CV` and avoid likely cover-letter controls;
3. assign only the file the user selected; and
4. dispatch input/change notifications for the page.

If no likely input is found, the app fails closed and asks for manual upload. Custom upload
widgets and cross-origin frame variations still require live-site testing.

## Gmail and Calendar assisted automation

1. The user authorizes Gmail and Calendar in their ChatGPT account.
2. DevStrider copies a constrained review prompt.
3. The user runs it in ChatGPT and pastes the returned JSON into Job Operations.
4. DevStrider requires source evidence, accepts only supported actions and interview types,
   and exact-matches one bid by company + role.
5. Unmatched or ambiguous items cannot be applied.
6. The user selects proposals and applies them explicitly.

Currently supported actions:

- `mark_bid_rejected`
- `create_interview`

An interview stores the supplied evidence in its user comment. Outbound email, automatic
calendar booking, background inbox polling, and automatic proposal application are not
implemented in the UI-only workflow.

## Safety boundaries

- No OpenAI API key is requested or stored.
- ChatGPT login state remains in WebView2's local browser profile.
- Generated and ChatGPT-assisted content is untrusted until reviewed.
- Legal declarations and final submission always remain manual.
- Resume upload requires a file chosen by the user and a separate upload click.
- Current-answer JSON affects only the current form unless the user explicitly saves it.
- Site markup failure must skip fields rather than guess.

## Implementation map

| Concern | Main code |
|---|---|
| Resume session and macro toggle | `ViewModels/ResumeStudioViewModel.cs`, `Views/ResumeStudioView.xaml` |
| ChatGPT WebView | `Views/ResumeStudioView.xaml.cs` |
| Assisted proposals | `ViewModels/AssistedAutomationViewModel.cs`, `Views/AssistedAutomationView.xaml` |
| Job form values | `ViewModels/JobBrowserViewModel.cs` |
| Adapter scripts | `Services/JobSiteFormAdapters.cs` |
| Job WebView, fill, and upload | `Views/JobBrowserView.xaml.cs`, `Views/JobBrowserView.xaml` |
| Per-profile preferences | `Models/AppSettings.cs` |
| Word output | `Services/WordMacroService.cs` |

## Remaining implementation plan

### P0: dependency security

1. Identify the direct packages that introduce `SharpCompress` and `Snappier`.
2. Upgrade through supported direct-package versions and run database/import regression tests.
3. Re-run the vulnerable-package report and do not suppress the warnings without remediation.

### P0: verify adapters before calling them production-ready

1. Save sanitized HTML fixtures for each supported provider and major form variant.
2. Add tests for URL detection, selector fallback, protected-field skipping, select/radio/
   checkbox handling, and missing controls.
3. Test core fill and resume upload on signed-in live forms without submitting them.
4. Record the tested host, form variant, date, and observed limitations.

### P1: improve form coverage

1. Move each provider into a separate adapter class with a version and selector contract.
2. Handle provider-specific React select/autocomplete components and multi-step forms.
3. Show a pre-fill preview mapping each answer to its target field and confidence.
4. Let the user deselect individual fields before fill.
5. Detect page changes and warn when a previously healthy selector contract stops matching.

### P1: strengthen result validation

1. Replace trailing free-text metadata with a versioned structured resume envelope.
2. Validate required resume sections before saving or running Word.
3. Show field-level validation errors and retain the pasted draft on failure.
4. Distinguish generated, validated, Word-created, and submitted states explicitly.

### P1: durable audit and recovery

1. Add an append-only database audit table for assisted proposals, matching decisions,
   field-fill summaries, uploads, macro runs, and applied actions.
2. Store source evidence metadata without storing full email bodies by default.
3. Add idempotency keys so retrying an action cannot duplicate an interview.
4. Add undo where the underlying operation is safely reversible.

### P2: broader communication actions

Add more reviewed actions only after schemas and confirmation UI exist, for example bid
stage changes, interview rescheduling, and reply drafts. Keep sending messages and booking
or changing external events behind explicit user confirmation in this architecture.

## Acceptance criteria for the current release

- The desktop project builds successfully.
- A ChatGPT sign-in persists across Resume Studio navigation and app restarts.
- A job-site sign-in persists in the Job Browser profile.
- Session limit and automatic Word-macro preference persist per profile.
- Saving a resume creates/updates a draft and never marks it submitted.
- Current ChatGPT answers override saved answers without overwriting them.
- Protected/legal fields and submit controls remain unchanged.
- Upload uses only the explicitly selected file and reports when no suitable input exists.
- Assisted actions cannot apply without one exact bid match and explicit selection.
- Greenhouse, Ashby, Lever, ApplyToJob, and generic fixtures pass before a production-ready
  adapter claim is made.
