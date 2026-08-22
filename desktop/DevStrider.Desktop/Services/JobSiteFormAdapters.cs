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

    public static string BuildFillScript(Uri uri, IReadOnlyDictionary<string, string> values)
    {
        var payload = JsonSerializer.Serialize(new { adapter = NameFor(uri), values });
        return """
(() => {
 const payload = __PAYLOAD__;
 const norm = s => String(s || '').toLowerCase().replace(/[^a-z0-9]+/g, ' ').trim();
 const visible = e => !!(e.offsetWidth || e.offsetHeight || e.getClientRects().length);
 const adapter = payload.adapter === 'Default (generic)' &&
   document.querySelector('a[href*="teamtailor.com" i],script[src*="teamtailor" i],[data-teamtailor]')
     ? 'Teamtailor' : payload.adapter;
 const protectedField = text => /\b(acknowledg|agree|agreement|attest|certif|consent|privacy|signature|terms|truthful|gender|race|ethnic|disabil|veteran|salary|compensation|work authori|sponsor|visa)\b/.test(text);
 const textFor = e => {
   const labels = e.labels ? Array.from(e.labels).map(x => x.innerText) : [];
   const forLabel = e.id ? document.querySelector(`label[for="${CSS.escape(e.id)}"]`) : null;
   const group = e.closest('fieldset, .field, .application-question, .question, .form-group, [class*="question" i]');
   const nearby = e.closest('label, li, div');
   return norm([e.name, e.id, e.getAttribute('aria-label'), e.placeholder, group?.querySelector('legend,[data-question-label],label')?.innerText, ...labels, forLabel?.innerText, nearby?.querySelector('label,legend')?.innerText].filter(Boolean).join(' '));
 };
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
 const setText = (e, v) => { const proto = e instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype; const setter = Object.getOwnPropertyDescriptor(proto, 'value')?.set; setter ? setter.call(e, v) : e.value = v; e.dispatchEvent(new Event('input',{bubbles:true})); e.dispatchEvent(new Event('change',{bubbles:true})); e.dispatchEvent(new Event('blur',{bubbles:true})); };
 const optionText = e => norm([e.value,e.getAttribute('aria-label'),...(e.labels ? Array.from(e.labels).map(x=>x.innerText) : [])].filter(Boolean).join(' '));
 const setSelect = (e, v) => { const wanted = norm(v); const option = Array.from(e.options).find(o => { const text=norm(o.text); const value=norm(o.value); return text===wanted || value===wanted || text.includes(wanted) || wanted.includes(text); }); if (!option) return false; e.value = option.value; e.dispatchEvent(new Event('input',{bubbles:true})); e.dispatchEvent(new Event('change',{bubbles:true})); return true; };
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
 for (const e of Array.from(document.querySelectorAll('input:not([type="hidden"]), textarea, select'))) { if (e.type === 'file' || !visible(e) || e.disabled || answered(e)) continue; const v=valueFor(e); if (v === null) { skipped++; continue; } if (write(e,v)) { filled++; touched.push(textFor(e)); } else skipped++; }
 return { adapter, filled, skipped, touched };
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
