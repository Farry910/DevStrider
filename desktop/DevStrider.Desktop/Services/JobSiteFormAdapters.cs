using System.Text.Json;

namespace DevStrider.Desktop.Services;

/// <summary>
/// Host-aware form filler. The JavaScript deliberately never submits forms or assigns file inputs.
/// Specific adapters provide stable core-field selectors; every other question uses the guarded
/// label/accessible-name matcher as a low-confidence fallback.
/// </summary>
public static class JobSiteFormAdapters
{
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
 const protectedField = text => /\b(acknowledg|agree|agreement|attest|certif|consent|privacy|signature|terms|truthful)\b/.test(text);
 const textFor = e => {
   const labels = e.labels ? Array.from(e.labels).map(x => x.innerText) : [];
   const forLabel = e.id ? document.querySelector(`label[for="${CSS.escape(e.id)}"]`) : null;
   const nearby = e.closest('label, .field, .application-question, .question, .form-group, li, div');
   return norm([e.name, e.id, e.getAttribute('aria-label'), e.placeholder, ...labels, forLabel?.innerText, nearby?.querySelector('label,legend')?.innerText].filter(Boolean).join(' '));
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
 const setSelect = (e, v) => { const wanted = norm(v); const option = Array.from(e.options).find(o => norm(o.text).includes(wanted) || norm(o.value) === wanted); if (!option) return false; e.value = option.value; e.dispatchEvent(new Event('input',{bubbles:true})); e.dispatchEvent(new Event('change',{bubbles:true})); return true; };
 const core = {
   'Greenhouse': { 'first name':['#first_name','input[name="first_name"]'], 'last name':['#last_name','input[name="last_name"]'], email:['#email','input[name="email"]'], phone:['#phone','input[name="phone"]'] },
   'Ashby': { 'full name':['input[name="name"]','input[autocomplete="name"]'], email:['input[type="email"]','input[name="email"]'], phone:['input[type="tel"]','input[name="phone"]'] },
   'Lever': { 'full name':['input[name="name"]','input[autocomplete="name"]'], email:['input[name="email"]','input[type="email"]'], phone:['input[name="phone"]','input[type="tel"]'] },
   'ApplyToJob': { 'first name':['input[name*="first" i]'], 'last name':['input[name*="last" i]'], email:['input[type="email"]'], phone:['input[type="tel"]'] }
 };
 let filled=0, skipped=0; const touched=[];
 const write = (e,v) => { if (!e || !visible(e) || e.disabled || e.readOnly || e.type === 'file' || protectedField(textFor(e))) return false; if (e.tagName === 'SELECT') { if (!setSelect(e,v)) return false; } else if (e.type === 'checkbox') { const yes=['true','yes','y','1'].includes(norm(v)); const no=['false','no','n','0'].includes(norm(v)); if (!yes && !no) return false; if (e.checked === yes) return false; e.click(); } else if (e.type === 'radio') { const label=norm(textFor(e)); if (!label.includes(norm(v)) || e.checked) return false; e.click(); } else setText(e,v); return true; };
 for (const [key, selectors] of Object.entries(core[payload.adapter] || {})) { const v = payload.values[key]; if (!v) continue; for (const selector of selectors) { const e=document.querySelector(selector); if (write(e,v)) { filled++; touched.push(key); break; } } }
 for (const e of Array.from(document.querySelectorAll('input:not([type="hidden"]), textarea, select'))) { if (e.type === 'file' || !visible(e) || e.disabled || e.value || e.checked) continue; const v=valueFor(e); if (v === null) { skipped++; continue; } if (write(e,v)) { filled++; touched.push(textFor(e)); } else skipped++; }
 return { adapter: payload.adapter, filled, skipped, touched };
})()
""".Replace("__PAYLOAD__", payload);
    }

    public const string QuestionsScript = """
(() => {
 const norm = s => String(s || '').replace(/\s+/g,' ').trim();
 const visible = e => !!(e.offsetWidth || e.offsetHeight || e.getClientRects().length);
 const label = e => { const ls=e.labels ? Array.from(e.labels).map(x=>x.innerText).join(' ') : ''; const forLabel=e.id ? document.querySelector(`label[for="${CSS.escape(e.id)}"]`)?.innerText : ''; const box=e.closest('.field,.application-question,.question,.form-group,li,div'); return norm([ls,forLabel,e.getAttribute('aria-label'),e.placeholder,box?.querySelector('label,legend')?.innerText].filter(Boolean).join(' ')); };
 return Array.from(document.querySelectorAll('input:not([type="hidden"]):not([type="file"]),textarea,select')).filter(e=>visible(e)&&!e.disabled&&!e.value&&!e.checked).map(label).filter((v,i,a)=>v&&a.indexOf(v)===i).slice(0,80);
})()
""";
}
