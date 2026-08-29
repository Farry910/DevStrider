using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevStrider.Desktop.Services;

/// <summary>
/// What kind of claim a question is asking the applicant to make.
/// </summary>
public enum QuestionClass
{
    /// <summary>
    /// A statement of fact about the person that an employer can and will verify: work
    /// authorisation, sponsorship, citizenship, a degree, a licence, a certification, a security
    /// clearance, employment dates, criminal history, salary history, references. These are the
    /// answers a background check tests, and the ones that cost the applicant something real when
    /// they turn out to be wrong.
    /// </summary>
    Factual,

    /// <summary>
    /// A choice that is the applicant's to make and cannot be "wrong": consent to a background
    /// check, willingness to relocate, availability, notice period, acknowledgements, preferred
    /// pronouns, how they heard about the role. Answering these from judgement is fine — there is
    /// no fact being asserted, only a preference being stated.
    /// </summary>
    Attestable,

    /// <summary>
    /// Free text — "why this role", "describe a project". Generated prose is the point.
    /// </summary>
    Open,
}

/// <summary>A factual question the reference data does not settle.</summary>
/// <param name="Question">The question exactly as the form asked it.</param>
/// <param name="Proposed">What the model wanted to answer, kept so the review line can show it.</param>
/// <param name="Topic">Which kind of fact it wanted — for the message the user reads.</param>
public sealed record UngroundedAnswer(string Question, string Proposed, string Topic);

/// <summary>
/// The rule about what this app is allowed to make up.
///
/// <para>
/// The answer prompts used to say, in as many words: <i>where the reference data is silent, still
/// answer, and answer so that this application stays eligible</i> — and they said it specifically
/// of citizenship, work authorisation, degrees, licences, clearances and employment dates. That is
/// an instruction to state things about a real person, to an employer, that nobody has checked and
/// the applicant may never see. It is also, on its own terms, a bad trade: a fabricated clearance
/// does not survive the background check it exists to get past, so the application is lost anyway,
/// later, and with the applicant's name on the claim.
/// </para>
///
/// <para>
/// So the rule is: <b>a factual question is answered from the reference data or it is not answered
/// by this app at all.</b> When the data is silent the question is lifted out, the field is left
/// for a person, and the link parks at review — which is the gate the pipeline already has, used
/// for the thing it is for. Preferences, consents and free text are untouched; those were never
/// the problem.
/// </para>
///
/// <para>
/// Two mechanisms, because one is not enough. The prompt asks the model to return
/// <see cref="NeedsReviewSentinel"/> for anything the data does not settle — and a model that
/// ignores that instruction is exactly the failure being defended against, so
/// <see cref="Screen"/> independently checks that a factual answer has some support in the
/// reference data before it is allowed through.
/// </para>
/// </summary>
public static class ApplicationQuestionPolicy
{
    /// <summary>
    /// What the model is told to return for a factual question the reference data does not settle.
    /// Deliberately not a plausible answer: anything that could be typed into a form by accident
    /// would be worse than useless here.
    /// </summary>
    public const string NeedsReviewSentinel = "__DEVSTRIDER_NEEDS_REVIEW__";

    /// <summary>
    /// A factual topic: how to spot the question, and which reference keys would settle it.
    ///
    /// <para>
    /// <c>Hints</c> is the backstop. If a question is about a clearance and nothing in the person's
    /// reference data mentions a clearance, then whatever came back is not grounded in anything —
    /// regardless of how confident it sounded — and it is parked.
    /// </para>
    /// </summary>
    private sealed record Topic(string Name, string[] Asked, string[] Hints);

    private static readonly Topic[] Topics =
    [
        new("work authorisation",
            ["work authoriz", "work author", "authorized to work", "authorised to work",
             "legally authorized", "legally authorised", "right to work", "eligible to work",
             "work permit", "employment eligibility"],
            ["work auth", "authoriz", "authoris", "right to work", "eligib", "permit", "visa",
             "citizen", "nationality"]),

        new("visa sponsorship",
            ["sponsorship", "sponsor", "h-1b", "h1b", "tn visa", "opt", "cpt", "ead"],
            ["sponsor", "visa", "work auth", "citizen", "status"]),

        new("citizenship",
            ["citizenship", "citizen of", "are you a citizen", "nationality", "country of citizenship"],
            ["citizen", "nationality", "passport", "country"]),

        new("security clearance",
            ["security clearance", "clearance level", "do you hold a clearance", "ts/sci",
             "active clearance", "polygraph"],
            ["clearance", "ts/sci", "polygraph", "security"]),

        new("degree or education",
            ["degree", "highest level of education", "did you graduate", "gpa", "diploma",
             "which university", "what school", "field of study", "major"],
            ["degree", "education", "school", "university", "college", "gpa", "diploma", "major",
             "field of study", "graduat"]),

        new("licence or certification",
            ["licence", "license", "certification", "certified", "credential", "registered nurse",
             "pmp", "cpa", "bar admission"],
            ["licence", "license", "certif", "credential", "registration"]),

        new("employment history",
            ["employment date", "start date at", "end date at", "how long did you work",
             "years of experience", "how many years", "previous employer", "current employer",
             "have you worked at", "previously employed", "reason for leaving"],
            ["career", "employ", "company", "experience", "years", "period", "role", "title",
             "current employer", "previous"]),

        new("criminal or disciplinary history",
            ["convicted", "criminal", "felony", "misdemean", "arrested", "disciplinary action",
             "terminated for cause", "dismissed for"],
            ["criminal", "conviction", "felony", "record", "disciplinary"]),

        new("salary history",
            ["current salary", "current compensation", "salary history", "most recent salary",
             "what do you currently earn", "present salary"],
            ["salary", "compensation", "current salary", "earn", "pay"]),

        new("references",
            ["reference contact", "professional reference", "may we contact", "referee",
             "name of your supervisor", "supervisor's phone", "supervisor's email"],
            ["reference", "referee", "supervisor", "manager contact"]),

        new("age or date of birth",
            ["date of birth", "how old are you", "your age", "year of birth", "are you over 18",
             "are you at least 18"],
            ["birth", "age", "dob"]),
    ];

    /// <summary>
    /// Questions whose answer is the applicant's own choice rather than a checkable fact. These are
    /// matched <em>before</em> the factual list, because the wording overlaps: "are you willing to
    /// undergo a background check" is a consent, not a criminal-history disclosure, and "would you
    /// require sponsorship in future" is still a sponsorship fact. Only the clear consent shapes
    /// are listed, so anything ambiguous falls through to the stricter reading.
    /// </summary>
    private static readonly string[] AttestableMarkers =
    [
        "are you willing", "would you be willing", "do you consent", "do you agree",
        "i agree", "i consent", "i acknowledge", "do you accept", "are you comfortable",
        "willing to relocate", "willing to travel", "notice period", "when could you start",
        "earliest start", "available to start", "how did you hear", "where did you hear",
        "preferred name", "pronoun", "desired salary", "salary expectation",
        "expected salary", "compensation expectation", "do you have any questions",
    ];

    /// <summary>
    /// Questions the employer must ask and the applicant may always decline. Voluntary by law in
    /// most of the jurisdictions this app is used from, so the honest answer when nothing is on
    /// file is "prefer not to say" — not a guess, and not a blank that reads as an oversight.
    /// </summary>
    private static readonly string[] VoluntaryDisclosureMarkers =
    [
        "gender", "race", "ethnic", "hispanic", "latino", "veteran", "disability",
        "sexual orientation", "transgender", "eeo", "equal employment opportunity",
        "protected veteran", "self-identif", "self identif",
    ];

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    public static QuestionClass Classify(string? question)
    {
        var text = Normalize(question);
        if (text.Length == 0) return QuestionClass.Open;

        if (VoluntaryDisclosureMarkers.Any(text.Contains)) return QuestionClass.Attestable;
        if (AttestableMarkers.Any(text.Contains)) return QuestionClass.Attestable;
        if (TopicFor(text) != null) return QuestionClass.Factual;
        return QuestionClass.Open;
    }

    /// <summary>The factual topic a question belongs to, or null when it is not a factual question.</summary>
    private static Topic? TopicFor(string normalized) =>
        Topics.FirstOrDefault(topic => topic.Asked.Any(normalized.Contains));

    /// <summary>
    /// Splits a set of answers into the ones this app will type and the ones it will not.
    ///
    /// <para>
    /// An answer is dropped when it is the sentinel, when it is empty for a factual question, or
    /// when the question is factual and nothing in <paramref name="knownValues"/> speaks to its
    /// topic at all. That last test is the one that survives a model ignoring its instructions:
    /// the check is on what the reference data contains, not on what the reply claims.
    /// </para>
    /// </summary>
    /// <param name="answersJson">The flat <c>{question: answer}</c> object from the reply.</param>
    /// <param name="knownValues">Profile columns plus person facts — the grounding set.</param>
    /// <returns>The answers that may be typed, and the questions lifted out for a person.</returns>
    public static (string GroundedJson, List<UngroundedAnswer> NeedsReview) Screen(
        string? answersJson, IReadOnlyDictionary<string, string> knownValues)
    {
        var kept = new Dictionary<string, string>();
        var review = new List<UngroundedAnswer>();

        Dictionary<string, string>? answers;
        try
        {
            answers = JsonSerializer.Deserialize<Dictionary<string, string>>(
                string.IsNullOrWhiteSpace(answersJson) ? "{}" : answersJson!);
        }
        catch (JsonException) { answers = null; }

        if (answers == null || answers.Count == 0)
            return (JsonSerializer.Serialize(kept), review);

        // Everything the reference data actually says, lowercased once, so the topic test below is
        // a substring scan over both the key and the value: a fact can be named by its key
        // ("Work authorisation") or only by its value ("US citizen" under "Status").
        var grounding = string.Join(" \n ",
            knownValues.Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
                       .Select(pair => $"{pair.Key} {pair.Value}")).ToLowerInvariant();

        foreach (var (question, answer) in answers)
        {
            var value = (answer ?? "").Trim();
            var normalized = Normalize(question);

            // The model was asked to say so explicitly. Honour it whatever the question's class.
            if (value.Contains(NeedsReviewSentinel, StringComparison.OrdinalIgnoreCase))
            {
                review.Add(new UngroundedAnswer(question, "", TopicFor(normalized)?.Name ?? "this"));
                continue;
            }

            var kind = Classify(question);
            if (kind != QuestionClass.Factual)
            {
                // A voluntary disclosure with nothing on file gets the answer that is always
                // available and always true: the applicant is declining to say.
                if (value.Length == 0 && VoluntaryDisclosureMarkers.Any(normalized.Contains))
                    kept[question] = "Prefer not to say";
                else if (value.Length > 0)
                    kept[question] = value;
                continue;
            }

            if (value.Length == 0)
            {
                review.Add(new UngroundedAnswer(question, "", TopicFor(normalized)?.Name ?? "this"));
                continue;
            }

            var topic = TopicFor(normalized);
            if (topic != null && !topic.Hints.Any(grounding.Contains))
            {
                // Nothing on file speaks to this at all, so whatever came back was composed rather
                // than recalled. This is the case the whole class exists for.
                review.Add(new UngroundedAnswer(question, value, topic.Name));
                continue;
            }

            kept[question] = value;
        }

        return (JsonSerializer.Serialize(kept), review);
    }

    /// <summary>
    /// The paragraph both answer prompts carry. Replaces the old instruction to answer eligibility
    /// questions "so that this application stays eligible" where the data is silent.
    /// </summary>
    public static string PromptRules =>
        "GROUNDING RULE — this one overrides anything else here. Some of these questions ask for " +
        "facts about the applicant that an employer will verify: work authorisation, visa " +
        "sponsorship, citizenship, security clearance, degrees, licences, certifications, " +
        "employment dates and years of experience, criminal history, salary history, references, " +
        "and date of birth. Answer those ONLY from the reference data below. If the reference data " +
        "does not state it, you do not know it: return exactly " + NeedsReviewSentinel + " as that " +
        "question's answer. Do not infer it from the resume, from the job description, from what " +
        "would make the application succeed, or from what is statistically likely. An invented " +
        "eligibility answer is worse than a blank one — it fails the background check it was " +
        "meant to get past, and the applicant is the one who carries it. " +
        "Questions that are the applicant's own choice — consent, willingness to relocate or " +
        "travel, availability, notice period, acknowledgements, how they heard about the role, " +
        "desired salary — are not facts to look up: answer those normally. " +
        "Voluntary demographic questions (gender, race, ethnicity, veteran status, disability) " +
        "are answered 'Prefer not to say' unless the reference data states otherwise. " +
        "For any question that carries a list of options, pick one of the options exactly as " +
        "written — unless the grounding rule applies and you must return " + NeedsReviewSentinel + ".";

    private static string Normalize(string? text) =>
        Whitespace.Replace((text ?? "").Trim().ToLowerInvariant(), " ");
}
