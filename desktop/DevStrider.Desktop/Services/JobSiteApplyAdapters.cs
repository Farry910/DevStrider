using System.Text.Json;

namespace DevStrider.Desktop.Services;

/// <summary>
/// Site-specific application-flow contracts. Field filling remains in
/// <see cref="JobSiteFormAdapters"/>; these profiles own entering the application, recognizing
/// intermediate/final actions, and finding validation errors. Resolution is specific-first and
/// always ends at <see cref="Default"/>.
/// </summary>
public static class JobSiteApplyAdapters
{
    public sealed record Definition(
        string Name,
        string[] HostFragments,
        string[] OpenSelectors,
        string[] ActionSelectors,
        string[] ErrorSelectors);

    public static readonly Definition Greenhouse = new(
        "Greenhouse",
        ["greenhouse.io"],
        ["a[href='#application']", "a[href*='#app']", "a[href*='/applications/']", "button[data-mapped='apply']"],
        ["#submit_app", "button[type='submit']", "input[type='submit']", "form button"],
        ["#error_message", ".field-error", ".error-message", ".validation-error", "[data-testid*='error' i]", "[role='alert']"]);

    public static readonly Definition Ashby = new(
        "Ashby",
        ["ashbyhq.com"],
        ["a[href$='/application']", "a[href*='/application?']", "button[data-testid*='apply' i]"],
        ["form button", "button[type='submit']", "[role='button']"],
        ["[role='alert']", "[aria-live='assertive']", "[data-testid*='error' i]", "[class*='error' i]"]);

    public static readonly Definition Lever = new(
        "Lever",
        ["lever.co"],
        ["a.postings-btn", "a[href$='/apply']", "a[href*='/apply?']"],
        [".template-btn-submit", "button[type='submit']", "input[type='submit']", "form button"],
        [".error", ".field-error", ".validation-error", "[role='alert']", "[aria-invalid='true']"]);

    public static readonly Definition ApplyToJob = new(
        "ApplyToJob",
        ["applytojob.com"],
        ["a[href*='/apply']", "button[id*='apply' i]", "button[class*='apply' i]"],
        ["button[type='submit']", "input[type='submit']", "form button", "[role='button']"],
        [".error", ".field-error", ".help-block", ".invalid-feedback", "[role='alert']", "[aria-invalid='true']"]);

    public static readonly Definition Teamtailor = new(
        "Teamtailor",
        ["teamtailor.com"],
        ["a[href*='/jobs/'][href*='/apply']", "[data-action*='apply' i]", "button[class*='apply' i]"],
        ["button[type='submit']", "form button", "[role='button']"],
        ["[role='alert']", ".field-error", ".invalid-feedback", "[class*='error' i]", "[aria-invalid='true']"]);

    public static readonly Definition Default = new(
        "Default (generic)",
        [],
        ["a[href*='apply' i]", "button[id*='apply' i]", "button[class*='apply' i]"],
        ["button[type='submit']", "input[type='submit']", "form button", "[role='button']"],
        ["[role='alert']", ".field-error", ".error-message", ".validation-error", ".invalid-feedback", "[aria-invalid='true']"]);

    private static readonly Definition[] Specific = [Greenhouse, Ashby, Lever, ApplyToJob, Teamtailor];

    public static Definition Resolve(Uri uri)
    {
        var host = uri.Host.ToLowerInvariant();
        return Specific.FirstOrDefault(adapter =>
                   adapter.HostFragments.Any(fragment =>
                       host.Equals(fragment, StringComparison.OrdinalIgnoreCase) ||
                       host.EndsWith("." + fragment, StringComparison.OrdinalIgnoreCase)))
               ?? Default;
    }

    public static string BuildOpenApplicationScript(Uri uri)
    {
        var payload = JsonSerializer.Serialize(Resolve(uri));
        return """
(() => {
 const adapter = __PAYLOAD__;
 const norm = value => String(value || '').toLowerCase().replace(/\s+/g,' ').trim();
 const visible = element => !!element && !!(element.offsetWidth || element.offsetHeight || element.getClientRects().length);
 const nodes = selectors => {
   const found = [];
   for (const selector of selectors || []) {
     try { found.push(...document.querySelectorAll(selector)); } catch {}
   }
   return Array.from(new Set(found));
 };
 const candidates = nodes(adapter.OpenSelectors).concat(Array.from(document.querySelectorAll('a,button,input[type="button"]')))
   .filter(visible);
 const target = candidates.find(element => {
   const text = norm(element.innerText || element.value || element.getAttribute('aria-label') || element.title);
   if (/\b(submit|continue|next|confirm|send application|complete application)\b/.test(text)) return false;
   return /^(apply|apply now|apply for this job|start application|start your application)$/.test(text)
     || adapter.OpenSelectors.some(selector => { try { return element.matches(selector); } catch { return false; } });
 });
 if (!target) return { adapter:adapter.Name, clicked:false, label:'' };
 if (target instanceof HTMLAnchorElement) target.removeAttribute('target');
 const label = norm(target.innerText || target.value || target.getAttribute('aria-label'));
 target.click();
 return { adapter:adapter.Name, clicked:true, label };
})()
""".Replace("__PAYLOAD__", payload);
    }

    /// <summary>
    /// Runs browser/native validation and locates intermediate or final action coordinates. The host
    /// commits those actions through WebView2's browser input pipeline rather than synthetic DOM clicks.
    /// </summary>
    public static string BuildValidationScript(Uri uri, bool allowSafeAdvance)
    {
        var payload = JsonSerializer.Serialize(new { adapter = Resolve(uri), allowSafeAdvance });
        return """
(() => {
 const request = __PAYLOAD__, adapter = request.adapter;
 const norm = value => String(value || '').toLowerCase().replace(/\s+/g,' ').trim();
 const clean = value => String(value || '').replace(/\s+/g,' ').trim();
 const visible = element => !!element && !!(element.offsetWidth || element.offsetHeight || element.getClientRects().length);
 const nodes = selectors => {
   const found = [];
   for (const selector of selectors || []) {
     try { found.push(...document.querySelectorAll(selector)); } catch {}
   }
   return Array.from(new Set(found));
 };
 const labelFor = element => {
   const labelled = clean((element.getAttribute('aria-labelledby') || '').split(/\s+/).filter(Boolean)
     .map(id => document.getElementById(id)?.innerText || '').join(' '));
   const own = clean(Array.from(element.labels || []).map(label => label.innerText).join(' '));
   const block = element.closest('[data-field-entry-id],[data-field-path],.ashby-application-form-field-entry,'
     + 'fieldset,[data-field],[class*="field" i]');
   const heading = clean(block?.querySelector(':scope > label,:scope > legend,[data-question-label],'
     + '.ashby-application-form-question-title')?.innerText);
   return (labelled || own || heading
     || clean(element.getAttribute('aria-label')) || clean(element.name) || clean(element.id) || 'Required field').slice(0,160);
 };

 const successPattern = /\b(thank you for applying|thanks for applying|application (has been )?(submitted|received)|we (have )?received your application)\b/;
 const successText = Array.from(document.querySelectorAll('h1,h2,h3,[role="heading"],[role="status"]'))
   .filter(visible).map(node => norm(node.innerText || node.textContent)).join(' ');
 if (successPattern.test(successText) || /\/(confirmation|submitted|thank-you|thank_you)(\/|$)/i.test(location.pathname))
   return { adapter:adapter.Name, action:'success', clicked:false, final:true, label:'application submitted', errors:[] };

 const candidates = nodes(adapter.ActionSelectors).filter(element => visible(element) && !element.disabled);
 const textFor = element => norm(element.innerText || element.value || element.getAttribute('aria-label') || element.title);
 const finalPattern = /^(submit|submit application|send application|complete application|finish application|apply)$/;
 const nextPattern = /^(next|continue|save and continue|continue application|review application|next step)$/;
 const final = candidates.find(element => finalPattern.test(textFor(element)));
 const next = candidates.find(element => nextPattern.test(textFor(element)));

 // On the primary pass, return the real action before inspecting validity. The host must physically
 // click Next/Submit first; only the errors rendered by that action are correction evidence.
 if (request.allowSafeAdvance && next) {
   next.scrollIntoView({block:'center',inline:'nearest',behavior:'instant'});
   const rect = next.getBoundingClientRect();
   return { adapter:adapter.Name, action:'next', clicked:false, final:false, label:textFor(next), errors:[],
     x:rect.left + rect.width/2, y:rect.top + rect.height/2 };
 }
 if (request.allowSafeAdvance && final) {
   final.scrollIntoView({block:'center',inline:'nearest',behavior:'instant'});
   const rect = final.getBoundingClientRect();
   return { adapter:adapter.Name, action:'final', clicked:false, final:true, label:textFor(final), errors:[],
     x:rect.left + rect.width/2, y:rect.top + rect.height/2 };
 }
 if (request.allowSafeAdvance)
   return { adapter:adapter.Name, action:'none', clicked:false, final:false, label:'', errors:[] };

 // This branch runs only after a real action click. :user-invalid distinguishes controls rejected
 // by that click from untouched required fields on a newly revealed step; calling reportValidity()
 // here would incorrectly turn every new-step field into second-pass correction input.
 const errors = [];
 for (const control of Array.from(document.querySelectorAll('input,textarea,select')).filter(visible)) {
   let userInvalid = control.getAttribute('aria-invalid') === 'true';
   try { userInvalid = userInvalid || control.matches(':user-invalid'); } catch {}
   if (control.disabled || control.type === 'hidden' || control.type === 'file'
       || control.validity?.valid !== false || !userInvalid) continue;
   errors.push({ question:labelFor(control), message:clean(control.validationMessage) || 'The field is invalid.' });
 }
 for (const node of nodes(adapter.ErrorSelectors).filter(visible)) {
   const message = clean(node.innerText || node.textContent || node.getAttribute('aria-label'));
   if (!message || message.length > 500) continue;
   const block = node.closest('[data-field-entry-id],[data-field-path],.ashby-application-form-field-entry,'
     + '[data-field],[class*="field" i],fieldset');
   const control = node.matches('input,textarea,select') ? node
     : block?.querySelector('input,textarea,select,[role="radio"],button[aria-pressed]');
   const heading = clean(block?.querySelector(':scope > label,:scope > legend,[data-question-label],'
     + '.ashby-application-form-question-title')?.innerText);
   errors.push({ question:control ? labelFor(control) : (heading || message).slice(0,160), message });
 }
 const uniqueErrors = Array.from(new Map(errors.map(error => [norm(error.question)+'|'+norm(error.message),error])).values()).slice(0,30);
 if (uniqueErrors.length) return { adapter:adapter.Name, action:'errors', clicked:false, final:false, errors:uniqueErrors };

 if (final) {
   final.scrollIntoView({block:'center',inline:'nearest',behavior:'instant'});
   const rect = final.getBoundingClientRect();
   return { adapter:adapter.Name, action:'final', clicked:false, final:true, label:textFor(final), errors:[],
     x:rect.left + rect.width/2, y:rect.top + rect.height/2 };
 }
 return { adapter:adapter.Name, action:'none', clicked:false, final:false, label:'', errors:[] };
})()
""".Replace("__PAYLOAD__", payload);
    }

    /// <summary>
    /// Watches the final button without clicking it. If the human clicks and the site renders
    /// errors, WebView2 reports them so the app can refill/recover instead of assuming submission.
    /// </summary>
    public static string BuildHumanSubmitObserverScript(Uri uri)
    {
        var payload = JsonSerializer.Serialize(Resolve(uri));
        return """
(() => {
 const adapter = __PAYLOAD__;
 window.__dsSubmitValidationAdapter = adapter;
 if (window.__dsSubmitValidationObserverInstalled) return { installed:true, adapter:adapter.Name };
 window.__dsSubmitValidationObserverInstalled = true;
 const norm = value => String(value || '').toLowerCase().replace(/\s+/g,' ').trim();
 const clean = value => String(value || '').replace(/\s+/g,' ').trim();
 const visible = element => !!element && !!(element.offsetWidth || element.offsetHeight || element.getClientRects().length);
 const finalPattern = /^(submit|submit application|send application|complete application|finish application|apply)$/;
 const nodes = selectors => {
   const found=[]; for (const selector of selectors || []) { try { found.push(...document.querySelectorAll(selector)); } catch {} }
   return Array.from(new Set(found));
 };
 const labelFor = element => {
   if (!element) return '';
   const labelled = clean((element.getAttribute('aria-labelledby') || '').split(/\s+/).filter(Boolean)
     .map(id => document.getElementById(id)?.innerText || '').join(' '));
   const own = clean(Array.from(element.labels || []).map(label => label.innerText).join(' '));
   const host = element.closest('[data-field-entry-id],[data-field-path],.ashby-application-form-field-entry,'
     + 'fieldset,[data-field],[class*="field" i]');
   const heading = clean(host?.querySelector(':scope > label,:scope > legend,[data-question-label],'
     + '.ashby-application-form-question-title')?.innerText);
   return (labelled || own || heading || clean(element.getAttribute('aria-label'))
     || clean(element.name) || clean(element.id) || 'Required field').slice(0,160);
 };
 const readErrors = config => {
   const errors = [];
   for (const node of nodes(config.ErrorSelectors).filter(visible)) {
     const message = clean(node.innerText || node.textContent || node.getAttribute('aria-label'));
     if (!message || message.length > 500) continue;
     const host = node.closest('[data-field-entry-id],[data-field-path],.ashby-application-form-field-entry,'
       + 'fieldset,[data-field],[class*="field" i]');
     const control = node.matches('input,textarea,select') ? node
       : host?.querySelector('input,textarea,select,[role="radio"],button[aria-pressed]');
     const heading = clean(host?.querySelector(':scope > label,:scope > legend,[data-question-label],'
       + '.ashby-application-form-question-title')?.innerText);
     errors.push({ question:(control ? labelFor(control) : heading || message).slice(0,160), message });
   }
   for (const control of Array.from(document.querySelectorAll('input:invalid,textarea:invalid,select:invalid')).filter(visible)) {
     errors.push({ question:labelFor(control),
       message:clean(control.validationMessage) || 'The field is invalid.' });
   }
   return Array.from(new Map(errors.map(error =>
     [norm(error.question)+'|'+norm(error.message),error])).values()).slice(0,30);
 };
 document.addEventListener('click', event => {
   const config = window.__dsSubmitValidationAdapter || adapter;
   const target = event.target?.closest?.('button,input[type="submit"],[role="button"]');
   if (!target || !finalPattern.test(norm(target.innerText || target.value || target.getAttribute('aria-label') || target.title))) return;
   let attempt = 0;
   const poll = () => {
     const errors = readErrors(config);
     if (errors.length && window.chrome?.webview) {
       const signature = JSON.stringify(errors);
       if (signature !== window.__dsLastSubmitValidationSignature) {
         window.__dsLastSubmitValidationSignature = signature;
         window.chrome.webview.postMessage({ type:'devstrider-submit-validation',
           adapter:config.Name, errors });
       }
       return;
     }
     if (++attempt < 14) setTimeout(poll, 250);
   };
   setTimeout(poll, 100);
 }, true);
 return { installed:true, adapter:adapter.Name };
})()
""".Replace("__PAYLOAD__", payload);
    }
}
