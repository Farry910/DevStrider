using System.Text.Json;

namespace DevStrider.Desktop.Services;

/// <summary>
/// Host-aware form filler. The JavaScript deliberately never submits forms or assigns file inputs.
/// Specific adapters provide stable core-field selectors; every other question uses the guarded
/// label/accessible-name matcher as a low-confidence fallback.
/// </summary>
public static class JobSiteFormAdapters
{
    public static string NameFor(Uri uri) => JobSiteApplyAdapters.Resolve(uri).Name;

    /// <summary>
    /// Label matching and value lookup, shared by the fill pass and the combobox pass so one rule
    /// decides what a control is asking and which saved answer belongs in it. Expects a
    /// <c>__PAYLOAD__</c> placeholder carrying <c>{ adapter, values }</c>.
    /// </summary>
    private const string MatchingPrelude = """
 const payload = __PAYLOAD__;
 // Accents are folded, not deleted. Stripping them as punctuation turned "Cuautitlán" into
 // "cuautitl n", so a typed "Cuautitlan" matched none of the suggestions a Mexican city returns
 // and the pick never happened.
 const norm = s => String(s || '').normalize('NFD').replace(/[̀-ͯ]/g, '')
   .toLowerCase().replace(/[^a-z0-9]+/g, ' ').trim();
 const visible = e => !!(e && (e.offsetWidth || e.offsetHeight || e.getClientRects().length));
 // Only what the user has said this app is never given. Everything else — demographics, salary,
 // work authorisation, sponsorship, legal attestations, date of birth — is answerable, because the
 // user supplies those answers themselves in Job Operations; they are simply never invented.
 const protectedField = text => /\b(social security|ssn|passport|national id|id number|identity number|driver licen|bank account|routing number|sort code|iban|credit card|card number|tax id|nino)\b/.test(text);
 const attr = (e, name) => e.getAttribute(name) || '';
 // Anything that would hand the click to a file input: the input itself, a wrapper containing one,
 // or a label bound to one. A native file dialog stops the whole run until a human closes it.
 const opensFilePicker = node => {
   if (!node || !node.tagName) return false;
   if (node.tagName === 'INPUT' && node.type === 'file') return true;
   if (node.querySelector && node.querySelector('input[type="file"]')) return true;
   const label = node.closest && node.closest('label');
   if (label && (label.control?.type === 'file' || label.querySelector('input[type="file"]'))) return true;
   const forId = label ? label.getAttribute('for') : '';
   return !!forId && document.getElementById(forId)?.type === 'file';
 };
 const labelledBy = e => attr(e, 'aria-labelledby').split(/\s+/).filter(Boolean)
   .map(id => document.getElementById(id)?.innerText || '').join(' ').trim();
 // Choice groups are not always native radio inputs. Greenhouse and other React forms also use
 // role=radio, aria-pressed buttons, and invisible native radios whose visible labels are clicked.
 const choiceSelector = 'input[type="radio"],input[type="checkbox"],[role="radio"],'
   + '[role="radiogroup"] button,[role="radiogroup"] [role="button"],button[aria-pressed],[role="button"][aria-pressed]';
 const nativeChoice = e => e?.type === 'radio' || e?.type === 'checkbox';
 const buttonChoice = e => !!e && (e.getAttribute('role') === 'radio'
   || e.hasAttribute('aria-pressed')
   || (!!e.closest('[role="radiogroup"]') && e.matches('button,[role="button"]')));
 const choice = e => nativeChoice(e) || buttonChoice(e);
 const choiceGroup = e => {
   const semantic = e?.closest('fieldset,[role="radiogroup"],[role="group"],[data-question],'
     + '[class*="yesno" i],[class*="button-group" i],[class*="option-group" i]');
   if (semantic) return semantic;
   // Ashby's yes/no container has no group role. Its two aria-pressed buttons and a hidden state
   // checkbox are siblings, so the smallest ancestor with at least two button choices is the group.
   for (let node = e?.parentElement, depth = 0; node && depth < 3; node = node.parentElement, depth++) {
     const buttons = Array.from(node.querySelectorAll('button[aria-pressed],[role="radio"],'
       + '[role="radiogroup"] button,[role="radiogroup"] [role="button"]')).filter(buttonChoice);
     if (buttons.length >= 2) return node;
   }
   // Some Ashby ValueRadio fields are plain same-name native radios inside a field entry: no
   // fieldset, radiogroup role, or option-group class. The field entry is still authoritative and
   // contains one question, so it is the correct group boundary for this otherwise invisible shape.
   if (nativeChoice(e)) {
     const fieldEntry = e.closest('[data-field-entry-id],[data-field-path],.ashby-application-form-field-entry');
     if (fieldEntry) {
       const sameName = Array.from(fieldEntry.querySelectorAll('input[type="radio"],input[type="checkbox"]'))
         .filter(option => !e.name || option.name === e.name);
       if (sameName.length >= 2) return fieldEntry;
     }
   }
   return null;
 };
 const choiceVisible = e => visible(e) || (nativeChoice(e) && Array.from(e.labels || []).some(visible));
 const choiceSelected = e => nativeChoice(e) ? !!e.checked
   : attr(e,'aria-checked') === 'true' || attr(e,'aria-pressed') === 'true'
     || attr(e,'aria-selected') === 'true' || attr(e,'data-selected') === 'true'
     || attr(e,'data-checked') === 'true'
     || /^(checked|on|selected|active)$/i.test(attr(e,'data-state'))
     || /(^|\s)(checked|selected|active)(\s|$)/i.test(String(e.className || ''));
 const choiceOptions = e => {
   const group = choiceGroup(e);
   const grouped = group ? Array.from(group.querySelectorAll(choiceSelector)).filter(choice) : [];
   // When visible buttons mirror one hidden checkbox, only the buttons are user-selectable options.
   const buttons = grouped.filter(buttonChoice);
   if (buttons.length >= 2) return buttons;
   if (nativeChoice(e) && e.name) {
     const named = Array.from(document.getElementsByName(e.name)).filter(choice);
     if (named.length) return named;
   }
   return grouped.length ? grouped : [e];
 };
 const mirroredChoice = e => nativeChoice(e) && choiceOptions(e).some(buttonChoice);
 const choiceLabel = e => String(attr(e,'aria-label') || attr(e,'data-value')
   || Array.from(e.labels || []).map(label => label.innerText).join(' ')
   || e.innerText || e.value || '').replace(/\s+/g,' ').trim();
 // ONE authoritative label, taken from the strongest association the page actually commits to.
 // Never widen to "the first label inside some ancestor div": on a two-column form that is the
 // neighbouring field's label, and because the old version concatenated every candidate into a
 // single haystack, the wrong one won. That is how a Country dropdown ends up holding a phone
 // number and "how many years of experience" ends up holding a LinkedIn URL.
 // A radio or checkbox's own label is the option, not the question — "Brainstorming, idea finding,
 // customer validation" is an answer to "What stage are you at in building?". The question lives on
 // the group, and only there: widening to any ancestor is what used to pick up a neighbour's label.
 const groupQuestion = e => {
   if (!choice(e)) return '';
   const group = choiceGroup(e);
   if (!group) return '';
   const host = group.closest('[data-field-entry-id],[data-field-path],.ashby-application-form-field-entry')
     || group;
   const directLabel = Array.from(host.children || []).find(node =>
     node.matches?.('label,legend,[data-question-label],.ashby-application-form-question-title'));
   return String(labelledBy(group) || labelledBy(host)
     || group.querySelector('legend,[data-question-label]')?.innerText
     || directLabel?.innerText || attr(group, 'aria-label') || attr(host, 'aria-label') || '').trim();
 };
 // Two controls are the same field when they are the same element, or two options of one radio or
 // checkbox group. Group membership is the name attribute, which is what makes them one answer.
 const sameField = (a, b) => a === b ||
   (!!a.name && a.name === b.name && nativeChoice(a) && nativeChoice(b)) ||
   (choice(a) && choice(b) && !!choiceGroup(a) && choiceGroup(a) === choiceGroup(b));
 // The wrapper holding this field and nothing else fillable. The walk stops at the first ancestor
 // that also contains a *different* control, so a neighbour's label can never be reached — that is
 // the precise version of the "first label in some ancestor div" rule that 9.1.5 had to delete for
 // putting a phone number in a Country dropdown. Markup-agnostic: no fieldset or label[for] needed,
 // which is what the previous fix wrongly assumed every board provides.
 const fieldBlock = e => {
   let best = null;
   for (let node = e.parentElement; node && node !== document.body; node = node.parentElement) {
     const controls = Array.from(node.querySelectorAll(
       'input:not([type="hidden"]):not([type="submit"]):not([type="button"]),textarea,select,[role="combobox"],'
       + choiceSelector));
     if (!controls.every(control => sameField(control, e))) break;
     best = node;
   }
   return best;
 };
 // The question text inside that wrapper: any label-ish element that is not an option's own label.
 const blockLabel = e => {
   const block = fieldBlock(e);
   if (!block) return '';
   const optionLabels = new Set(Array.from(block.querySelectorAll('input,textarea,select'))
     .flatMap(control => Array.from(control.labels || [])));
   const node = Array.from(block.querySelectorAll('label,legend,[class*="label" i],[class*="title" i],[class*="question" i]'))
     .find(candidate => !optionLabels.has(candidate) && !candidate.querySelector('input,textarea,select')
       && String(candidate.innerText || '').trim().length > 1);
   return node ? String(node.innerText).replace(/\s+/g, ' ').trim() : '';
 };
 const association = e => {
   const forLabel = e.id ? document.querySelector(`label[for="${CSS.escape(e.id)}"]`)?.innerText : '';
   const grouped = choice(e);
   // An option's own label is the answer, never the question, so a grouped control skips straight
   // past label[for] and its own <label> to whatever names the group.
   const own = !grouped && e.labels && e.labels.length
     ? Array.from(e.labels).map(x => x.innerText).join(' ') : '';
   return groupQuestion(e) || attr(e, 'description')
     || (grouped ? '' : labelledBy(e) || forLabel || own || attr(e, 'aria-label'))
     || blockLabel(e)
     || (grouped ? '' : e.placeholder) || attr(e, 'name') || e.id || '';
 };
 const textFor = e => norm(association(e));
 // react-select and friends render a real <input> as their search box. Typing into it looks like a
 // successful fill and selects nothing, so it belongs to the combobox pass, not the text pass.
 // A text box that will not keep a typed value: it offers a list once you type, and writes the real
 // answer into a hidden field only when an option is picked. Lever's Current location is the one
 // that forced this. It looks like an ordinary <input type="text"> — no combobox role, no
 // aria-autocomplete — so it went to the text pass, which typed into it, watched Lever clear it on
 // blur, reported "value did not persist", and left the application to be rejected for an empty
 // required field on every attempt.
 //
 // The tell is the hidden partner. A field block holding one visible text input beside a hidden
 // input whose name says it holds the *selected* value is a control that demands a choice, whatever
 // it looks like. That is markup-shaped rather than site-shaped, so it reads the same pattern
 // wherever it appears.
 const suggestionPartner = e => {
   if (!e || e.tagName !== 'INPUT') return null;
   const type = (e.type || 'text').toLowerCase();
   if (type !== 'text' && type !== 'search') return null;
   const block = e.closest('[class*="field" i],[data-field],[data-field-entry-id],fieldset,li');
   if (!block) return null;
   return Array.from(block.querySelectorAll('input[type="hidden"]'))
     .find(hidden => /select|chosen|resolved/i.test((hidden.name || '') + ' ' + (hidden.id || ''))) || null;
 };
 const typeaheadInput = e => !!suggestionPartner(e);
 const comboInput = e => e.getAttribute('role') === 'combobox'
   || e.getAttribute('aria-autocomplete') === 'list'
   || /(^|\s)select__input(\s|$)/.test(String(e.className || ''))
   || typeaheadInput(e);
 const comboSelector = '[role="combobox"],[aria-haspopup="listbox"],[aria-autocomplete="list"],'
   + '[data-radix-select-trigger],[data-headlessui-listbox-button],[data-reach-listbox-button]';
 // The rendered chosen value of a custom dropdown, found by walking up from the control instead of
 // guessing at one ancestor. react-select puts the value in .select__value-container while the input
 // sits in .select__input-container beneath it, so every "nearest select-ish ancestor" guess landed
 // one level too low and found nothing. Dropdowns that were correctly set therefore reported as
 // unanswered everywhere at once: the verify probe warned "not confirmed, leaving it for review",
 // the outstanding list kept them, and the plan re-drove them on the next pass. The parked tab in a
 // real run had select__single-value reading "Cuautitlán Izcalli, México, Mexico" while the trace
 // insisted that field was empty. The walk stops before an ancestor holding a second combobox, so
 // it can never read the value belonging to the dropdown next door.
 const chosenValueFor = control => {
   // A suggestion field's answer lives in its hidden partner, not in a rendered value element.
   const partner = suggestionPartner(control);
   if (partner) return String(partner.value || '').trim().length > 0 ? partner : null;
   let node = control && control.parentElement;
   for (let up = 0; up < 6 && node; up++) {
     if (node.querySelectorAll(comboSelector).length > 1) break;
     const hit = node.querySelector('[class*="single-value" i],[class*="singleValue" i],'
       + '[class*="multi-value" i],[class*="multiValue" i]');
     if (hit) return hit;
     node = node.parentElement;
   }
   return null;
 };
 const aliases = { 'full name':['full name','candidate name','your name','name'], 'first name':['first name','given name'], 'last name':['last name','family name','surname'], email:['email','email address'], phone:['phone','phone number','mobile'], linkedin:['linkedin','linkedin url','linkedin profile'], location:['location','city'], 'salary expectation':['salary expectation','salary expectations','desired salary','expected salary','salary range','desired compensation','compensation expectation','compensation expectations','expected compensation','minimum salary'] };
 // Best match wins, not first, and containment works both ways. A field labelled just "Name" has
 // to take "full name": the old rule only tried key-inside-label, so the shorter label matched
 // nothing at all and Ashby filled zero fields. Scoring keeps "first name" from being answered by
 // the "name" alias — an exact hit always outranks a containment, and a longer one a shorter.
 // A label can be asking for somebody else's details. "Company name" on an employment-history row
 // is the employer's; "School" is the university's; "Reference email" and "Manager phone" belong to
 // other people entirely. Every one of those was being answered with the applicant's own value,
 // because containment is all it took: the alias "name" is four characters and sits inside "company
 // name", "school name" and "your manager's name" alike. Found on Lyft's Greenhouse form, where
 // Company name came back filled with the applicant's full name.
 //
 // Containment is what has to stop at the boundary, not matching itself: a key that matches such a
 // label exactly is a value genuinely about that other party, and is still allowed through.
 const otherParty = /\b(company|employer|business|organisation|organization|school|university|college|institution|manager|supervisor|recruiter|reference|referee|emergency|parent|guardian|spouse)\b/;
 const valueFor = e => {
   const hay = textFor(e); if (!hay || protectedField(hay)) return null;
   const foreign = otherParty.test(hay);
   let best = null, bestScore = 0;
   for (const [raw, value] of Object.entries(payload.values || {})) {
     const key = norm(raw); if (!key || String(value) === '') continue;
     for (const alias of (aliases[key] || [key])) {
       const k = norm(alias); if (!k) continue;
       let score = 0;
       if (hay === k) score = 300 + k.length;
       else if (foreign) score = 0;
       else if (hay.includes(k) && k.length >= 3) score = 200 + k.length;
       else if (k.includes(hay) && hay.length >= 4) score = 100 + hay.length;
       if (score > bestScore) { bestScore = score; best = String(value); }
     }
   }
   return best;
 };
 // The same association, kept unnormalised so a human — and ChatGPT — read the real question.
 // Keep long eligibility/work-authorisation questions intact. Their explanatory note can change
 // the correct Yes/No answer, and truncating at 120 characters also makes error/refill matching
 // ambiguous when several questions share the same opening phrase.
 const labelFor = e => String(association(e)).replace(/\s+/g, ' ').trim().slice(0, 1600);
""";

    public static string BuildFillScript(Uri uri, IReadOnlyDictionary<string, string> values,
        IReadOnlyCollection<string>? forceLabels = null, IReadOnlyCollection<string>? settledLabels = null,
        IReadOnlyCollection<string>? onlyLabels = null)
    {
        var payload = JsonSerializer.Serialize(new
        {
            adapter = NameFor(uri), values,
            forceLabels = forceLabels ?? [], settledLabels = settledLabels ?? [],
            onlyLabels = onlyLabels ?? [],
        });
        return """
(() => {
__PRELUDE__
 const adapter = payload.adapter === 'Default (generic)' &&
   document.querySelector('a[href*="teamtailor.com" i],script[src*="teamtailor" i],[data-teamtailor]')
     ? 'Teamtailor' : payload.adapter;
 const fieldKey = value => norm(value).replace(/[^a-z0-9]+/g,' ').trim();
 const forceKeys = (payload.forceLabels || []).map(fieldKey).filter(Boolean);
 const forceField = e => {
   const key = fieldKey(labelFor(e) || textFor(e));
   return !!key && forceKeys.some(forced => key === forced
     || Math.min(key.length, forced.length) >= 8 && (key.includes(forced) || forced.includes(key)));
 };
 // Fields the host has already typed and verified, and that the last validation pass did not
 // complain about. Retyping a correct answer is not free: every pass costs a click, a full retype
 // and a settle delay, and each one is another chance for a controlled input to drop the value it
 // already held. A field leaves this set only when the form itself reports an error on it.
 const settledKeys = (payload.settledLabels || []).map(fieldKey).filter(Boolean);
 const settledField = e => {
   if (forceField(e)) return false;
   const key = fieldKey(labelFor(e) || textFor(e));
   return !!key && settledKeys.some(done => key === done
     || Math.min(key.length, done.length) >= 8 && (key.includes(done) || done.includes(key)));
 };
 // The scope of a correction pass. The site named the fields it rejected, and on a correction pass
 // those are the only ones this is allowed to touch — everything else keeps whatever it holds.
 // The settled ledger aims at the same thing but reaches it by inference: it survives only while
 // the page key holds, and it defers to an "is this already answered?" probe that has to be right
 // about every widget on the page. One radio group whose probe reads false is enough to retype a
 // correct answer, and on a checkbox the retype clicks it back off. The scope is not inference —
 // a field nobody complained about is not eligible, whatever the probes think. Empty on a primary
 // pass, which does see the whole form.
 const onlyKeys = (payload.onlyLabels || []).map(fieldKey).filter(Boolean);
 const outOfScope = e => {
   if (!onlyKeys.length) return false;
   const key = fieldKey(labelFor(e) || textFor(e));
   return !key || !onlyKeys.some(only => key === only
     || Math.min(key.length, only.length) >= 8 && (key.includes(only) || only.includes(key)));
 };
 // Ashby persists controlled fields asynchronously. Sending every blur in one JS task starts
 // overlapping state updates and later responses can restore an older, partially-filled form.
 // Queue text controls here; the host types and verifies them one at a time with a settle delay.
 window.__dsTextPlan = [];
 window.__dsChoicePlan = [];
 const planned = e => window.__dsTextPlan.some(item => item.element === e);
 const choicePlanned = e => window.__dsChoicePlan.some(item => sameField(item.element, e));
 const setText = (e, v) => {
   if (planned(e)) return true;
   window.__dsTextPlan.push({ element:e, label:labelFor(e) || textFor(e),
     value:String(v).replace(/\r\n?/g, '\n').replace(/\n+$/g, '') });
   return true;
 };
 const optionText = e => norm(choiceLabel(e));
 const optionMatch = (candidates, wanted, textOf) => candidates.find(o => textOf(o) === wanted)
   // Containment needs a length floor on the shorter side: norm('Norway').includes(norm('No')) is
   // true, which is how a country picker ends up answering a yes/no question.
   || candidates.find(o => { const t = textOf(o); return t && Math.min(t.length, wanted.length) >= 4 && (t.includes(wanted) || wanted.includes(t)); });
 const setSelect = (e, v) => {
   const wanted = norm(v); if (!wanted) return false;
   // Options with an empty value are the "Select…" placeholder, never an answer.
   const options = Array.from(e.options).filter(o => String(o.value || '').trim() !== '');
   const option = optionMatch(options, wanted, o => norm(o.text)) || optionMatch(options, wanted, o => norm(o.value));
   if (!option) return false;
   // Plain assignment leaves React's value tracker believing nothing changed, so the choice is
   // reverted on the next render and the control shows its placeholder again. The prototype setter
   // is the same trick setText already uses for the text inputs that do work.
   const setter = Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'value')?.set;
   e.focus({preventScroll:true});
   setter ? setter.call(e, option.value) : e.value = option.value;
   e.dispatchEvent(new Event('input',{bubbles:true}));
   e.dispatchEvent(new Event('change',{bubbles:true}));
   return true;
 };
 const planChoice = (e, v) => {
   if (choicePlanned(e)) return true;
   const wanted = norm(v);
   const force = forceField(e);
   let target = null;
   if (e.type === 'checkbox' && choiceOptions(e).length === 1) {
     const yes=['true','yes','y','1'].includes(wanted), no=['false','no','n','0'].includes(wanted);
     const option=optionText(e), matches=option && (wanted===option || wanted.includes(option));
     const shouldCheck=matches || yes;
     if (!matches && !yes && !no || e.checked === shouldCheck && !force) return false;
     target = e;
   } else {
     target = optionMatch(choiceOptions(e), wanted, optionText) || null;
     if (!target || choiceSelected(target) && !force) return false;
   }
   window.__dsChoicePlan.push({ element:target, label:labelFor(e) || textFor(e),
     value:String(v), option:choiceLabel(target), force });
   return true;
 };
 const core = {
   'Default (generic)': { 'first name':['input[autocomplete="given-name"]','input[name*="first_name" i]','input[name*="firstname" i]'], 'last name':['input[autocomplete="family-name"]','input[name*="last_name" i]','input[name*="lastname" i]'], 'full name':['input[autocomplete="name"]','input[name="name"]'], email:['input[type="email"]','input[autocomplete="email"]'], phone:['input[type="tel"]','input[autocomplete="tel"]'], linkedin:['input[name*="linkedin" i]','input[id*="linkedin" i]'] },
   'Greenhouse': { 'first name':['#first_name','input[name="first_name"]'], 'last name':['#last_name','input[name="last_name"]'], email:['#email','input[name="email"]'], phone:['#phone','input[name="phone"]'] },
   'Ashby': { 'full name':['input[name="name"]','input[autocomplete="name"]'], email:['input[type="email"]','input[name="email"]'], phone:['input[type="tel"]','input[name="phone"]'] },
   'Lever': { 'full name':['input[name="name"]','input[autocomplete="name"]'], email:['input[name="email"]','input[type="email"]'], phone:['input[name="phone"]','input[type="tel"]'] },
   'ApplyToJob': { 'first name':['input[name*="first" i]'], 'last name':['input[name*="last" i]'], email:['input[type="email"]'], phone:['input[type="tel"]'] },
   'Teamtailor': { 'first name':['input[autocomplete="given-name"]','input[name*="first_name" i]','input[id*="first_name" i]'], 'last name':['input[autocomplete="family-name"]','input[name*="last_name" i]','input[id*="last_name" i]'], email:['input[type="email"]','input[autocomplete="email"]'], phone:['input[type="tel"]','input[autocomplete="tel"]'], linkedin:['input[name*="linkedin" i]','input[id*="linkedin" i]'] }
 };
 let filled=0, skipped=0; const touched=[];
 const write = (e,v) => {
   if (!e || (!visible(e) && !choiceVisible(e)) || e.disabled || e.readOnly || e.type === 'file'
       || protectedField(textFor(e))) return false;
   if (e.tagName === 'SELECT') { if (!setSelect(e,v)) return false; }
   else if (choice(e)) { if (!planChoice(e,v)) return false; }
   else setText(e,v);
   return true;
 };
 const answered = e => choice(e) ? choiceSelected(e)
   : e.tagName === 'SELECT' ? e.selectedIndex > 0 && !!e.value : !!String(e.value || '').trim();
 // A radio group is answered when ANY of its options is, not when this particular one is. Asked per
 // element, a group we had just filled reported as still outstanding, once per unchosen option.
 const groupAnswered = e => choice(e) ? choiceOptions(e).some(choiceSelected) : answered(e);
 // The core selectors used to write unconditionally, so name/email/phone/LinkedIn were retyped on
 // every pass including the post-validation correction one, where nothing had asked for them.
 // They answer to the same rule as everything else now: already answered, or settled, means leave it.
 for (const [key, selectors] of Object.entries(core[adapter] || core['Default (generic)'])) {
   const v = payload.values[key]; if (!v) continue;
   for (const selector of selectors) {
     const e = document.querySelector(selector);
     if (!e || outOfScope(e) || settledField(e) || groupAnswered(e) && !forceField(e)) continue;
     if (write(e,v)) { filled++; touched.push(key); break; }
   }
 }
 const controls = Array.from(new Set(Array.from(document.querySelectorAll(
   'input:not([type="hidden"]),textarea,select,' + choiceSelector))));
 for (const e of controls) {
   if (e.type === 'file' || comboInput(e) || mirroredChoice(e)
       || (!visible(e) && !choiceVisible(e)) || e.disabled || outOfScope(e)
       || groupAnswered(e) && !forceField(e) || settledField(e)
       || planned(e) || choicePlanned(e)) continue;
   const v=valueFor(e);
   if (v === null) { skipped++; continue; }
   if (write(e,v)) { filled++; touched.push(textFor(e)); } else skipped++;
 }
 // Report what a human still has to complete. Protected questions are in here by design — the
 // adapters never answer legal, demographic or work-authorisation fields — and so is any dropdown
 // no saved answer matched, which is otherwise invisible until the form rejects the submission.
 const chosen = e => !!chosenValueFor(e);
 const custom = Array.from(document.querySelectorAll(comboSelector))
   .filter(e => visible(e) && e.getAttribute('aria-disabled') !== 'true' && !norm(e.value) && !chosen(e));
 const outstandingNodes = Array.from(new Set(Array.from(document.querySelectorAll(
   'input:not([type="hidden"]):not([type="file"]),textarea,select,' + choiceSelector))))
   .filter(e => (visible(e) || choiceVisible(e)) && !comboInput(e) && !mirroredChoice(e)
     && !e.disabled && !e.readOnly && !groupAnswered(e));
 const outstanding = outstandingNodes
   .map(e => { const l = labelFor(e); return l && e.tagName === 'SELECT' ? l + ' (dropdown)' : l; })
   .concat(custom.map(e => { const l = labelFor(e); return l ? l + ' (dropdown)' : ''; }))
   .filter(Boolean);
 // Which of those the form itself insists on. The distinction decides whether an application may be
 // submitted without a person seeing it: an unanswered optional EEO question is on every form ever
 // and blocking on it would mean nothing ever submits, while an unanswered required question means
 // the application is incomplete and an employer is about to receive it that way.
 const requiredField = e => !!(e.required || attr(e,'aria-required') === 'true'
   || /[*∗]\s*$/.test(String(labelFor(e) || '').trim())
   || (e.closest && e.closest('[aria-required="true"],[data-required="true"]')));
 const outstandingRequired = outstandingNodes.filter(requiredField)
   .map(e => { const l = labelFor(e); return l && e.tagName === 'SELECT' ? l + ' (dropdown)' : l; })
   .concat(custom.filter(requiredField).map(e => { const l = labelFor(e); return l ? l + ' (dropdown)' : ''; }))
   .filter(Boolean);
 // Which visible controls the correction scope actually resolved to. A scope that matches nothing is
 // a silent no-op otherwise: the pass reports zero fields filled and reads exactly like a form that
 // needed nothing, when what really happened is that the site's error text named no field we can see.
 const scopeMatched = !onlyKeys.length ? [] : Array.from(new Set(Array.from(document.querySelectorAll(
   'input:not([type="hidden"]),textarea,select,' + choiceSelector))))
   .filter(e => (visible(e) || choiceVisible(e)) && !e.disabled && !outOfScope(e))
   .map(e => labelFor(e) || textFor(e)).filter(Boolean);
 return { adapter, filled, skipped, touched:Array.from(new Set(touched)), textPlanned:window.__dsTextPlan.length,
   scoped:onlyKeys.length, scopeMatched:Array.from(new Set(scopeMatched)),
   textLabels:window.__dsTextPlan.map(item => item.label), choicePlanned:window.__dsChoicePlan.length,
   choiceLabels:window.__dsChoicePlan.map(item => item.label), unfilled:Array.from(new Set(outstanding)).slice(0,25),
   unfilledRequired:Array.from(new Set(outstandingRequired)).slice(0,25) };
})()
""".Replace("__PRELUDE__", MatchingPrelude).Replace("__PAYLOAD__", payload);
    }

    public static readonly string TextFieldPlanScript =
        "(window.__dsTextPlan || []).map((item,index) => ({index,label:item.label,value:item.value}))";

    public static readonly string ChoiceFieldPlanScript =
        "(window.__dsChoicePlan || []).map((item,index) => ({index,label:item.label,value:item.value,option:item.option,force:!!item.force}))";

    /// <summary>Returns the viewport coordinate of a planned choice for browser-level input.</summary>
    public static string BuildChoiceTargetScript(int index)
    {
        var payload = JsonSerializer.Serialize(new { index, adapter = "", values = new { } });
        return """
(() => {
__PRELUDE__
 const item = (window.__dsChoicePlan || [])[payload.index];
 if (!item) return { ok:false, error:'missing choice plan item' };
 let target = item.element?.isConnected ? item.element : null;
 if (!target) {
   target = Array.from(document.querySelectorAll(choiceSelector)).find(element =>
     choiceVisible(element) && norm(labelFor(element)) === norm(item.label)
       && norm(choiceLabel(element)) === norm(item.option));
 }
 if (!target || target.disabled) return { ok:false, error:'choice target unavailable' };
 item.element = target;
 target.scrollIntoView({block:'center',inline:'nearest',behavior:'instant'});
 const rect = target.getBoundingClientRect();
 if (rect.width <= 0 || rect.height <= 0) return { ok:false, error:'choice target has no viewport rectangle' };
 return { ok:true, x:rect.left + rect.width/2, y:rect.top + rect.height/2,
   label:item.label, option:item.option };
})()
""".Replace("__PRELUDE__", MatchingPrelude).Replace("__PAYLOAD__", payload);
    }

    public static string BuildChoiceVerifyScript(int index)
    {
        var payload = JsonSerializer.Serialize(new { index, adapter = "", values = new { } });
        return """
(() => {
__PRELUDE__
 const item = (window.__dsChoicePlan || [])[payload.index];
 if (!item) return { ok:false, error:'missing choice plan item' };
 const target = item.element?.isConnected ? item.element
   : Array.from(document.querySelectorAll(choiceSelector)).find(element =>
       norm(labelFor(element)) === norm(item.label) && norm(choiceLabel(element)) === norm(item.option));
 if (!target) return { ok:false, error:'choice target unavailable after click' };
 item.element = target;
 return { ok:choiceSelected(target), selected:choiceSelected(target), option:choiceLabel(target) };
})()
""".Replace("__PRELUDE__", MatchingPrelude).Replace("__PAYLOAD__", payload);
    }

    /// <summary>
    /// Finds a different option for a forced correction. Selecting it before the requested option
    /// guarantees a real controlled-state transition. A single checkbox resets with two clicks.
    /// </summary>
    public static string BuildChoiceResetTargetScript(int index)
    {
        var payload = JsonSerializer.Serialize(new { index, adapter = "", values = new { } });
        return """
(() => {
__PRELUDE__
 const item = (window.__dsChoicePlan || [])[payload.index];
 if (!item) return { ok:false, error:'missing choice plan item' };
 const target = item.element?.isConnected ? item.element
   : Array.from(document.querySelectorAll(choiceSelector)).find(element =>
       choiceVisible(element) && norm(labelFor(element)) === norm(item.label)
         && norm(choiceLabel(element)) === norm(item.option));
 if (!target) return { ok:false, error:'choice target unavailable' };
 const options = choiceOptions(target).filter(option => choiceVisible(option) && !option.disabled);
 const reset = options.find(option => option !== target) || target;
 reset.scrollIntoView({block:'center',inline:'nearest',behavior:'instant'});
 const rect = reset.getBoundingClientRect();
 if (rect.width <= 0 || rect.height <= 0) return { ok:false, error:'reset target has no viewport rectangle' };
 return { ok:true, x:rect.left + rect.width/2, y:rect.top + rect.height/2,
   option:choiceLabel(reset) };
})()
""".Replace("__PRELUDE__", MatchingPrelude).Replace("__PAYLOAD__", payload);
    }

    /// <summary>Returns a planned text control's viewport coordinate for browser-level typing.</summary>
    public static string BuildTextFieldTargetScript(int index)
    {
        var payload = JsonSerializer.Serialize(new { index, adapter = "", values = new { } });
        return """
(() => {
__PRELUDE__
 const item = (window.__dsTextPlan || [])[payload.index];
 if (!item) return { ok:false, error:'missing text plan item' };
 const controls = Array.from(document.querySelectorAll('input:not([type="hidden"]):not([type="file"]),textarea'));
 const target = item.element?.isConnected ? item.element
   : controls.find(control => visible(control) && norm(labelFor(control)) === norm(item.label));
 if (!target || target.disabled || target.readOnly) return { ok:false, error:'text target unavailable' };
 item.element = target;
 // behavior:'instant' is load-bearing. A page with scroll-behavior:smooth animates the scroll, so
 // the rectangle read on the next line is the position *before* it moved — on Greenhouse that put
 // the LinkedIn field at y=1017 in a 900px window, the click landed outside the viewport, and every
 // keystroke after it went nowhere while the field reported only "value did not persist".
 target.scrollIntoView({block:'center',inline:'nearest',behavior:'instant'});
 const rect = target.getBoundingClientRect();
 if (rect.width <= 0 || rect.height <= 0) return { ok:false, error:'text target has no viewport rectangle' };
 const x = rect.left + rect.width/2, y = rect.top + rect.height/2;
 if (x < 0 || y < 0 || x > window.innerWidth || y > window.innerHeight)
   return { ok:false, error:'target off-screen at ' + Math.round(x) + ',' + Math.round(y)
     + ' (viewport ' + window.innerWidth + 'x' + window.innerHeight + ')' };
 // Whatever is topmost at that point is what the click will hit. A sticky header or a consent
 // banner over the field would otherwise swallow it and send the typing to the wrong place.
 const atPoint = document.elementFromPoint(x, y);
 if (atPoint && atPoint !== target && !target.contains(atPoint) && !atPoint.contains(target))
   return { ok:false, error:'covered by ' + atPoint.tagName.toLowerCase()
     + (atPoint.className ? '.' + String(atPoint.className).split(' ')[0] : '') };
 // Clicking anything wired to a file input opens the operating system's file picker, and that
 // dialog blocks the run until somebody dismisses it by hand — the stall that looked like the
 // extraction hanging at the resume upload and freeing itself when another area was clicked.
 // The resume is attached over DevTools instead, so no click ever needs to land here.
 if (opensFilePicker(atPoint) || opensFilePicker(target))
   return { ok:false, error:'target would open a file dialog; resume upload is handled separately' };
 return { ok:true, x, y };
})()
""".Replace("__PRELUDE__", MatchingPrelude).Replace("__PAYLOAD__", payload);
    }

    /// <summary>Types one planned text field. The host waits before moving to the next field.</summary>
    public static string BuildTextFieldTypeScript(int index)
    {
        var payload = JsonSerializer.Serialize(new { index, adapter = "", values = new { } });
        return """
(() => {
__PRELUDE__
 const item = (window.__dsTextPlan || [])[payload.index];
 if (!item) return { ok:false, error:'missing plan item' };
 const controls = Array.from(document.querySelectorAll('input:not([type="hidden"]):not([type="file"]),textarea'));
 const e = item.element?.isConnected ? item.element
   : controls.find(control => visible(control) && norm(labelFor(control)) === norm(item.label));
 if (!e || e.disabled || e.readOnly) return { ok:false, error:'field unavailable' };
 item.element = e;
 const value = String(item.value || '').replace(/\r\n?/g,'\n').replace(/\n+$/g,'');
 const proto = e instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
 const setter = Object.getOwnPropertyDescriptor(proto,'value')?.set;
 const put = text => setter ? setter.call(e,text) : e.value = text;
 e.focus();
 put('');
 e.dispatchEvent(new InputEvent('input',{bubbles:true,inputType:'deleteContentBackward'}));
 let typed = '';
 for (const character of value) {
   const lineBreak = character === '\n';
   const key = lineBreak ? 'Enter' : character;
   typed += character;
   e.dispatchEvent(new KeyboardEvent('keydown',{key,code:lineBreak?'Enter':'',bubbles:true,cancelable:true}));
   put(typed);
   e.dispatchEvent(new InputEvent('input',{bubbles:true,inputType:lineBreak?'insertLineBreak':'insertText',data:lineBreak?null:character}));
   e.dispatchEvent(new KeyboardEvent('keyup',{key,code:lineBreak?'Enter':'',bubbles:true,cancelable:true}));
 }
 e.dispatchEvent(new Event('change',{bubbles:true}));
 e.blur();
 return { ok:true, length:value.length };
})()
""".Replace("__PRELUDE__", MatchingPrelude).Replace("__PAYLOAD__", payload);
    }

    public static string BuildTextFieldVerifyScript(int index)
    {
        var payload = JsonSerializer.Serialize(new { index, adapter = "", values = new { } });
        return """
(() => {
__PRELUDE__
 const item = (window.__dsTextPlan || [])[payload.index];
 if (!item) return { ok:false, value:'', error:'missing plan item' };
 const controls = Array.from(document.querySelectorAll('input:not([type="hidden"]):not([type="file"]),textarea'));
 const e = item.element?.isConnected ? item.element
   : controls.find(control => visible(control) && norm(labelFor(control)) === norm(item.label));
 if (!e) return { ok:false, value:'', error:'field unavailable after render' };
 item.element = e;
 const actual = String(e.value || ''), expected = String(item.value || '');
 // Fields reformat what they are given: a phone widget regroups digits and appends a space, a URL
 // field may drop a trailing slash. Demanding an exact string called those "did not persist" and
 // sent a correctly filled field to human review, so the value is accepted when it matches once
 // punctuation and spacing are set aside — and only ever when the field is genuinely non-empty.
 const tidy = s => String(s || '').replace(/\s+/g, ' ').trim();
 const significant = s => tidy(s).toLowerCase().replace(/[^a-z0-9@._+-]/g, '');
 const ok = tidy(actual) === tidy(expected)
   || (actual.length > 0 && significant(actual) === significant(expected));
 return { ok, value:actual.slice(0,120),
   error: ok ? '' : (actual.length === 0 ? 'field still empty' : 'value did not persist') };
})()
""".Replace("__PRELUDE__", MatchingPrelude).Replace("__PAYLOAD__", payload);
    }

    /// <summary>Read-only final checkpoint after sequential text and dropdown filling.</summary>
    public static readonly string OutstandingFieldsScript = """
(() => {
__PRELUDE__
 const answered = e => choice(e) ? choiceSelected(e)
   : e.tagName === 'SELECT' ? e.selectedIndex > 0 && !!e.value : !!String(e.value || '').trim();
 const groupAnswered = e => choice(e) ? choiceOptions(e).some(choiceSelected) : answered(e);
 const chosen = e => !!chosenValueFor(e);
 // Whether the form itself insists on this one. This is the reading that decides whether an
 // application may be submitted unseen, and it has to be taken here rather than from the fill
 // script: the fill script only *plans* the text fields, and the host types them afterwards, so
 // anything it reported as outstanding was a snapshot from before a single character was entered.
 // Wiring the submit gate to that snapshot made it refuse to submit a form it had just filled,
 // naming Full name, Email and Phone as unanswered moments after verifying all three.
 const requiredField = e => !!(e.required || attr(e,'aria-required') === 'true'
   || /[*✱∗﹡＊]\s*$/.test(String(labelFor(e) || '').trim())
   || (e.closest && e.closest('[aria-required="true"],[data-required="true"]')));
 const label = e => { const l=labelFor(e); return l && e.tagName === 'SELECT' ? l+' (dropdown)' : l; };
 const plainNodes = Array.from(new Set(Array.from(document.querySelectorAll(
   'input:not([type="hidden"]):not([type="file"]),textarea,select,' + choiceSelector))))
   .filter(e => (visible(e) || choiceVisible(e)) && !comboInput(e) && !mirroredChoice(e)
     && !e.disabled && !e.readOnly && !groupAnswered(e));
 const customNodes = Array.from(document.querySelectorAll(comboSelector))
   // Search text is not a selected answer. Dynamic dropdowns commonly retain the failed primary
   // query in their input, so only a rendered chosen-value marker removes them from this list.
   .filter(e => visible(e) && e.getAttribute('aria-disabled') !== 'true' && !chosen(e));
 const name = e => e.tagName === 'SELECT' || customNodes.includes(e)
   ? (labelFor(e) ? labelFor(e) + ' (dropdown)' : '') : label(e);
 const all = plainNodes.concat(customNodes).map(name).filter(Boolean);
 const required = plainNodes.concat(customNodes).filter(requiredField).map(name).filter(Boolean);
 return { all: Array.from(new Set(all)).slice(0,25),
          required: Array.from(new Set(required)).slice(0,25) };
})()
""".Replace("__PRELUDE__", MatchingPrelude).Replace("__PAYLOAD__", "{\"adapter\":\"\",\"values\":{}}");

    // =============================================================================================
    // Custom dropdowns
    //
    // React comboboxes (Ashby, Greenhouse's newer boards, anything on react-select) keep no options
    // in the DOM until the control is opened, so the fill pass above cannot reach them. They are
    // driven the way a person drives them: focus the control, type the answer so the list filters,
    // then press Enter to take the highlighted option. That runs as three calls with a wait between
    // typing and committing, because the list is rendered asynchronously and ExecuteScriptAsync does
    // not await a promise.
    // =============================================================================================

    /// <summary>
    /// Stashes the unanswered custom dropdowns that have a known answer on <c>window.__dsCombos</c>
    /// and returns what will be typed into each. Nothing is touched yet.
    /// </summary>
    public static string BuildComboboxPlanScript(IReadOnlyDictionary<string, string> values,
        IReadOnlyCollection<string>? forceLabels = null, IReadOnlyCollection<string>? settledLabels = null,
        IReadOnlyCollection<string>? onlyLabels = null)
    {
        var payload = JsonSerializer.Serialize(new
        {
            adapter = "", values,
            forceLabels = forceLabels ?? [], settledLabels = settledLabels ?? [],
            onlyLabels = onlyLabels ?? [],
        });
        return """
(() => {
__PRELUDE__
 const shell = e => e.closest('[class*="select" i]:not(input)') || e.parentElement || e;
 // Answered means the widget renders a chosen value, not merely that its search box holds text.
 const answered = e => !!chosenValueFor(e);
 const fieldKey = value => norm(value).replace(/[^a-z0-9]+/g,' ').trim();
 const forceKeys = (payload.forceLabels || []).map(fieldKey).filter(Boolean);
 const forced = e => {
   const key = fieldKey(labelFor(e) || labelFor(shell(e)));
   return !!key && forceKeys.some(force => key === force
     || Math.min(key.length, force.length) >= 8 && (key.includes(force) || force.includes(key)));
 };
 // A dropdown the host already drove and confirmed. Reopening one costs an overlay, a schema read
 // and a verify poll each time, and the overlay covers whatever still needs filling underneath.
 const settledKeys = (payload.settledLabels || []).map(fieldKey).filter(Boolean);
 const settled = e => {
   if (forced(e)) return false;
   const key = fieldKey(labelFor(e) || labelFor(shell(e)));
   return !!key && settledKeys.some(done => key === done
     || Math.min(key.length, done.length) >= 8 && (key.includes(done) || done.includes(key)));
 };
 // Same scope rule as the fill script: on a correction pass only the fields the site named are
 // eligible. Reopening a dropdown that already shows the right answer costs an overlay, a schema
 // read and a verify poll, and the overlay covers whatever else still needs filling underneath.
 const onlyKeys = (payload.onlyLabels || []).map(fieldKey).filter(Boolean);
 const outOfScope = e => {
   if (!onlyKeys.length) return false;
   const key = fieldKey(labelFor(e) || labelFor(shell(e)));
   return !key || !onlyKeys.some(only => key === only
     || Math.min(key.length, only.length) >= 8 && (key.includes(only) || only.includes(key)));
 };
 // Suggestion text boxes join the dropdown pass, because that is what they are: the pass already
 // knows how to type, wait for a list, choose an option and read back what was chosen, which is
 // exactly the sequence one of these needs and exactly what the text pass cannot do.
 const controls = Array.from(new Set(Array.from(document.querySelectorAll(comboSelector))
     .concat(Array.from(document.querySelectorAll('input')).filter(typeaheadInput))))
   .filter(e => visible(e) && e.getAttribute('aria-disabled') !== 'true' && !e.disabled
     && !outOfScope(e) && !settled(e) && (!answered(e) || forced(e)));
 const plan = [];
 window.__dsCombos = [];
 for (const control of controls) {
   const value = valueFor(control) ?? valueFor(shell(control));
   if (value === null) continue;
   window.__dsCombos.push(control);
   plan.push({ index: window.__dsCombos.length - 1,
     label: labelFor(control) || labelFor(shell(control)), value, force:forced(control) });
 }
 return plan;
})()
""".Replace("__PRELUDE__", MatchingPrelude).Replace("__PAYLOAD__", payload);
    }

    /// <summary>Activates a fill-time dropdown and focuses the search input it reveals.</summary>
    public static string BuildComboboxOpenScript(int index)
    {
        var payload = JsonSerializer.Serialize(new { index });
        return """
(() => {
 const request = __PAYLOAD__;
 const control = (window.__dsCombos || [])[request.index];
 if (!control) return { ok:false, error:'gone' };
 const visible = e => !!(e && (e.offsetWidth || e.offsetHeight || e.getClientRects().length));
 const baselineNodes = Array.from(document.body.querySelectorAll('*'));
 window.__dsComboBaseline = new WeakSet(baselineNodes);
 window.__dsComboVisibleBaseline = new WeakSet(baselineNodes.filter(visible));
 control.scrollIntoView({ block:'center', behavior:'instant' });
 if (typeof PointerEvent === 'function')
   control.dispatchEvent(new PointerEvent('pointerdown',{bubbles:true,cancelable:true,pointerType:'mouse',isPrimary:true}));
 control.dispatchEvent(new MouseEvent('mousedown',{bubbles:true,cancelable:true,button:0,buttons:1}));
 control.focus({preventScroll:true});
 if (typeof PointerEvent === 'function')
   control.dispatchEvent(new PointerEvent('pointerup',{bubbles:true,cancelable:true,pointerType:'mouse',isPrimary:true}));
 control.dispatchEvent(new MouseEvent('mouseup',{bubbles:true,cancelable:true,button:0}));
 control.click();
 // Prefer the element the click itself focused, then an input in this field, then a newly mounted
 // portal input. Searching the whole document for the first input used to focus Name/Email instead.
 const container = control.closest('[role="combobox"],[class*="select" i],[data-field],fieldset')
   || control.parentElement || control;
 const active = document.activeElement;
 const input = control.tagName === 'INPUT' ? control
   : active?.tagName === 'INPUT' && visible(active) ? active
   : Array.from(container.querySelectorAll('input')).find(e => visible(e) && !e.disabled && !e.readOnly)
   || Array.from(document.querySelectorAll('input')).find(e => visible(e) && !window.__dsComboBaseline?.has(e)
       && !e.disabled && !e.readOnly);
 (input || control).focus({preventScroll:true});
 window.__dsComboInput = input || null;
 return { ok:true, input:!!input, focused:document.activeElement === (input || control), error:'' };
})()
""".Replace("__PAYLOAD__", payload);
    }

    /// <summary>Types the answer after the activated dropdown has had time to mount its input.</summary>
    public static string BuildComboboxTypeScript(int index, string value)
    {
        var payload = JsonSerializer.Serialize(new { index, value });
        return """
(() => {
 const request = __PAYLOAD__;
 const control = (window.__dsCombos || [])[request.index];
 if (!control) return { ok:false, error:'gone' };
 const visible = e => !!(e && (e.offsetWidth || e.offsetHeight || e.getClientRects().length));
 const container = control.closest('[role="combobox"],[class*="select" i],[data-field],fieldset')
   || control.parentElement || control;
 const active = document.activeElement;
 const input = control.tagName === 'INPUT' ? control
   : window.__dsComboInput?.isConnected ? window.__dsComboInput
   : active?.tagName === 'INPUT' && visible(active) ? active
   : Array.from(container.querySelectorAll('input')).find(e => visible(e) && !e.disabled && !e.readOnly)
   || Array.from(document.querySelectorAll('input')).find(e => visible(e) && !window.__dsComboBaseline?.has(e)
       && !e.disabled && !e.readOnly);
 if (!input) return { ok:false, error:'no input to type into after activation' };
 input.focus({preventScroll:true});
 const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value')?.set;
 const put = value => setter ? setter.call(input,value) : input.value = value;
 put(''); input.dispatchEvent(new InputEvent('input',{bubbles:true,inputType:'deleteContentBackward'}));
 let typed = '';
 for (const character of String(request.value || '')) {
   typed += character;
   input.dispatchEvent(new KeyboardEvent('keydown',{key:character,bubbles:true,cancelable:true}));
   put(typed);
   input.dispatchEvent(new InputEvent('input',{bubbles:true,inputType:'insertText',data:character}));
   input.dispatchEvent(new KeyboardEvent('keyup',{key:character,bubbles:true,cancelable:true}));
 }
 window.__dsComboInput = input;
 return { ok:true, error:'' };
})()
""".Replace("__PAYLOAD__", payload);
    }

    /// <summary>
    /// Finds every unanswered custom dropdown before the ChatGPT question prompt. The controls are
    /// retained on <c>window</c> because their options usually do not exist until each menu is open.
    /// </summary>
    public static string BuildDropdownQuestionPlanScript(IReadOnlyDictionary<string, string> values)
    {
        var payload = JsonSerializer.Serialize(new { adapter = "", values });
        return """
(() => {
__PRELUDE__
 const shell = e => e.closest('[class*="select" i]:not(input)') || e.parentElement || e;
 const chosen = e => !!chosenValueFor(e);
 const controls = Array.from(document.querySelectorAll(comboSelector))
   .filter(e => visible(e) && e.getAttribute('aria-disabled') !== 'true' && !e.disabled && !chosen(e));
 const seen = new Set();
 window.__dsQuestionCombos = [];
 const plan = [];
 for (const control of controls) {
   const question = labelFor(control) || labelFor(shell(control));
   const key = norm(question);
   if (!question || !key || protectedField(key) || seen.has(key)) continue;
   seen.add(key);
   window.__dsQuestionCombos.push(control);
   const rawCandidate = String(valueFor(control) ?? valueFor(shell(control)) ?? '').trim();
   // Placeholders are sometimes exposed as the current value (notably "No options"). They are
   // not candidate answers and typing one into every server-backed dropdown prevents it from
   // returning useful suggestions.
   const candidate = /^(no options?|select( an?)? option|choose( an?)? option)$/i.test(rawCandidate)
     ? '' : rawCandidate;
   plan.push({ index: window.__dsQuestionCombos.length - 1, question, candidate });
 }
 return plan;
})()
""".Replace("__PRELUDE__", MatchingPrelude).Replace("__PAYLOAD__", payload);
    }

    /// <summary>
    /// Starts the same read-only form-schema query the Ashby job page uses. ValueSelect fields carry
    /// their complete selectableValues collection here even when the open menu has no option roles.
    /// </summary>
    public static readonly string StartAshbyDropdownSchemaScript = """
(() => {
 if (!location.hostname.toLowerCase().endsWith('ashbyhq.com')) return { started:false, error:'not Ashby' };
 const parts = location.pathname.split('/').filter(Boolean);
 const posting = parts.find(part => /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(part));
 const organization = parts[0] || '';
 if (!organization || !posting) return { started:false, error:'job identity not found' };
 const query = `query ApiJobPosting($organizationHostedJobsPageName:String!,$jobPostingId:String!){jobPosting(organizationHostedJobsPageName:$organizationHostedJobsPageName,jobPostingId:$jobPostingId){applicationForm{sections{fieldEntries{field isHidden}}}surveyForms{sections{fieldEntries{field isHidden}}}}}`;
 window.__dsAshbyDropdownSchema = { status:'loading', questions:[], error:'' };
 fetch('/api/non-user-graphql?op=ApiJobPosting', {
   method:'POST', headers:{'Content-Type':'application/json'},
   body:JSON.stringify({operationName:'ApiJobPosting',variables:{organizationHostedJobsPageName:organization,jobPostingId:posting},query})
 }).then(response => response.json()).then(json => {
   if (json.errors?.length) throw new Error(json.errors.map(error => error.message).join('; '));
   const postingData = json.data?.jobPosting;
   const forms = [postingData?.applicationForm, ...(postingData?.surveyForms || [])].filter(Boolean);
   const questions = [];
   for (const form of forms) for (const section of (form.sections || [])) {
     for (const entry of (section.fieldEntries || [])) {
       if (entry.isHidden) continue;
       let field = entry.field;
       if (typeof field === 'string') { try { field = JSON.parse(field); } catch { continue; } }
       const options = Array.from(field?.selectableValues || [])
         .map(option => String(option?.label ?? option?.value ?? '').replace(/\s+/g,' ').trim())
         .filter(Boolean);
       const question = String(field?.title || field?.humanReadablePath || '').replace(/\s+/g,' ').trim();
       if (question && options.length) questions.push({ question, options:Array.from(new Set(options)) });
     }
   }
   window.__dsAshbyDropdownSchema = { status:'ready', questions, error:'' };
 }).catch(error => window.__dsAshbyDropdownSchema = { status:'error', questions:[], error:String(error?.message || error) });
 return { started:true, error:'' };
})()
""";

    public static readonly string ReadAshbyDropdownSchemaScript =
        "window.__dsAshbyDropdownSchema || { status:'missing', questions:[], error:'' }";

    /// <summary>
    /// Opens one retained custom dropdown without choosing an option. Pointer/mousedown are sent
    /// before explicit focus and click because several job-site widgets mount their menu from those
    /// events rather than from <c>focus()</c> alone.
    /// </summary>
    public static string BuildDropdownQuestionOpenScript(int index)
    {
        var payload = JsonSerializer.Serialize(new { index });
        return """
(() => {
 const request = __PAYLOAD__;
 const control = (window.__dsQuestionCombos || [])[request.index];
 if (!control || !control.isConnected) return { ok:false, error:'gone' };
 const visible = e => !!e && !!(e.offsetWidth || e.offsetHeight || e.getClientRects().length);
 const baselineNodes = Array.from(document.body.querySelectorAll('*'));
 window.__dsDropdownBaseline = new WeakSet(baselineNodes);
 window.__dsDropdownVisibleBaseline = new WeakSet(baselineNodes.filter(visible));
 control.scrollIntoView({ block:'center', behavior:'instant' });
 if (typeof PointerEvent === 'function')
   control.dispatchEvent(new PointerEvent('pointerdown',{bubbles:true,cancelable:true,pointerType:'mouse',isPrimary:true}));
 control.dispatchEvent(new MouseEvent('mousedown',{bubbles:true,cancelable:true,button:0,buttons:1}));
 control.focus({preventScroll:true});
 if (typeof PointerEvent === 'function')
   control.dispatchEvent(new PointerEvent('pointerup',{bubbles:true,cancelable:true,pointerType:'mouse',isPrimary:true}));
 control.dispatchEvent(new MouseEvent('mouseup',{bubbles:true,cancelable:true,button:0}));
 control.click();
 const container = control.closest('[role="combobox"],[class*="select" i],[data-field],fieldset')
   || control.parentElement || control;
 const active = document.activeElement;
 const input = control.tagName === 'INPUT' ? control
   : active?.tagName === 'INPUT' && visible(active) ? active
   : Array.from(container.querySelectorAll('input')).find(e => visible(e) && !e.disabled && !e.readOnly)
   || Array.from(document.querySelectorAll('input')).find(e => visible(e) && !window.__dsDropdownBaseline?.has(e)
       && !e.disabled && !e.readOnly);
 (input || control).focus({preventScroll:true});
 window.__dsQuestionComboInput = input || control;
 return { ok:true, focused:document.activeElement === (input || control), error:'' };
})()
""".Replace("__PAYLOAD__", payload);
    }

    /// <summary>Types a known candidate value to materialize autocomplete suggestions.</summary>
    public static string BuildDropdownQuestionSearchScript(int index, string value)
    {
        var payload = JsonSerializer.Serialize(new { index, value });
        return """
(() => {
 const request = __PAYLOAD__;
 const control = (window.__dsQuestionCombos || [])[request.index];
 const input = window.__dsQuestionComboInput || control;
 if (!input || input.tagName !== 'INPUT') return { ok:false, error:'no search input' };
 const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value')?.set;
 const put = value => setter ? setter.call(input,value) : input.value = value;
 input.focus(); put(''); input.dispatchEvent(new InputEvent('input',{bubbles:true,inputType:'deleteContentBackward'}));
 let typed = '';
 for (const character of String(request.value || '')) {
   typed += character;
   input.dispatchEvent(new KeyboardEvent('keydown',{key:character,bubbles:true,cancelable:true}));
   put(typed);
   input.dispatchEvent(new InputEvent('input',{bubbles:true,inputType:'insertText',data:character}));
   input.dispatchEvent(new KeyboardEvent('keyup',{key:character,bubbles:true,cancelable:true}));
 }
 return { ok:true, error:'' };
})()
""".Replace("__PAYLOAD__", payload);
    }

    /// <summary>
    /// Reads the current page of an open custom dropdown and advances its scroll viewport. Repeated
    /// calls allow the host to collect options from virtualized lists that only render one page.
    /// </summary>
    public static string BuildDropdownQuestionReadScript(int index)
    {
        var payload = JsonSerializer.Serialize(new { index });
        return """
(() => {
 const request = __PAYLOAD__;
 const control = (window.__dsQuestionCombos || [])[request.index];
 if (!control || !control.isConnected) return { ok:false, options:[], atEnd:true, error:'gone' };
 const visible = e => !!e && !!(e.offsetWidth || e.offsetHeight || e.getClientRects().length);
 const clean = value => String(value || '').replace(/\s+/g, ' ').trim();
 const input = window.__dsQuestionComboInput || control;
 const controlledId = input.getAttribute?.('aria-controls') || input.getAttribute?.('aria-owns')
   || control.getAttribute?.('aria-controls') || control.getAttribute?.('aria-owns') || '';
 // Both .filter(Boolean) calls are load-bearing, and so is the null guard in visible(). With no
 // aria-controls — which is what an unopened menu looks like — controlledId is '', so split gives
 // [''], getElementById('') is null, and visible(null) threw. WebView2 reports a thrown script as
 // the bare string "null", so the host read a crash as "no option matched": every dropdown on the
 // form logged "not confirmed, leaving it for review", and the run submitted the application anyway.
 const controlled = controlledId.split(/\s+/).filter(Boolean)
   .map(id => document.getElementById(id)).filter(Boolean).find(visible) || null;
 const listboxes = Array.from(document.querySelectorAll('[role="listbox"],[class*="menu" i],[class*="popover" i],[class*="dropdown-results" i],[class*="suggest" i],[class*="typeahead" i],[class*="autocomplete" i]')).filter(visible);
 // aria-controls is authoritative. Without it, only a menu mounted by this activation is eligible;
 // using the last menu anywhere on the page picked Greenhouse's navigation and every JD <li>.
 // A menu may either be newly mounted or pre-exist hidden and become visible when activated.
 const freshListboxes = listboxes.filter(node => !window.__dsDropdownVisibleBaseline?.has(node));
 const listbox = visible(controlled) ? controlled : (freshListboxes.length ? freshListboxes[freshListboxes.length - 1] : null);
 const roleNodes = listbox
   ? Array.from(listbox.querySelectorAll('[role="option"],[data-value],[data-option],[class*="option" i],[class*="dropdown-" i],li')).filter(visible)
   : [];
 // Ashby's hashed menu markup currently has no option/listbox roles. Menu rows are freshly mounted,
 // so their visible leaf nodes are a safer fallback than guessing a generated class name.
 const controlRect = control.getBoundingClientRect();
 const nearControl = node => {
   const rect = node.getBoundingClientRect();
   return rect.right >= controlRect.left - 40 && rect.left <= controlRect.right + 240
     && rect.bottom >= controlRect.top - 120 && rect.top <= controlRect.bottom + 700;
 };
 const optionText = node => clean(node.innerText || node.textContent);
 const noise = text => /^(\d+ results? available\.?|no options|loading\.\.\.)$/i.test(text)
   || /use up and down|press enter to select|press escape to exit|press tab to select/i.test(text);
 const freshLeaves = Array.from(document.body.querySelectorAll('*')).filter(node =>
   visible(node) && !window.__dsDropdownVisibleBaseline?.has(node) &&
   !Array.from(node.children).some(child => visible(child) && clean(child.innerText || child.textContent)) &&
   nearControl(node) && optionText(node).length <= 240 && !noise(optionText(node)));
 const nodes = Array.from(new Set(roleNodes.concat(freshLeaves)));
 const options = Array.from(new Set(nodes.map(optionText)
   .filter(text => text && text.length <= 240 && !noise(text))));
 let scrollHost = listbox;
 if (nodes.length) {
   for (let node = nodes[0].parentElement; node && node !== document.body; node = node.parentElement) {
     if (node.scrollHeight > node.clientHeight + 1) { scrollHost = node; break; }
   }
 }
 if (!scrollHost || scrollHost.scrollHeight <= scrollHost.clientHeight + 1)
   return { ok:options.length > 0, options, atEnd:true, advanced:false, source:listbox ? 'menu' : 'fresh', error:options.length ? '' : 'no options rendered' };
 const max = scrollHost.scrollHeight - scrollHost.clientHeight;
 const before = scrollHost.scrollTop;
 const atEnd = before >= max - 1;
 if (!atEnd) {
   scrollHost.scrollTop = Math.min(max, before + Math.max(80, Math.floor(scrollHost.clientHeight * 0.8)));
   scrollHost.dispatchEvent(new Event('scroll',{bubbles:true}));
 }
 const after = scrollHost.scrollTop;
 return { ok:options.length > 0, options, atEnd, advanced:after > before + 0.5,
   source:listbox ? 'menu' : 'fresh', error:options.length ? '' : 'no options rendered' };
})()
""".Replace("__PAYLOAD__", payload);
    }

    /// <summary>Closes the option-discovery menu without changing its answer.</summary>
    public static string BuildDropdownQuestionCloseScript(int index)
    {
        var payload = JsonSerializer.Serialize(new { index });
        return """
(() => {
 const request = __PAYLOAD__;
 const control = (window.__dsQuestionCombos || [])[request.index];
 const input = window.__dsQuestionComboInput || control;
 if (!input) return false;
 for (const type of ['keydown','keyup'])
   input.dispatchEvent(new KeyboardEvent(type,{key:'Escape',code:'Escape',bubbles:true,cancelable:true}));
 input.blur();
 // Greenhouse keeps some menus active after synthetic Escape. A neutral body click is the same
 // outside click that releases the widget for the next dropdown, without choosing an option.
 if (typeof PointerEvent === 'function')
   document.body.dispatchEvent(new PointerEvent('pointerdown',{bubbles:true,cancelable:true,pointerType:'mouse',isPrimary:true}));
 document.body.dispatchEvent(new MouseEvent('mousedown',{bubbles:true,cancelable:true,button:0,buttons:1}));
 document.body.dispatchEvent(new MouseEvent('mouseup',{bubbles:true,cancelable:true,button:0}));
 document.body.click();
 return true;
})()
""".Replace("__PAYLOAD__", payload);
    }

    /// <summary>
    /// Commits the filtered list with Enter, then reports what the control ended up showing so the
    /// caller can count a real selection rather than assume one.
    /// </summary>
    public static string BuildComboboxCommitScript(int index, string value)
    {
        var payload = JsonSerializer.Serialize(new { index, value });
        return """
(() => {
 const request = __PAYLOAD__;
 const control = (window.__dsCombos || [])[request.index];
 const input = window.__dsComboInput;
 if (!control || !input) return { ok:false, text:'' };
 // Accents are folded, not deleted. Stripping them as punctuation turned "Cuautitlán" into
 // "cuautitl n", so a typed "Cuautitlan" matched none of the suggestions a Mexican city returns
 // and the pick never happened.
 const norm = s => String(s || '').normalize('NFD').replace(/[̀-ͯ]/g, '')
   .toLowerCase().replace(/[^a-z0-9]+/g, ' ').trim();
 const visible = e => !!(e && (e.offsetWidth || e.offsetHeight || e.getClientRects().length));
 const wanted = norm(request.value);
 const clean = value => String(value || '').replace(/\s+/g,' ').trim();
 const controlledId = input.getAttribute?.('aria-controls') || input.getAttribute?.('aria-owns')
   || control.getAttribute?.('aria-controls') || control.getAttribute?.('aria-owns') || '';
 // Both .filter(Boolean) calls are load-bearing, and so is the null guard in visible(). With no
 // aria-controls — which is what an unopened menu looks like — controlledId is '', so split gives
 // [''], getElementById('') is null, and visible(null) threw. WebView2 reports a thrown script as
 // the bare string "null", so the host read a crash as "no option matched": every dropdown on the
 // form logged "not confirmed, leaving it for review", and the run submitted the application anyway.
 const controlled = controlledId.split(/\s+/).filter(Boolean)
   .map(id => document.getElementById(id)).filter(Boolean).find(visible) || null;
 const listboxes = Array.from(document.querySelectorAll('[role="listbox"],[class*="menu" i],[class*="popover" i],[class*="dropdown-results" i],[class*="suggest" i],[class*="typeahead" i],[class*="autocomplete" i]')).filter(visible);
 const freshListboxes = listboxes.filter(node => !window.__dsComboVisibleBaseline?.has(node));
 const menu = visible(controlled) ? controlled : (freshListboxes.length ? freshListboxes[freshListboxes.length - 1] : null);
 const roleOptions = menu
   ? Array.from(menu.querySelectorAll('[role="option"],[data-value],[data-option],[class*="option" i],[class*="dropdown-" i],li')).filter(visible)
   : [];
 const controlRect = control.getBoundingClientRect();
 const nearControl = node => {
   const rect = node.getBoundingClientRect();
   return rect.right >= controlRect.left - 40 && rect.left <= controlRect.right + 240
     && rect.bottom >= controlRect.top - 120 && rect.top <= controlRect.bottom + 700;
 };
 const noise = text => /^(\d+ results? available\.?|no options|loading\.\.\.)$/i.test(text)
   || /use up and down|press enter to select|press escape to exit|press tab to select/i.test(text);
 const freshLeaves = Array.from(document.body.querySelectorAll('*')).filter(node =>
   visible(node) && !window.__dsComboVisibleBaseline?.has(node) &&
   !Array.from(node.children).some(child => visible(child) && clean(child.innerText || child.textContent)) &&
   nearControl(node) && clean(node.innerText || node.textContent).length <= 240 &&
   !noise(clean(node.innerText || node.textContent)));
 const options = Array.from(new Set(roleOptions.concat(freshLeaves)));
 // A suggestion list answers a partial query with a fuller string: type "Cuautitlan Izcalli" and
 // the option comes back "Cuautitlán Izcalli, México, MEX". Requiring equality left every one of
 // those unpicked — and on a control that accepts nothing but a picked option, unpicked means the
 // answer never landed at all. Equality still wins where it exists; the looser readings are ordered
 // most-specific-first and carry a length floor so a two-letter query cannot select a country.
 const optionText = option => norm(option.innerText || option.textContent);
 const exact = options.find(option => optionText(option) === wanted)
   || (wanted.length >= 3 ? options.find(option => optionText(option).startsWith(wanted)) : null)
   || (wanted.length >= 4 ? options.find(option => optionText(option).includes(wanted)) : null)
   || (wanted.length >= 4 ? options.find(option => {
        const text = optionText(option);
        return text.length >= 4 && wanted.startsWith(text);
      }) : null);
 if (exact) {
   exact.scrollIntoView({block:'nearest',inline:'nearest',behavior:'instant'});
   // The host clicks these coordinates on a later round trip, so they have to still mean this option
   // by the time it does. scrollIntoView can move the page under a floating menu, and a rectangle
   // read on the wrong side of that scroll names whatever now sits at that point: the click missed,
   // the option stayed unpicked, and the field reported "not confirmed" while the menu underneath it
   // was showing the right answer. Ashby's Location came back at y=122 on a menu that was at y=400.
   const at = () => {
     const rect = exact.getBoundingClientRect();
     return { x: rect.left + rect.width/2, y: rect.top + rect.height/2 };
   };
   const hits = point => {
     const el = document.elementFromPoint(point.x, point.y);
     return !!el && (el === exact || exact.contains(el) || el.contains(exact));
   };
   let point = at();
   if (!hits(point)) point = at();
   if (hits(point)) {
     // Stash it so the host can re-read the rectangle immediately before it clicks. A floating menu
     // repositions itself a frame or two after the scroll above, which is inside the round trip
     // between this answer and the click that acts on it.
     window.__dsComboOption = exact;
     return { ok:true, text:String(exact.innerText || exact.textContent || '').trim(),
       method:'mouse-target', x:point.x, y:point.y };
   }
   // Rather than click a point that is not the option, fall through to the keyboard: the exact match
   // is highlighted by the typing, and Enter takes what is highlighted.
 }
 const rect = input.getBoundingClientRect();
 return { ok:true, text:'', method:'enter-target', x:rect.left + rect.width/2, y:rect.top + rect.height/2 };
})()
""".Replace("__PAYLOAD__", payload);
    }

    /// <summary>Confirms that a custom dropdown displays a chosen value, not search text.</summary>
    public static string BuildComboboxVerifyScript(int index, string value)
    {
        var payload = JsonSerializer.Serialize(new { index, value });
        return """
(() => {
 const request = __PAYLOAD__;
 const control = (window.__dsCombos || [])[request.index];
 if (!control || !control.isConnected) return { ok:false, text:'' };
 // Accents are folded, not deleted. Stripping them as punctuation turned "Cuautitlán" into
 // "cuautitl n", so a typed "Cuautitlan" matched none of the suggestions a Mexican city returns
 // and the pick never happened.
 const norm = s => String(s || '').normalize('NFD').replace(/[̀-ͯ]/g, '')
   .toLowerCase().replace(/[^a-z0-9]+/g, ' ').trim();
 // Walk up for the chosen value rather than guessing one ancestor. Element.closest() matches the
 // element itself, and this control IS an <input role="combobox">, so the old lookup resolved to the
 // input — which has no children and could therefore never hold a value. Every dropdown reported
 // "not confirmed, leaving it for review" whether the selection had worked or not; a real run left
 // a parked tab showing select__single-value = "Cuautitlán Izcalli, México, Mexico" on a field the
 // trace had just called empty. Stop before an ancestor with a second combobox in it, so this can
 // never read the neighbouring dropdown's answer and call this one confirmed.
 // A suggestion field keeps its answer in a hidden partner rather than in a rendered value element,
 // so look there first. This script carries its own helpers and does not share the fill prelude, and
 // that is exactly how it came to disagree with it: the pick had worked, Lever's hidden
 // selectedLocation held the chosen city, and the verify still reported the field empty.
 const hiddenPartner = (() => {
   const block = control.closest('[class*="field" i],[data-field],[data-field-entry-id],fieldset,li');
   if (!block) return null;
   return Array.from(block.querySelectorAll('input[type="hidden"]'))
     .find(h => /select|chosen|resolved/i.test((h.name || '') + ' ' + (h.id || ''))) || null;
 })();
 if (hiddenPartner) {
   const held = String(hiddenPartner.value || '').trim();
   const shown = String(control.value || '').trim();
   return { ok: held.length > 0, text: (shown || held).slice(0,120),
     error: held.length > 0 ? '' : 'no option was picked, so the field holds nothing the form will accept' };
 }

 const comboish = '[role="combobox"],[aria-haspopup="listbox"],[aria-autocomplete="list"]';
 let chosen = null, box = control.parentElement;
 for (let up = 0; up < 6 && box; up++) {
   if (box.querySelectorAll(comboish).length > 1) break;
   chosen = box.querySelector('[class*="single-value" i],[class*="singleValue" i],[class*="multi-value" i],[class*="multiValue" i]');
   if (chosen) break;
   box = box.parentElement;
 }
 const expanded = control.getAttribute('aria-expanded') === 'true';
 const text = String(chosen?.innerText || chosen?.textContent || control.getAttribute('aria-valuetext') || '').trim();
 const actual = norm(text), wanted = norm(request.value);
 return { ok:!expanded && !!actual && (actual === wanted || actual.includes(wanted) || wanted.includes(actual)), text:text.slice(0,120) };
})()
""".Replace("__PAYLOAD__", payload);
    }

    /// <summary>
    /// Re-reads where the chosen option is, immediately before the host clicks it.
    ///
    /// <para>
    /// The commit script scrolls the option into view and measures it in the same synchronous task,
    /// but a floating menu repositions itself a frame or two later — and the click happens on a
    /// separate round trip after that. Ashby's Location came back at y=122 for a menu that settled at
    /// y=400, so the click landed on the page behind the list, the option stayed unpicked, and the
    /// form was rejected for a required field whose answer was visible on screen at the time.
    /// </para>
    /// </summary>
    public static readonly string ComboboxRetargetScript = """
(() => {
 const option = window.__dsComboOption;
 if (!option || !option.isConnected) return { ok:false, error:'the option is gone' };
 const visible = e => !!(e && (e.offsetWidth || e.offsetHeight || e.getClientRects().length));
 if (!visible(option)) return { ok:false, error:'the option is no longer visible' };
 const rect = option.getBoundingClientRect();
 const x = rect.left + rect.width/2, y = rect.top + rect.height/2;
 if (x < 0 || y < 0 || x > window.innerWidth || y > window.innerHeight)
   return { ok:false, error:'the option is outside the viewport at ' + Math.round(x) + ',' + Math.round(y) };
 const at = document.elementFromPoint(x, y);
 if (!at || (at !== option && !option.contains(at) && !at.contains(option)))
   return { ok:false, error:'something else is at ' + Math.round(x) + ',' + Math.round(y) };
 return { ok:true, x, y, text:String(option.innerText || option.textContent || '').trim() };
})()
""";

    /// <summary>
    /// Asks whether the site has taken the resume, rather than whether we handed it over.
    ///
    /// <para>
    /// Setting a file input's files is instant; what follows is not. Ashby posts the file to its own
    /// server on the change event and only then counts the field as answered, so a Submit fired in
    /// the meantime is rejected for a missing resume — intermittently, depending on how long the
    /// rest of the form took to fill. The file being in the input is our side of it; the file's name
    /// appearing on the page is the site's.
    /// </para>
    /// </summary>
    public static string BuildResumeAttachedScript(string fileName)
    {
        var payload = JsonSerializer.Serialize(new { fileName });
        return """
(() => {
 const request = __PAYLOAD__;
 const visible = e => !!(e && (e.offsetWidth || e.offsetHeight || e.getClientRects().length));
 const norm = s => String(s || '').toLowerCase().replace(/\s+/g, ' ').trim();
 const wanted = norm(request.fileName);
 const inputs = Array.from(document.querySelectorAll('input[type="file"]'));
 const held = inputs.some(input => (input.files || []).length > 0);
 const body = norm(document.body ? document.body.innerText : '');
 // The full file name, extension and all. Matching the stem as well looked more forgiving and was
 // simply wrong: this profile's resume is Fernando.pdf and the applicant is Fernando, so "fernando"
 // is on the form the moment the name field is filled — the check reported the resume attached
 // before it had been.
 const named = wanted.length > 0 && body.includes(wanted);
 // Anything still in flight, so a slow upload is waited out rather than mistaken for a failure.
 const busy = Array.from(document.querySelectorAll(
   '[role="progressbar"],progress,[class*="upload" i],[class*="progress" i],[class*="spinner" i]'))
   .some(e => visible(e) && /uploading|processing|\d{1,3}\s*%/.test(norm(e.innerText || e.getAttribute('aria-label'))));
 // Either side is enough. Greenhouse hands the file to its own uploader and removes the native
 // input entirely — there were no file inputs left on the page at all — so demanding that our file
 // still be sitting in one could never be satisfied there. Ashby keeps the input and renders the
 // name. What must not happen is waiting on evidence a site never produces.
 return { held, named, busy, ok: !busy && (held || named),
   quiet: !held && !named && !busy };
})()
""".Replace("__PAYLOAD__", payload);
    }

    /// <summary>
    /// Locates the verification-code box on a confirmed application, and the control that submits it.
    /// The same reading that decided the application was not finished — an empty, enabled field
    /// carrying <c>autocomplete="one-time-code"</c> or a label that says so.
    /// </summary>
    public static readonly string CodeFieldTargetScript = """
(() => {
 const visible = e => !!(e && (e.offsetWidth || e.offsetHeight || e.getClientRects().length));
 const norm = s => String(s || '').toLowerCase().replace(/\s+/g, ' ').trim();
 const attr = (e, n) => (e && e.getAttribute && e.getAttribute(n)) || '';
 const labelOf = e => {
   const forId = e.id ? document.querySelector('label[for="' + CSS.escape(e.id) + '"]') : null;
   const own = e.labels && e.labels.length ? Array.from(e.labels).map(l => l.innerText).join(' ') : '';
   const box = e.closest('[class*="field" i],fieldset,li,div');
   return norm((forId ? forId.innerText : '') + ' ' + own + ' ' + attr(e,'aria-label') + ' ' +
     (e.placeholder || '') + ' ' + (e.name || '') + ' ' + (e.id || '') + ' ' +
     ((box && box.innerText) || '').slice(0, 120));
 };
 const codeWords = /\b(code|verification|verify|otp|one[- ]time|passcode|pin)\b/;
 const field = Array.from(document.querySelectorAll('input,textarea')).find(e =>
   visible(e) && !e.disabled && !e.readOnly && String(e.value || '').trim().length === 0 &&
   (norm(attr(e,'autocomplete')) === 'one-time-code' || codeWords.test(labelOf(e))));
 if (!field) return { ok:false, error:'no empty verification-code field is on this page' };
 field.scrollIntoView({block:'center',inline:'nearest',behavior:'instant'});
 const rect = field.getBoundingClientRect();
 if (rect.width <= 0 || rect.height <= 0) return { ok:false, error:'the code field has no rectangle' };
 // The control that sends it. Scoped to the field's own area first, because a confirmation page can
 // still carry the application's own buttons elsewhere.
 const box = field.closest('form,[class*="field" i],fieldset,section,div') || document.body;
 const sendWords = /\b(verify|submit|confirm|continue|send|done|finish)\b/;
 const button = Array.from(box.querySelectorAll('button,input[type="submit"],[role="button"]'))
     .find(b => visible(b) && !b.disabled && sendWords.test(norm(b.innerText || b.value || attr(b,'aria-label'))))
   || Array.from(document.querySelectorAll('button,input[type="submit"],[role="button"]'))
     .find(b => visible(b) && !b.disabled && sendWords.test(norm(b.innerText || b.value || attr(b,'aria-label'))));
 let send = null;
 if (button) {
   const r = button.getBoundingClientRect();
   send = { x: r.left + r.width/2, y: r.top + r.height/2,
     label: norm(button.innerText || button.value || attr(button,'aria-label')).slice(0, 40) };
 }
 return { ok:true, x: rect.left + rect.width/2, y: rect.top + rect.height/2,
   label: norm(attr(field,'aria-label') || field.placeholder || field.name).slice(0, 60), send };
})()
""";

    /// <summary>Dismisses an uncommitted search so the discovery pass can reopen it cleanly.</summary>
    public static string BuildComboboxCloseScript(int index)
    {
        var payload = JsonSerializer.Serialize(new { index });
        return """
(() => {
 const request = __PAYLOAD__;
 const control = (window.__dsCombos || [])[request.index];
 const input = window.__dsComboInput || control;
 if (!input) return false;
 for (const type of ['keydown','keyup'])
   input.dispatchEvent(new KeyboardEvent(type,{key:'Escape',code:'Escape',bubbles:true,cancelable:true}));
 input.blur();
 if (typeof PointerEvent === 'function')
   document.body.dispatchEvent(new PointerEvent('pointerdown',{bubbles:true,cancelable:true,pointerType:'mouse',isPrimary:true}));
 document.body.dispatchEvent(new MouseEvent('mousedown',{bubbles:true,cancelable:true,button:0,buttons:1}));
 document.body.dispatchEvent(new MouseEvent('mouseup',{bubbles:true,cancelable:true,button:0}));
 document.body.click();
 return true;
})()
""".Replace("__PAYLOAD__", payload);
    }

    /// <summary>
    /// The unanswered questions, worded exactly as the page words them.
    ///
    /// <para>
    /// Built on the same prelude as the fill, and that is the whole point: these strings become the
    /// keys of ChatGPT's answer object, and the fill then looks each field up by its own label. When
    /// the two disagreed, nothing matched — this used to concatenate every label it could reach, so
    /// an Ashby field came back as "Name Name Type here..." while the filler asked for "name", and
    /// every answer ChatGPT returned was discarded.
    /// </para>
    /// </summary>
    public static readonly string QuestionsScript = """
(() => {
__PRELUDE__
 const chosen = e => !!chosenValueFor(e);
 const answered = e => choice(e) ? choiceSelected(e)
   : e.tagName === 'SELECT' ? e.selectedIndex > 0 && !!e.value
   : !!String(e.value || '').trim();
 // A radio group is answered when any of its options is — otherwise the same question is asked
 // once per unchosen option, which is how three answer keys came back for one question.
 const groupAnswered = e => choice(e) ? choiceOptions(e).some(choiceSelected) : answered(e);
 const fields = Array.from(new Set(Array.from(document.querySelectorAll(
   'input:not([type="hidden"]):not([type="file"]),textarea,select,' + choiceSelector))))
   .filter(e => (visible(e) || choiceVisible(e)) && !mirroredChoice(e)
     && !e.disabled && !groupAnswered(e) && !comboInput(e));
 const combos = Array.from(document.querySelectorAll(comboSelector))
   .filter(e => visible(e) && e.getAttribute('aria-disabled') !== 'true' && !norm(e.value) && !chosen(e));
 // The candidate answers, so ChatGPT picks from the list instead of inventing wording the control
 // will then reject. A react-select keeps its options out of the DOM until it is opened, so those
 // come back empty and are flagged as a dropdown rather than pretended to be free text.
 const optionsFor = e => {
   if (e.tagName === 'SELECT')
     return Array.from(e.options)
       .filter(option => String(option.value || '').trim() !== '')
       .map(option => String(option.text || '').replace(/\s+/g, ' ').trim())
       .filter(Boolean);
   if (choice(e))
     return choiceOptions(e).map(choiceLabel).filter(Boolean);
   return [];
 };
 const seen = new Set();
 const asked = [];
 for (const e of fields.concat(combos)) {
   const question = labelFor(e);
   if (!question || protectedField(norm(question))) continue;
   const key = norm(question);
   if (seen.has(key)) continue;
   seen.add(key);
   const options = optionsFor(e);
   const item = { question };
   if (options.length) item.options = options;
   if (e.type === 'checkbox') item.multiple = true;
   if (!options.length && (comboInput(e) || e.tagName === 'SELECT')) item.type = 'dropdown';
   asked.push(item);
   if (asked.length >= 80) break;
 }
 return asked;
})()
""".Replace("__PRELUDE__", MatchingPrelude).Replace("__PAYLOAD__", "{\"adapter\":\"\",\"values\":{}}");
}
