using System.Text.Json;

namespace DevStrider.Desktop.Services;

/// <summary>What a job-description read produced, and whether it is really a job description.</summary>
/// <param name="Text">The description prose.</param>
/// <param name="Source">The selector or strategy that found it, for the trace.</param>
/// <param name="Controls">Form controls inside the container. A description has none.</param>
/// <param name="Sentences">Prose sentences. Form labels do not make sentences.</param>
public readonly record struct JobDescriptionRead(string Text, string Source, int Controls, int Sentences)
{
    public static JobDescriptionRead Empty => new("", "none", 0, 0);
}

/// <summary>
/// Reads the job description from the posting, and knows the difference between a posting and an
/// application form.
///
/// <para>
/// This replaces <c>document.body.innerText</c> plus a 180-character floor, which could not tell
/// them apart and did not try. Driving Absci's real Ashby posting through WebView2 shows why that
/// mattered: on the posting page the description container holds 5,038 characters and zero form
/// controls; on <c>/application</c> the description is not in the DOM at all, and the largest block
/// of text is the form itself — 2,857 characters across 68 controls. Both clear 180 characters, so
/// a link that pointed straight at <c>/application</c> sent the contact fields, the demographic
/// survey and the cookie banner to ChatGPT as the job description.
/// </para>
///
/// <para>
/// The fix has two halves. Go to the posting URL rather than reading whichever page happens to be
/// loaded, and judge what comes back by structure — a description is prose in a container with no
/// form controls in it — rather than by length.
/// </para>
/// </summary>
public static class JobPostingExtractor
{
    /// <summary>
    /// The page that carries the description for an application URL.
    ///
    /// <para>
    /// Ashby and Lever put the posting and the form at two addresses, one a suffix of the other, so
    /// the posting is recoverable from the form URL by dropping the suffix. Greenhouse's embedded
    /// form has no such relation — <c>job_app?token=…</c> does not name the board it belongs to —
    /// so its URL is returned unchanged and the description is looked for on the page itself.
    /// </para>
    /// </summary>
    public static Uri PostingUrlFor(Uri applicationUri)
    {
        var path = applicationUri.AbsolutePath.TrimEnd('/');
        foreach (var suffix in new[] { "/application", "/apply" })
        {
            if (!path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            var trimmed = path[..^suffix.Length];
            if (trimmed.Length == 0 || trimmed == "/") break;
            return new UriBuilder(applicationUri) { Path = trimmed }.Uri;
        }
        return applicationUri;
    }

    /// <summary>True when the two addresses are the same page, ignoring the query.</summary>
    public static bool SamePage(Uri left, Uri right) =>
        Uri.Compare(left, right, UriComponents.Host | UriComponents.Path,
            UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0;

    /// <summary>
    /// Finds the description container and reports what it is, rather than returning bare text.
    ///
    /// <para>
    /// Named selectors are tried first — Ashby's <c>_descriptionText_</c>, Greenhouse's
    /// <c>#content</c>, Lever's posting body — and each candidate still has to look like prose, so a
    /// site that reuses one of those names for a form section does not win by matching. When nothing
    /// is named, the fallback scores every block on the page and takes the one with the most prose
    /// and the fewest controls, preferring the smallest node that still owns the text so a wrapper
    /// cannot drag the navigation and the footer in with it.
    /// </para>
    /// </summary>
    public const string ExtractScript = """
(() => {
  const txt = e => (e && e.innerText || '').replace(/\s+/g, ' ').trim();
  const controls = e => e.querySelectorAll(
    'input:not([type="hidden"]),select,textarea,[role="radio"],[role="checkbox"],[role="combobox"]').length;
  // A sentence here is a run of prose closed by punctuation. Form labels - "First Name", "Upload
  // file", "I don't wish to answer" - never satisfy it, which is the whole point of counting them.
  const sentences = s => (s.match(/[a-z][a-z,;'")\s-]{25,}?[.!?](\s|$)/g) || []).length;
  const owns = e => Array.from(e.children).some(
    c => txt(c).length > txt(e).length * 0.9);

  const named = [
    '[class*="descriptionText" i]', '[class*="jobDescription" i]', '[class*="job-description" i]',
    '[class*="posting-description" i]', '[data-testid*="description" i]', '[data-qa*="description" i]',
    '#job-description', '#jobDescription', '#content', '.job__description', '#overview',
  ];
  for (const sel of named) {
    for (const e of Array.from(document.querySelectorAll(sel))) {
      const t = txt(e);
      if (t.length >= 400 && sentences(t) >= 3 && controls(e) === 0)
        return { text: t, source: sel, controls: 0, sentences: sentences(t) };
    }
  }

  const scored = Array.from(document.querySelectorAll('div,section,article,main'))
    .map(e => ({ e, t: txt(e) }))
    .filter(x => x.t.length >= 400 && !owns(x.e))
    .map(x => ({ text: x.t, source: 'largest prose block', controls: controls(x.e), sentences: sentences(x.t) }))
    .filter(x => x.sentences >= 3)
    .sort((a, b) => (a.controls - b.controls) || (b.sentences - a.sentences) || (b.text.length - a.text.length));

  return scored[0] || { text: '', source: 'none', controls: 0, sentences: 0 };
})()
""";

    /// <summary>Reads a script result into a verdict, tolerating anything the page might return.</summary>
    public static JobDescriptionRead Read(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return JobDescriptionRead.Empty;
        return new JobDescriptionRead(
            root.TryGetProperty("text", out var text) ? text.GetString() ?? "" : "",
            root.TryGetProperty("source", out var source) ? source.GetString() ?? "?" : "?",
            root.TryGetProperty("controls", out var controls) && controls.TryGetInt32(out var c) ? c : 0,
            root.TryGetProperty("sentences", out var sentences) && sentences.TryGetInt32(out var s) ? s : 0);
    }

    /// <summary>
    /// Why text a person selected or pasted is not a usable job description, or an empty string.
    ///
    /// <para>
    /// There is no container to judge here, so the structural test is gone and the floor is lower: a
    /// person selecting the description will not include the navigation, and may reasonably select
    /// only the responsibilities. What remains worth catching is the wrong half of the page - a
    /// stray click that selected the form, or a selection so short it carries nothing to tailor to.
    /// </para>
    /// </summary>
    public static string RejectSupplied(string? text)
    {
        var tidy = (text ?? "").Trim();
        if (tidy.Length == 0) return "there is no text to use";
        if (tidy.Length < 300) return $"only {tidy.Length} characters - select the whole description";

        var lower = tidy.ToLowerInvariant();
        var hits = FormMarkers.Where(marker => lower.Contains(marker, StringComparison.Ordinal)).ToArray();
        if (hits.Length >= 4)
            return $"it reads as the application form ({string.Join(", ", hits.Take(4))})";
        return "";
    }

    /// <summary>Phrases that only ever appear in an application form or the chrome around it.</summary>
    private static readonly string[] FormMarkers =
    [
        "upload file", "drag and drop", "autofill from resume", "i don't wish to answer",
        "i prefer to self-describe", "submit application", "accept all", "necessary only",
        "customize settings", "cookie", "+ add education", "add another", "resume/cv",
    ];

    /// <summary>
    /// Why this read is not a usable job description, or an empty string when it is.
    ///
    /// <para>
    /// Structure decides first, because it is the reliable signal: prose in a container with no form
    /// controls. The phrase list is a second line for sites whose description container is not clean
    /// enough to be judged on controls alone — four distinct form phrases in what claims to be a job
    /// description is not a coincidence.
    /// </para>
    /// </summary>
    public static string Reject(JobDescriptionRead read)
    {
        var text = read.Text.Trim();
        if (text.Length == 0) return "no description text was found on the posting";
        if (text.Length < 400) return $"only {text.Length} characters of description text";
        if (read.Sentences < 3) return $"{text.Length} characters but only {read.Sentences} prose sentence(s)";
        if (read.Controls > 2) return $"the block holds {read.Controls} form controls, so it is a form";

        var lower = text.ToLowerInvariant();
        var hits = FormMarkers.Where(marker => lower.Contains(marker, StringComparison.Ordinal)).ToArray();
        if (hits.Length >= 4)
            return $"reads as an application form ({string.Join(", ", hits.Take(4))})";

        return "";
    }
}
