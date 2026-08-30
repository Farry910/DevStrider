using System.Text.Json;

namespace DevStrider.Desktop.Services;

/// <summary>
/// Finding the application form on a site nobody wrote an adapter for.
///
/// <para>
/// The generic adapter used to be one shot: click the first thing whose text is exactly "apply" or
/// whose id/class contains the word, then count controls and give up if there were fewer than
/// three. That fails on the shape most careers pages actually have. The button says <i>I'm
/// interested</i> or <i>Join our team</i> and never the word apply. The description and the form
/// live on two tabs of the same page, so the form is present but not rendered. Or the posting page
/// genuinely has no form at all and the application is one navigation further on — sometimes two.
/// Deel's postings failed seven times this way, reporting "1 control", which is a true and useless
/// description of a page whose form was one click away.
/// </para>
///
/// <para>
/// So this replaces the guess with a loop: <b>survey the page, score every affordance on it, take
/// the best untried one, and survey again to find out whether that helped.</b> The page reports
/// what it has rather than the app assuming; each hop is judged by whether the form got closer,
/// and a hop that achieved nothing is remembered so the next one tries something else. It is
/// bounded — <see cref="MaxHops"/> navigations and a fixed candidate budget — because an agent
/// that cannot fail is one that hangs.
/// </para>
///
/// <para>
/// Nothing here submits anything. The loop stops the moment a fillable form is on screen, which is
/// where the existing fill-and-review pipeline takes over. Every pattern that means <i>finish and
/// send</i> is scored below zero precisely so a hunt for the way in can never trip the way out.
/// </para>
/// </summary>
public static class AgenticApplyNavigator
{
    /// <summary>
    /// How many navigations to spend looking for the form. Three covers posting → apply → form,
    /// which is the deepest real flow seen; the fourth is slack for an interstitial (a cookie wall,
    /// a "continue as guest" step) rather than an invitation to wander.
    /// </summary>
    public const int MaxHops = 4;

    /// <summary>
    /// Controls that make a page count as the application form. Matches the existing gate in
    /// <c>JobBrowserView</c> — a name, an email and one more is the smallest real application, and
    /// anything less is a search box or a newsletter signup.
    /// </summary>
    public const int FormControlThreshold = 3;

    // ── survey ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Inventories the page: what is fillable, what is tabbed, and what looks like a way in.
    ///
    /// <para>
    /// Every candidate is stamped with <c>data-ds-nav</c> so a later activation can address it by
    /// that attribute rather than by an index into a list the page may have re-rendered underneath.
    /// Stamps from an earlier survey are cleared first, so the numbering is always this survey's.
    /// </para>
    ///
    /// <para>
    /// Scores are returned with the reasons that produced them. When a run picks the wrong thing,
    /// the trace has to show what it was choosing between and why — a bare "clicked: Learn more"
    /// says nothing about what it should have clicked instead.
    /// </para>
    /// </summary>
    public const string SurveyScript = """
(() => {
  const norm = v => String(v || '').toLowerCase().replace(/\s+/g,' ').trim();
  const clean = v => String(v || '').replace(/\s+/g,' ').trim();
  const visible = e => {
    if (!e || !(e.offsetWidth || e.offsetHeight || e.getClientRects().length)) return false;
    const s = getComputedStyle(e);
    return s.visibility !== 'hidden' && s.display !== 'none' && Number(s.opacity || '1') > 0.05;
  };
  const usable = e => visible(e) && !e.disabled && e.getAttribute('aria-disabled') !== 'true';
  const textOf = e => norm(e.innerText || e.value || e.getAttribute('aria-label') ||
                           e.title || e.getAttribute('data-tracking-control-name'));

  for (const stamped of document.querySelectorAll('[data-ds-nav]')) stamped.removeAttribute('data-ds-nav');
  let stamp = 0;
  const mark = e => { const id = ++stamp; e.setAttribute('data-ds-nav', String(id)); return id; };

  const FILLABLE = 'input:not([type="hidden"]):not([type="submit"]):not([type="button"]),' +
                   'select,textarea,[role="radio"],[role="checkbox"],[role="combobox"],[contenteditable="true"]';
  const fillable = Array.from(document.querySelectorAll(FILLABLE)).filter(visible);
  const files = fillable.filter(e => e.type === 'file').length;

  // Prose, so a description page can be told from a form page. Same sentence test the description
  // extractor uses: form labels never close a clause with punctuation.
  const bodyText = clean(document.body ? document.body.innerText : '');
  const sentences = (bodyText.match(/[a-z][a-z,;'")\s-]{25,}?[.!?](\s|$)/g) || []).length;

  // ── tabs ──────────────────────────────────────────────────────────────────
  // Real tablists first, then the things that behave like tabs without saying so: a nav strip
  // whose items toggle aria-selected or an "active"/"current" class. A careers page that splits
  // Overview / Description / Application across a strip like that hides the form completely from
  // anything that only looks at what is currently painted.
  const tabNodes = new Set();
  for (const sel of ['[role="tab"]',
                     '[role="tablist"] a, [role="tablist"] button',
                     '.tabs a, .tabs button, ul.tab-list a, ul.tab-list button',
                     '[class*="tab" i][class*="nav" i] a, [class*="tab" i][class*="nav" i] button',
                     '[data-tab], [data-toggle="tab"], [data-bs-toggle="tab"]']) {
    try { for (const n of document.querySelectorAll(sel)) tabNodes.add(n); } catch {}
  }
  const tabs = Array.from(tabNodes).filter(usable).slice(0, 12).map(e => ({
    ref: mark(e),
    label: clean(e.innerText || e.getAttribute('aria-label') || e.title).slice(0, 80),
    selected: e.getAttribute('aria-selected') === 'true' ||
              e.getAttribute('aria-current') === 'page' ||
              /\b(active|selected|current)\b/i.test(e.className || ''),
  })).filter(t => t.label.length > 0 && t.label.length < 60);

  // ── ways in ───────────────────────────────────────────────────────────────
  // Ordered strongest first. "I'm interested" is here because it is what a large family of
  // templates says instead of Apply, and the old exact-match list had no room for it.
  const STRONG = [
    /^apply$/, /^apply now\b/, /^apply for this\b/, /^apply to this\b/, /^apply online\b/,
    /^start (your |an |the )?application\b/, /^begin (your |an |the )?application\b/,
    /^continue to (the )?application\b/, /^go to (the )?application\b/,
    /^(quick|easy|one[- ]click) apply\b/, /^apply with\b/,
  ];
  const MEDIUM = [
    /^i(\s|')?m interested\b/, /^i am interested\b/, /^interested\b/, /^express interest\b/,
    /^register (your )?interest\b/, /^join (our|the) team\b/, /^join us\b/,
    /^application form\b/, /^candidate application\b/, /^submit (your )?(cv|resume)\b/,
    /^apply\b/, /\bapply now\b/,
  ];
  // Anything that finishes an application. Scored out entirely: this loop is looking for the way
  // in, and must never be the thing that presses send.
  const FINAL = [
    /\bsubmit\b/, /\bsend application\b/, /\bcomplete application\b/, /\bfinish\b/,
    /\bconfirm\b/, /\bnext\b/, /\bcontinue$/, /\bsave and continue\b/, /\breview application\b/,
  ];
  // Things that look inviting and go somewhere else entirely.
  const DECOY = [
    /\bsave (this )?job\b/, /\bshare\b/, /\bprint\b/, /\bemail (this|a friend)\b/,
    /\brefer a friend\b/, /\bsign in\b/, /\blog ?in\b/, /\bcreate (an )?account\b/,
    /\bregister\b/, /\bview all\b/, /\ball jobs\b/, /\bsimilar jobs\b/, /\bback to\b/,
    /\bsearch\b/, /\bcookie/, /\baccept\b/, /\bdecline\b/, /\bprivacy\b/, /\bnewsletter\b/,
    /\bsubscribe\b/, /\blearn more\b/, /\bread more\b/, /\bhome\b/, /\bcontact us\b/,
  ];

  const scoreOf = e => {
    const text = textOf(e);
    const href = norm(e.getAttribute && e.getAttribute('href'));
    const attrs = norm((e.id || '') + ' ' + (typeof e.className === 'string' ? e.className : '') + ' ' +
                       (e.getAttribute && (e.getAttribute('data-testid') || '')) + ' ' +
                       (e.getAttribute && (e.getAttribute('data-automation-id') || '')));
    const why = [];
    let score = 0;

    if (FINAL.some(r => r.test(text))) { return { score: -100, why: ['final/next action — never clicked here'] }; }
    if (DECOY.some(r => r.test(text)))  { return { score: -50,  why: ['decoy wording'] }; }

    if (STRONG.some(r => r.test(text))) { score += 100; why.push('strong apply wording'); }
    else if (MEDIUM.some(r => r.test(text))) { score += 70; why.push('interest/apply wording'); }

    if (/\/apply(\/|$|\?)/.test(href) || /\/application(s)?(\/|$|\?)/.test(href)) { score += 45; why.push('href points at an application'); }
    else if (/apply|application/.test(href)) { score += 20; why.push('href mentions apply'); }
    if (/apply|application/.test(attrs)) { score += 15; why.push('id/class mentions apply'); }

    // Everything above is evidence that this control opens an application. Everything below only
    // adjusts how strong that evidence looks. Without at least one piece of actual evidence the
    // candidate does not qualify at any score — otherwise a job index page, where every posting is
    // a short link inside a styled card, offers up its first job title as the way in and the run
    // wanders off to a different job. Structural shape is a tie-breaker, never a qualification.
    if (score === 0) return { score: 0, why: ['no apply signal'] };

    // A real call to action is a button or a styled link, not a bare word in a paragraph.
    if (e.tagName === 'BUTTON' || e.getAttribute('role') === 'button' ||
        /\bbtn\b|button|cta/.test(attrs)) { score += 10; why.push('looks like a button'); }
    // Short labels are calls to action; long ones are sentences that happen to contain the word.
    if (text.length > 0 && text.length <= 30) { score += 8; why.push('short label'); }
    else if (text.length > 80) { score -= 15; why.push('long label — probably prose'); }

    return { score, why };
  };

  const clickable = Array.from(document.querySelectorAll(
    'a[href],button,input[type="button"],input[type="submit"],[role="button"],[onclick]')).filter(usable);
  const affordances = clickable
    .map(e => {
      const { score, why } = scoreOf(e);
      return { e, score, why };
    })
    .filter(c => c.score > 0)
    .sort((a, b) => b.score - a.score)
    .slice(0, 10)
    .map(c => ({
      ref: mark(c.e),
      label: clean(c.e.innerText || c.e.value || c.e.getAttribute('aria-label') || c.e.title).slice(0, 80),
      href: (c.e.getAttribute && c.e.getAttribute('href')) || '',
      tag: c.e.tagName.toLowerCase(),
      score: c.score,
      why: c.why.join(', '),
    }));

  return {
    url: location.href,
    title: clean(document.title).slice(0, 200),
    controls: fillable.length,
    files,
    sentences,
    textLength: bodyText.length,
    tabs,
    affordances,
  };
})()
""";

    /// <summary>
    /// Clicks whatever the last survey stamped with this <c>data-ds-nav</c> value.
    ///
    /// <para>
    /// Addressing by stamp rather than by selector is what makes the loop safe across a re-render:
    /// if the element is gone, this reports so and the caller picks the next candidate, instead of
    /// a selector silently matching some other element that has drifted into the same position.
    /// </para>
    ///
    /// <para>
    /// <c>target</c> is stripped from anchors so a new window cannot carry the flow off where the
    /// automation cannot see it. Scrolled into view first, because a control outside the viewport
    /// is one some frameworks decline to fire handlers for.
    /// </para>
    /// </summary>
    public static string BuildActivateScript(int reference)
    {
        var payload = JsonSerializer.Serialize(new { reference });
        return """
(() => {
  const request = __PAYLOAD__;
  const target = document.querySelector('[data-ds-nav="' + request.reference + '"]');
  if (!target) return { clicked:false, reason:'that control is no longer on the page', url:location.href };
  const label = String(target.innerText || target.value || target.getAttribute('aria-label') || '')
    .replace(/\s+/g,' ').trim().slice(0,80);
  if (target instanceof HTMLAnchorElement) target.removeAttribute('target');
  try { target.scrollIntoView({ block:'center', behavior:'instant' }); } catch {}
  try { target.click(); }
  catch (error) { return { clicked:false, reason:String(error), label, url:location.href }; }
  return { clicked:true, label, url:location.href };
})()
""".Replace("__PAYLOAD__", payload);
    }

    /// <summary>
    /// The visible prose of the panel a tab just revealed.
    ///
    /// <para>
    /// Scoped to the tab's own panel where <c>aria-controls</c> names one, because otherwise every
    /// tab returns the whole page and the harvest is four copies of the same text. Falls back to the
    /// largest prose block when the markup does not connect the two, which is common enough on
    /// hand-rolled tab strips.
    /// </para>
    /// </summary>
    public static string BuildReadPanelScript(int reference)
    {
        var payload = JsonSerializer.Serialize(new { reference });
        return """
(() => {
  const request = __PAYLOAD__;
  const clean = v => String(v || '').replace(/\s+/g,' ').trim();
  const controls = e => e.querySelectorAll(
    'input:not([type="hidden"]),select,textarea,[role="radio"],[role="checkbox"],[role="combobox"]').length;

  const tab = document.querySelector('[data-ds-nav="' + request.reference + '"]');
  let panel = null;
  const controlsId = tab && tab.getAttribute && tab.getAttribute('aria-controls');
  if (controlsId) panel = document.getElementById(controlsId);
  if (!panel) {
    const panels = Array.from(document.querySelectorAll('[role="tabpanel"]'))
      .filter(p => !!(p.offsetWidth || p.offsetHeight || p.getClientRects().length));
    if (panels.length === 1) panel = panels[0];
  }
  if (!panel) {
    // No declared relationship. Take the biggest visible block that is mostly prose, which is what
    // the tab was switched to reveal.
    let best = null, bestLen = 0;
    for (const node of document.querySelectorAll('main,article,section,div')) {
      if (!(node.offsetWidth || node.offsetHeight || node.getClientRects().length)) continue;
      const text = clean(node.innerText);
      if (text.length < 200 || text.length <= bestLen) continue;
      if (Array.from(node.children).some(c => clean(c.innerText).length > text.length * 0.9)) continue;
      best = node; bestLen = text.length;
    }
    panel = best;
  }
  if (!panel) return { ok:false, text:'', controls:0 };
  return { ok:true, text: clean(panel.innerText).slice(0, 20000), controls: controls(panel) };
})()
""".Replace("__PAYLOAD__", payload);
    }

    // ── typed reads of what those scripts return ────────────────────────────

    public sealed record Affordance(int Ref, string Label, string Href, string Tag, int Score, string Why);

    public sealed record TabHandle(int Ref, string Label, bool Selected);

    public sealed record Survey(
        string Url, string Title, int Controls, int Files, int Sentences, int TextLength,
        IReadOnlyList<TabHandle> Tabs, IReadOnlyList<Affordance> Affordances)
    {
        /// <summary>The page is the application form: enough to fill in that it cannot be anything else.</summary>
        public bool LooksLikeForm => Controls >= FormControlThreshold;

        /// <summary>A one-line description for the trace, so a run reads as a sequence of decisions.</summary>
        public string Describe =>
            $"{Controls} control(s), {Files} file input(s), {Sentences} sentence(s), " +
            $"{Tabs.Count} tab(s), {Affordances.Count} candidate way(s) in";
    }

    public static Survey ReadSurvey(JsonElement root)
    {
        var tabs = new List<TabHandle>();
        if (root.TryGetProperty("tabs", out var tabsElement) && tabsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var tab in tabsElement.EnumerateArray())
                tabs.Add(new TabHandle(
                    Int(tab, "ref"), Str(tab, "label"),
                    tab.TryGetProperty("selected", out var s) && s.ValueKind == JsonValueKind.True));
        }

        var affordances = new List<Affordance>();
        if (root.TryGetProperty("affordances", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
                affordances.Add(new Affordance(
                    Int(item, "ref"), Str(item, "label"), Str(item, "href"),
                    Str(item, "tag"), Int(item, "score"), Str(item, "why")));
        }

        return new Survey(
            Str(root, "url"), Str(root, "title"), Int(root, "controls"), Int(root, "files"),
            Int(root, "sentences"), Int(root, "textLength"), tabs, affordances);
    }

    private static string Str(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";

    private static int Int(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.TryGetInt32(out var number) ? number : 0 : 0;

    /// <summary>
    /// A key for "we have already tried this", stable across re-surveys where the stamp is not.
    /// The stamp is only valid for the survey that issued it, so remembering stamps would let the
    /// loop click the same button under a new number on every hop.
    /// </summary>
    public static string TriedKey(string url, Affordance affordance) =>
        $"{url}|{affordance.Label.ToLowerInvariant()}|{affordance.Href.ToLowerInvariant()}";

    /// <summary>
    /// Why the hunt stopped, in words that name the page rather than the symptom. "1 control" was
    /// true of every one of Deel's seven failures and told nobody what to do next.
    /// </summary>
    public static string DescribeDeadEnd(Survey survey, int hops, IReadOnlyCollection<string> tried)
    {
        if (survey.Affordances.Count == 0 && survey.Tabs.Count == 0)
            return $"no application form and nothing on the page that opens one — {survey.Controls} " +
                   $"control(s) at {survey.Url}. This may be a job index rather than a single posting.";

        if (tried.Count == 0)
            return $"nothing on the page scored as a way into an application. Candidates seen: " +
                   $"{string.Join(", ", survey.Affordances.Take(4).Select(a => $"\"{a.Label}\""))}. " +
                   "If one of those is the apply control, its wording needs adding to the navigator.";

        return $"followed {hops} step(s) and still no form ({survey.Controls} control(s) at " +
               $"{survey.Url}). Tried: {string.Join(", ", tried.Take(4).Select(key => $"\"{key.Split('|')[1]}\""))}.";
    }
}
