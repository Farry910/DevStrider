using System.Text.Json;

namespace DevStrider.Desktop.Services;

/// <summary>
/// Finds ChatGPT's message composer on a live page, and drives it.
///
/// <para>
/// This exists because "the composer" used to be defined by what it is <em>not</em>: not inside a
/// transcript turn, not disabled, and — when that failed to narrow it down — simply the last text
/// input in document order. That definition breaks the moment ChatGPT puts a second editable box on
/// the page. Clicking Edit on a reply does exactly that, and the box it opens is a textarea that is
/// visible, enabled, not disabled and, depending on the build, not inside anything the transcript
/// selectors recognise. The prompt went into the open reply instead of the composer, nothing was
/// ever sent, and the run waited out its full reply timeout for an answer to a question it had not
/// asked.
/// </para>
///
/// <para>
/// So the composer is identified by what it <em>is</em>. It is the text input that sits in a box
/// alongside the attach ("+") control, the send button, and the model dropdown — the four things
/// that together appear exactly once on the page, around the real composer and nowhere else. Each
/// candidate input is scored on how much of that furniture it can see, and the winner is the one
/// with the most. An open message editor scores zero: it has no attach control, no model picker and
/// no send button of the composer's kind, only Cancel and Save.
/// </para>
///
/// <para>
/// Every caller shares one definition. Four copies of the old rule had already drifted apart, and a
/// send that targets a different element from the one the text was placed in is the exact bug this
/// class is here to stop.
/// </para>
/// </summary>
public static class ChatGptComposer
{
    /// <summary>
    /// Defines <c>dsComposer()</c>, which returns the composer and the evidence for the choice.
    /// Every script below is built on it so they cannot disagree about which element is the target.
    /// </summary>
    public const string Prelude = """
 const visible = e => !!(e && (e.offsetWidth || e.offsetHeight ||
   (e.getClientRects && e.getClientRects().length)));
 const attr = (e, name) => (e && e.getAttribute && e.getAttribute(name)) || '';
 const labelOf = e => (attr(e,'aria-label') + ' ' + attr(e,'title') + ' ' + attr(e,'data-testid') +
   ' ' + attr(e,'name') + ' ' + ((e && e.innerText) || '')).toLowerCase();
 // A transcript turn. An open message editor lives inside one of these on the builds where the
 // markers are present - but not on all of them, which is why this is a disqualifier and not the
 // whole test.
 const inMessage = e => !!(e && e.closest && e.closest(
   '[data-message-author-role],[data-testid^="conversation-turn"],[data-testid*="conversation-turn"],article'));
 const editable = e => !!e && (e.tagName === 'TEXTAREA' || e.tagName === 'INPUT' ||
   e.isContentEditable || attr(e,'contenteditable') === 'true' || attr(e,'role') === 'textbox');
 const readText = e => !e ? '' :
   (e.value !== undefined && e.value !== null ? e.value : (e.innerText || e.textContent || ''));
 const inputSelector = 'textarea,input[type="text"],[contenteditable="true"],[role="textbox"]';
 // The composer's own box. A form when there is one; otherwise the nearest thing that calls itself
 // a composer; otherwise a walk up the ancestors — which has to be bounded, and not by depth.
 // A fixed six-level walk reaches <body> on a shallow page, and then every candidate "sees" the
 // whole document's furniture: an open message editor scored 8 out of the composer's own attach
 // button and send button in the harness. So the walk stops at the last ancestor that still
 // describes this one input. An element holding a second text box is not the furniture around this
 // one, it is the page.
 const boxOf = e => {
   const named = e.closest && (e.closest('form') ||
     e.closest('[class*="composer" i],[data-testid*="composer" i],[id*="composer" i]'));
   if (named) return named;
   let node = e;
   for (let up = 0; up < 6; up++) {
     const parent = node.parentElement;
     if (!parent || parent === document.body || parent === document.documentElement) break;
     if (parent.querySelectorAll(inputSelector).length > 1) break;
     node = parent;
   }
   return node;
 };
 const buttonsIn = box => Array.from(box.querySelectorAll('button,[role="button"]')).filter(visible);
 // The "+" control. It is a file picker however it is dressed: some builds render a real
 // input[type=file], some a button that opens a menu whose items attach files.
 const isAttach = b => /\b(attach|upload|add files|add photos|add file|plus)\b/.test(labelOf(b)) ||
   labelOf(b).includes('upload') || attr(b,'data-testid').toLowerCase().includes('attach');
 const isSend = b => attr(b,'data-testid').toLowerCase().includes('send') ||
   /\b(send|submit)\b/.test(labelOf(b));
 const isDropdown = box => !!box.querySelector(
   '[role="combobox"],[aria-haspopup="menu"],[aria-haspopup="listbox"],[data-testid*="model" i],[data-testid*="picker" i]');

 // Scores one candidate on how much composer furniture surrounds it. Nothing here is decisive on
 // its own: ChatGPT renames a test id or drops the placeholder every few months, and any single
 // rule written as a hard requirement is a run that stops dead the week it changes.
 const scoreComposer = input => {
   const why = [];
   if (!visible(input) || input.disabled || input.readOnly) return { score:-1, why:['not usable'] };
   if (inMessage(input)) return { score:-1, why:['inside a transcript turn'] };
   let score = 0;
   if (input.id === 'prompt-textarea') { score += 5; why.push('#prompt-textarea'); }
   const placeholder = (attr(input,'placeholder') + ' ' + attr(input,'data-placeholder') + ' ' +
     attr(input,'aria-label')).toLowerCase().trim();
   // "Ask anything", "Message ChatGPT", and — on the build this was checked against — an
   // aria-label of "Chat with ChatGPT" on the contenteditable, with the visible "Ask ChatGPT"
   // placeholder sitting on a hidden textarea that is not the typing target at all. \bchat\b does
   // not match the sidebar's "Search chats", which is the one thing nearby that could be confused
   // for this — and it has none of the furniture anyway.
   if (/\b(ask|message|chat|prompt)\b/.test(placeholder) || placeholder.includes('anything')) {
     score += 4; why.push('placeholder "' + placeholder.slice(0,40) + '"');
   }
   const box = boxOf(input);
   if (box) {
     const buttons = buttonsIn(box);
     if (box.querySelector('input[type="file"]')) { score += 3; why.push('file input'); }
     if (buttons.some(isAttach)) { score += 3; why.push('attach button'); }
     if (buttons.some(isSend)) { score += 3; why.push('send button'); }
     if (isDropdown(box)) { score += 2; why.push('dropdown'); }
     if (buttons.length >= 2) { score += 1; why.push(buttons.length + ' buttons'); }
   }
   // ChatGPT keeps a hidden textarea beside the contenteditable it actually types into — the
   // accessibility mirror that carries the visible "Ask ChatGPT" placeholder. During a render it can
   // briefly measure as visible and score well, because it sits in the composer's own box and sees
   // all of the same furniture. Live, that cost one wasted attempt per prompt: the text went into
   // the mirror, "prompt did not land in the composer", and a retry a second later got it right.
   // Where both are in one box, the contenteditable is the target.
   if (input.tagName === 'TEXTAREA' && box && box.querySelector('[contenteditable="true"]')) {
     score -= 6; why.push('a contenteditable in the same box is the real target');
   }
   // Cancel/Save is the signature of a message being edited, not of a composer.
   if (box && buttonsIn(box).some(b => /^\s*(cancel|save|discard)\s*$/i.test((b.innerText || '').trim()))) {
     score -= 4; why.push('cancel/save buttons: this is a message editor');
   }
   return { score, why };
 };

 const dsComposerAll = () => Array.from(new Set(Array.from(document.querySelectorAll(
     inputSelector))))
   .filter(editable)
   .map((input, order) => { const s = scoreComposer(input); return { input, order, score:s.score, why:s.why }; });

 // The winner is the highest score; a tie goes to whichever is further down the page, because the
 // composer sits below the transcript.
 const dsComposer = () => {
   const ranked = dsComposerAll().filter(c => c.score > 0)
     .sort((a,b) => b.score - a.score || b.order - a.order);
   return ranked[0] || null;
 };
""";

    /// <summary>
    /// Places the prompt in the composer and reports what it found. Sending is the host's job — it
    /// presses Enter through the browser input pipeline, which is what a person does.
    /// </summary>
    public static string PlaceScript(string prompt)
    {
        var payload = JsonSerializer.Serialize(prompt);
        return """
(() => {
__PRELUDE__
 const prompt = __PROMPT__;
 // A reply left open for editing no longer blocks anything: the composer is identified positively,
 // so an open editor is just another low-scoring candidate. Close it anyway when ChatGPT offers a
 // Cancel, because leaving it open is untidy and the next reply-read has to step around it — but
 // never make the run depend on finding that button.
 const openEditor = Array.from(document.querySelectorAll('textarea,[contenteditable="true"]'))
   .find(e => visible(e) && inMessage(e));
 let closedEditor = '';
 if (openEditor) {
   const cancel = Array.from(document.querySelectorAll('button')).find(b => visible(b) &&
     /^\s*(cancel|discard)\s*$/i.test(((b.innerText || '') + ' ' + attr(b,'aria-label')).trim()));
   if (cancel) { cancel.click(); closedEditor = 'closed an open message editor'; }
   else closedEditor = 'a message is open for editing; left it alone';
 }

 const chosen = dsComposer();
 if (!chosen) {
   const seen = dsComposerAll().map(c => ({
     tag:c.input.tagName.toLowerCase(), id:c.input.id || '', score:c.score, why:c.why.join(', ') }));
   return { ok:false, waiting:true, closedEditor,
     error:'ChatGPT composer was not found. Sign in and dismiss any dialog.',
     candidates:seen.slice(0,8) };
 }
 const input = chosen.input;
 input.focus();
 if (input instanceof HTMLTextAreaElement || input instanceof HTMLInputElement) {
   const proto = input instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
   const setter = Object.getOwnPropertyDescriptor(proto,'value')?.set;
   setter ? setter.call(input, prompt) : input.value = prompt;
 } else {
   input.textContent = prompt;
 }
 input.dispatchEvent(new InputEvent('input',{bubbles:true,inputType:'insertText',data:prompt}));
 input.dispatchEvent(new Event('change',{bubbles:true}));
 const box = boxOf(input);
 const send = buttonsIn(box).find(b => isSend(b) && !b.disabled) ||
   Array.from(document.querySelectorAll('button')).find(b =>
     visible(b) && !b.disabled && !inMessage(b) && isSend(b));
 const rect = input.getBoundingClientRect();
 return { ok:true, waiting:false, error:'', closedEditor,
   hasSend:!!send, chosenBy:chosen.why.join(', '), score:chosen.score,
   placed:String(readText(input) || '').trim().length,
   x:rect.left + rect.width/2, y:rect.top + rect.height/2 };
})()
""".Replace("__PRELUDE__", Prelude).Replace("__PROMPT__", payload);
    }

    /// <summary>
    /// Whether the composer emptied, which is how ChatGPT shows a message actually went. A composer
    /// that cannot be found reads as empty: the page moved on, and waiting on a vanished element is
    /// how a finished send used to look like a stall.
    /// </summary>
    public static string EmptyScript => """
(() => {
__PRELUDE__
 const chosen = dsComposer();
 if (!chosen) return true;
 return String(readText(chosen.input) || '').trim().length === 0;
})()
""".Replace("__PRELUDE__", Prelude);

    /// <summary>Clicks the composer's own send control, for when Enter did not take.</summary>
    public static string ClickSendScript => """
(() => {
__PRELUDE__
 const chosen = dsComposer();
 // Scoped to the composer's own box first. A bare document-wide search for "send" finds the Save
 // button of an open message editor on some builds, which submits an edit to an old reply.
 const box = chosen ? boxOf(chosen.input) : document.body;
 const send = buttonsIn(box).find(b => isSend(b) && !b.disabled) ||
   Array.from(document.querySelectorAll('button')).find(b =>
     visible(b) && !b.disabled && !inMessage(b) && isSend(b));
 if (!send) return false;
 send.click();
 return true;
})()
""".Replace("__PRELUDE__", Prelude);

    /// <summary>
    /// Everything the detector can see, scored, with no side effects. This is what /dev/composer
    /// serves: when a run reports it could not find the composer, this says which elements were
    /// considered and what each was missing, on the actual signed-in page.
    /// </summary>
    public static string DiagnoseScript => """
(() => {
__PRELUDE__
 const describe = c => ({
   tag:c.input.tagName.toLowerCase(),
   id:c.input.id || '',
   testid:attr(c.input,'data-testid'),
   placeholder:attr(c.input,'placeholder') || attr(c.input,'data-placeholder'),
   ariaLabel:attr(c.input,'aria-label'),
   inMessage:inMessage(c.input),
   visible:visible(c.input),
   order:c.order,
   score:c.score,
   why:c.why,
   valueLength:String(readText(c.input) || '').length,
   boxButtons:buttonsIn(boxOf(c.input)).slice(0,10).map(b =>
     ((b.innerText || '').trim() || attr(b,'aria-label') || attr(b,'data-testid') || '?').slice(0,30)),
 });
 const all = dsComposerAll().map(describe);
 const chosen = dsComposer();
 return {
   url:location.href,
   title:document.title,
   candidates:all,
   chosen: chosen ? describe(chosen) : null,
   openEditors:Array.from(document.querySelectorAll('textarea,[contenteditable="true"]'))
     .filter(e => visible(e) && inMessage(e)).length,
   turns:document.querySelectorAll('[data-message-author-role]').length,
 };
})()
""".Replace("__PRELUDE__", Prelude);
}
