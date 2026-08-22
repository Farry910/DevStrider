using System.Text.Json;

namespace DevStrider.Desktop.Services;

/// <summary>
/// Host-aware form filler. The JavaScript deliberately never submits forms or assigns file inputs.
/// Specific adapters provide stable core-field selectors; every other question uses the guarded
/// label/accessible-name matcher as a low-confidence fallback.
/// </summary>
public static class JobSiteFormAdapters
{
    /// <summary>Opens an application form but never clicks Next, Continue, or final Submit.</summary>
    public const string OpenApplicationScript = """
(() => {
 const norm = s => String(s || '').toLowerCase().replace(/\s+/g,' ').trim();
 const visible = e => !!(e.offsetWidth || e.offsetHeight || e.getClientRects().length);
 const candidates = Array.from(document.querySelectorAll('a,button,input[type="button"]')).filter(visible);
 const target = candidates.find(e => {
   const text = norm(e.innerText || e.value || e.getAttribute('aria-label') || e.title);
   if (/\b(submit|continue|next|confirm|send application)\b/.test(text)) return false;
   return /^(apply|apply now|apply for this job|start application|start your application)$/.test(text);
 });
 if (!target) return { clicked:false, label:'' };
 if (target instanceof HTMLAnchorElement) target.removeAttribute('target');
 const label = norm(target.innerText || target.value || target.getAttribute('aria-label'));
 target.click();
 return { clicked:true, label };
})()
""";

    public static string NameFor(Uri uri)
    {
        var host = uri.Host.ToLowerInvariant();
        if (host.Contains("greenhouse.io")) return "Greenhouse";
        if (host.Contains("ashbyhq.com") || host.Contains("ashby")) return "Ashby";
        if (host.Contains("lever.co")) return "Lever";
        if (host.Contains("applytojob")) return "ApplyToJob";
        return "Default (generic)";
    }

    /// <summary>
    /// Label matching and value lookup, shared by the fill pass and the combobox pass so one rule
    /// decides what a control is asking and which saved answer belongs in it. Expects a
    /// <c>__PAYLOAD__</c> placeholder carrying <c>{ adapter, values }</c>.
    /// </summary>
    private const string MatchingPrelude = """
 const payload = __PAYLOAD__;
 const norm = s => String(s || '').toLowerCase().replace(/[^a-z0-9]+/g, ' ').trim();
 const visible = e => !!(e.offsetWidth || e.offsetHeight || e.getClientRects().length);
 // Only what the user has said this app is never given. Everything else — demographics, salary,
 // work authorisation, sponsorship, legal attestations, date of birth — is answerable, because the
 // user supplies those answers themselves in Job Operations; they are simply never invented.
 const protectedField = text => /\b(social security|ssn|passport|national id|id number|identity number|driver licen|bank account|routing number|sort code|iban|credit card|card number|tax id|nino)\b/.test(text);
 const attr = (e, name) => e.getAttribute(name) || '';
 const labelledBy = e => attr(e, 'aria-labelledby').split(/\s+/).filter(Boolean)
   .map(id => document.getElementById(id)?.innerText || '').join(' ').trim();
 // ONE authoritative label, taken from the strongest association the page actually commits to.
 // Never widen to "the first label inside some ancestor div": on a two-column form that is the
 // neighbouring field's label, and because the old version concatenated every candidate into a
 // single haystack, the wrong one won. That is how a Country dropdown ends up holding a phone
 // number and "how many years of experience" ends up holding a LinkedIn URL.
 const textFor = e => {
   const forLabel = e.id ? document.querySelector(`label[for="${CSS.escape(e.id)}"]`)?.innerText : '';
   const own = e.labels && e.labels.length ? Array.from(e.labels).map(x => x.innerText).join(' ') : '';
   return norm(labelledBy(e) || attr(e, 'description') || forLabel || own
     || attr(e, 'aria-label') || e.placeholder || attr(e, 'name') || e.id);
 };
 // react-select and friends render a real <input> as their search box. Typing into it looks like a
 // successful fill and selects nothing, so it belongs to the combobox pass, not the text pass.
 const comboInput = e => e.getAttribute('role') === 'combobox'
   || e.getAttribute('aria-autocomplete') === 'list'
   || /(^|\s)select__input(\s|$)/.test(String(e.className || ''));
 const aliases = { 'full name':['full name','candidate name'], 'first name':['first name','given name'], 'last name':['last name','family name','surname'], email:['email','email address'], phone:['phone','phone number','mobile'], linkedin:['linkedin','linkedin url'], location:['location','city'] };
 const valueFor = e => {
   const hay = textFor(e); if (!hay || protectedField(hay)) return null;
   for (const [raw, value] of Object.entries(payload.values || {})) {
     const key = norm(raw); if (!key || value === '') continue;
     const keys = aliases[key] || [key];
     if (keys.some(k => hay.includes(k))) return String(value);
   }
   return null;
 };
 // Same association order as textFor, kept unnormalised so a human reads the real question.
 const labelFor = e => {
   const forLabel = e.id ? document.querySelector(`label[for="${CSS.escape(e.id)}"]`)?.innerText : '';
   const raw = labelledBy(e) || attr(e, 'description') || forLabel || (e.labels && e.labels[0]?.innerText)
     || attr(e, 'aria-label') || e.placeholder || attr(e, 'name') || e.id || '';
   return String(raw).replace(/\s+/g, ' ').trim().slice(0, 80);
 };
""";

    public static string BuildFillScript(Uri uri, IReadOnlyDictionary<string, string> values)
    {
        var payload = JsonSerializer.Serialize(new { adapter = NameFor(uri), values });
        return """
(() => {
__PRELUDE__
 const adapter = payload.adapter === 'Default (generic)' &&
   document.querySelector('a[href*="teamtailor.com" i],script[src*="teamtailor" i],[data-teamtailor]')
     ? 'Teamtailor' : payload.adapter;
 const setText = (e, v) => { const proto = e instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype; const setter = Object.getOwnPropertyDescriptor(proto, 'value')?.set; setter ? setter.call(e, v) : e.value = v; e.dispatchEvent(new Event('input',{bubbles:true})); e.dispatchEvent(new Event('change',{bubbles:true})); e.dispatchEvent(new Event('blur',{bubbles:true})); };
 const optionText = e => norm([e.value,e.getAttribute('aria-label'),...(e.labels ? Array.from(e.labels).map(x=>x.innerText) : [])].filter(Boolean).join(' '));
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
   setter ? setter.call(e, option.value) : e.value = option.value;
   e.dispatchEvent(new Event('input',{bubbles:true}));
   e.dispatchEvent(new Event('change',{bubbles:true}));
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
 const write = (e,v) => { if (!e || !visible(e) || e.disabled || e.readOnly || e.type === 'file' || protectedField(textFor(e))) return false; if (e.tagName === 'SELECT') { if (!setSelect(e,v)) return false; } else if (e.type === 'checkbox') { const wanted=norm(v); const option=optionText(e); const yes=['true','yes','y','1'].includes(wanted); const no=['false','no','n','0'].includes(wanted); const matchesOption=option && (wanted===option || wanted.includes(option)); const shouldCheck=matchesOption || yes; if (!matchesOption && !yes && !no) return false; if (e.checked === shouldCheck) return false; e.click(); } else if (e.type === 'radio') { const option=optionText(e); const wanted=norm(v); if (!option || !(option===wanted || option.includes(wanted) || wanted.includes(option)) || e.checked) return false; e.click(); } else setText(e,v); return true; };
 const answered = e => e.type === 'checkbox' || e.type === 'radio' ? e.checked : e.tagName === 'SELECT' ? e.selectedIndex > 0 && !!e.value : !!String(e.value || '').trim();
 for (const [key, selectors] of Object.entries(core[adapter] || core['Default (generic)'])) { const v = payload.values[key]; if (!v) continue; for (const selector of selectors) { const e=document.querySelector(selector); if (write(e,v)) { filled++; touched.push(key); break; } } }
 for (const e of Array.from(document.querySelectorAll('input:not([type="hidden"]), textarea, select'))) { if (e.type === 'file' || comboInput(e) || !visible(e) || e.disabled || answered(e)) continue; const v=valueFor(e); if (v === null) { skipped++; continue; } if (write(e,v)) { filled++; touched.push(textFor(e)); } else skipped++; }
 // Report what a human still has to complete. Protected questions are in here by design — the
 // adapters never answer legal, demographic or work-authorisation fields — and so is any dropdown
 // no saved answer matched, which is otherwise invisible until the form rejects the submission.
 const chosen = e => { const box = e.closest('[class*="select" i]:not(input)') || e.parentElement; return !!box?.querySelector('[class*="single-value" i],[class*="singleValue" i],[class*="multi-value" i],[class*="multiValue" i]'); };
 const custom = Array.from(document.querySelectorAll('[role="combobox"],[aria-haspopup="listbox"],[aria-autocomplete="list"]'))
   .filter(e => visible(e) && e.getAttribute('aria-disabled') !== 'true' && !norm(e.value) && !chosen(e));
 const outstanding = Array.from(document.querySelectorAll('input:not([type="hidden"]):not([type="file"]),textarea,select'))
   .filter(e => visible(e) && !comboInput(e) && !e.disabled && !e.readOnly && !answered(e))
   .map(e => { const l = labelFor(e); return l && e.tagName === 'SELECT' ? l + ' (dropdown)' : l; })
   .concat(custom.map(e => { const l = labelFor(e); return l ? l + ' (dropdown)' : ''; }))
   .filter(Boolean);
 return { adapter, filled, skipped, touched, unfilled: Array.from(new Set(outstanding)).slice(0, 25) };
})()
""".Replace("__PRELUDE__", MatchingPrelude).Replace("__PAYLOAD__", payload);
    }

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
    public static string BuildComboboxPlanScript(IReadOnlyDictionary<string, string> values)
    {
        var payload = JsonSerializer.Serialize(new { adapter = "", values });
        return """
(() => {
__PRELUDE__
 const shell = e => e.closest('[class*="select" i]:not(input)') || e.parentElement || e;
 // Answered means the widget renders a chosen value, not merely that its search box holds text.
 const answered = e => !!norm(e.value)
   || !!shell(e).querySelector('[class*="single-value" i],[class*="singleValue" i],[class*="multi-value" i],[class*="multiValue" i]');
 const controls = Array.from(document.querySelectorAll(
   '[role="combobox"],[aria-haspopup="listbox"],[aria-autocomplete="list"]'))
   .filter(e => visible(e) && e.getAttribute('aria-disabled') !== 'true' && !e.disabled && !answered(e));
 const plan = [];
 window.__dsCombos = [];
 for (const control of controls) {
   const value = valueFor(control) ?? valueFor(shell(control));
   if (value === null) continue;
   window.__dsCombos.push(control);
   plan.push({ index: window.__dsCombos.length - 1, label: labelFor(control) || labelFor(shell(control)), value });
 }
 return plan;
})()
""".Replace("__PRELUDE__", MatchingPrelude).Replace("__PAYLOAD__", payload);
    }

    /// <summary>Opens dropdown <c>index</c> and types its answer so the option list filters.</summary>
    public static string BuildComboboxTypeScript(int index, string value)
    {
        var payload = JsonSerializer.Serialize(new { index, value });
        return """
(() => {
 const request = __PAYLOAD__;
 const control = (window.__dsCombos || [])[request.index];
 if (!control) return { ok:false, error:'gone' };
 const visible = e => !!(e.offsetWidth || e.offsetHeight || e.getClientRects().length);
 control.scrollIntoView({ block:'center' });
 control.dispatchEvent(new MouseEvent('mousedown',{bubbles:true}));
 control.click();
 // Controls that are not themselves inputs reveal one once opened; react-select is the common case.
 const input = control.tagName === 'INPUT' ? control
   : Array.from((control.closest('[class*="select" i]') || document).querySelectorAll('input'))
       .find(e => visible(e) && !e.disabled && !e.readOnly);
 if (!input) return { ok:false, error:'no input to type into' };
 input.focus();
 const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value')?.set;
 setter ? setter.call(input, request.value) : input.value = request.value;
 input.dispatchEvent(new Event('input',{bubbles:true}));
 window.__dsComboInput = input;
 return { ok:true, error:'' };
})()
""".Replace("__PAYLOAD__", payload);
    }

    /// <summary>
    /// Commits the filtered list with Enter, then reports what the control ended up showing so the
    /// caller can count a real selection rather than assume one.
    /// </summary>
    public static string BuildComboboxCommitScript(int index)
    {
        var payload = JsonSerializer.Serialize(new { index });
        return """
(() => {
 const request = __PAYLOAD__;
 const control = (window.__dsCombos || [])[request.index];
 const input = window.__dsComboInput;
 if (!control || !input) return { ok:false, text:'' };
 const norm = s => String(s || '').toLowerCase().replace(/[^a-z0-9]+/g, ' ').trim();
 // keyCode and which are read-only on KeyboardEvent, and libraries older than event.key still
 // read them, so they are defined onto each event before it is dispatched.
 for (const type of ['keydown','keypress','keyup']) {
   const event = new KeyboardEvent(type, { key:'Enter', code:'Enter', bubbles:true, cancelable:true });
   Object.defineProperty(event, 'keyCode', { get: () => 13 });
   Object.defineProperty(event, 'which', { get: () => 13 });
   input.dispatchEvent(event);
 }
 input.dispatchEvent(new Event('change',{bubbles:true}));
 const shell = control.closest('[role="combobox"],[aria-haspopup="listbox"],[class*="select" i]') || control;
 const text = norm(input.value) || norm(shell.innerText);
 return { ok: !!text, text: String(text).slice(0,60) };
})()
""".Replace("__PAYLOAD__", payload);
    }

    public const string QuestionsScript = """
(() => {
 const norm = s => String(s || '').replace(/\s+/g,' ').trim();
 const visible = e => !!(e.offsetWidth || e.offsetHeight || e.getClientRects().length);
 const label = e => { const ls=e.labels ? Array.from(e.labels).map(x=>x.innerText).join(' ') : ''; const forLabel=e.id ? document.querySelector(`label[for="${CSS.escape(e.id)}"]`)?.innerText : ''; const group=e.closest('fieldset,.field,.application-question,.question,.form-group,[class*="question" i]'); const question=group?.querySelector('legend,[data-question-label],label')?.innerText; return norm(question || [ls,forLabel,e.getAttribute('aria-label'),e.placeholder].filter(Boolean).join(' ')); };
 const protectedField = text => /\b(acknowledg|agree|agreement|attest|certif|consent|privacy|signature|terms|truthful|gender|race|ethnic|disabil|veteran|salary|compensation|work authori|sponsor|visa)\b/i.test(text);
 const answered = e => e.type === 'checkbox' || e.type === 'radio' ? e.checked : e.tagName === 'SELECT' ? e.selectedIndex > 0 && !!e.value : !!String(e.value || '').trim();
 return Array.from(document.querySelectorAll('input:not([type="hidden"]):not([type="file"]),textarea,select')).filter(e=>visible(e)&&!e.disabled&&!answered(e)).map(label).filter((v,i,a)=>v&&!protectedField(v)&&a.indexOf(v)===i).slice(0,80);
})()
""";
}
