using System.Text.Json;
using DevStrider.Desktop.Data;
using DevStrider.Desktop.Models;

namespace DevStrider.Desktop.Services;

/// <summary>
/// Personal reference data for a profile, and the block of it handed to ChatGPT.
///
/// <para>
/// Two shapes come out of here. <see cref="LoadAsync"/> returns the editor's view — education and
/// career rows plus custom fields. <see cref="BuildReferenceAsync"/> returns the flat dictionary the
/// prompts and the form filler use, which merges the profile's own columns in front of the facts:
/// <c>ds_profiles</c> is shared with the company portal and stays the single source of truth for
/// name, location, phone, email and LinkedIn, so those are read from it and never stored here.
/// </para>
/// </summary>
public sealed class PersonFactsService
{
    private readonly IPersonFactRepository _facts;

    public PersonFactsService(IPersonFactRepository facts) => _facts = facts;

    public sealed record PersonalData(
        List<EducationEntry> Education,
        List<CareerEntry> Careers,
        List<PersonFact> Custom);

    public async Task<PersonalData> LoadAsync(ObjectId profileId)
    {
        var rows = await _facts.ListByProfileAsync(profileId);

        var education = rows.Where(row => row.Kind == PersonFactKinds.Education)
            .GroupBy(row => row.Slot).OrderBy(group => group.Key)
            .Select(group => new EducationEntry
            {
                Slot = group.Key,
                Degree = Value(group, PersonFactFields.EducationDegree),
                School = Value(group, PersonFactFields.EducationSchool),
                Period = Value(group, PersonFactFields.EducationPeriod),
            }).ToList();

        var careers = rows.Where(row => row.Kind == PersonFactKinds.Career)
            .GroupBy(row => row.Slot).OrderBy(group => group.Key)
            .Select(group => new CareerEntry
            {
                Slot = group.Key,
                Company = Value(group, PersonFactFields.CareerCompany),
                Period = Value(group, PersonFactFields.CareerPeriod),
            }).ToList();

        var custom = rows.Where(row => row.Kind is PersonFactKinds.Custom or PersonFactKinds.Text)
            .OrderBy(row => row.SortOrder).ToList();

        return new PersonalData(education, careers, custom);
    }

    public Task SaveAsync(ObjectId profileId, PersonalData data)
    {
        var facts = new List<PersonFact>();
        var slot = 0;
        foreach (var entry in data.Education.Where(entry => !entry.IsEmpty).Take(PersonFactFields.MaxEducation))
        {
            slot++;
            Add(facts, PersonFactFields.EducationDegree, entry.Degree, slot, PersonFactKinds.Education);
            Add(facts, PersonFactFields.EducationSchool, entry.School, slot, PersonFactKinds.Education);
            Add(facts, PersonFactFields.EducationPeriod, entry.Period, slot, PersonFactKinds.Education);
        }

        slot = 0;
        foreach (var entry in data.Careers.Where(entry => !entry.IsEmpty))
        {
            slot++;
            Add(facts, PersonFactFields.CareerCompany, entry.Company, slot, PersonFactKinds.Career);
            Add(facts, PersonFactFields.CareerPeriod, entry.Period, slot, PersonFactKinds.Career);
        }

        var order = 0;
        foreach (var fact in data.Custom.Where(fact => !string.IsNullOrWhiteSpace(fact.FieldName)))
        {
            fact.Kind = PersonFactKinds.Custom;
            fact.Slot = 0;
            fact.SortOrder = order++;
            facts.Add(fact);
        }

        return _facts.ReplaceForProfileAsync(profileId, facts);
    }

    /// <summary>
    /// Everything known about the person, flattened for prompts and for the form filler. Profile
    /// columns go in first so a custom field can override nothing that the portal owns.
    /// </summary>
    public async Task<Dictionary<string, string>> BuildReferenceAsync(Profile? profile)
    {
        var reference = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (profile == null) return reference;

        var names = profile.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Put(reference, "full name", profile.Name);
        if (names.Length > 0) Put(reference, "first name", names[0]);
        if (names.Length > 1) Put(reference, "last name", names[^1]);
        Put(reference, "email", profile.PersonalEmail);
        Put(reference, "phone", profile.Phone);
        Put(reference, "location", profile.Location);
        Put(reference, "linkedin", profile.LinkedinUrl);
        Put(reference, "headline", profile.Headline);

        // Answers worth having before anyone types one. Keyed on the shortest wording that still
        // matches — the filler scores a key that is contained in the label, so "how did you hear"
        // catches "How did you hear about Forum?" and "How did you hear about us?" alike. Written
        // first so a custom field of the same name replaces it.
        Put(reference, "how did you hear", "LinkedIn");

        var data = await LoadAsync(profile.Id);
        var education = data.Education.Where(entry => !entry.IsEmpty).Select(entry => entry.Describe()).ToList();
        if (education.Count > 0) Put(reference, "education", string.Join("; ", education));
        var careers = data.Careers.Where(entry => !entry.IsEmpty).Select(entry => entry.Describe()).ToList();
        if (careers.Count > 0) Put(reference, "career history", string.Join("; ", careers));
        foreach (var fact in data.Custom) Put(reference, fact.FieldName, fact.FieldData);
        return reference;
    }

    /// <summary>
    /// Adds or replaces one custom fact, leaving the rest alone. The Quick answers tab writes
    /// through here: an answer given once becomes reference data and fills itself next time.
    /// </summary>
    public async Task AddCustomAsync(ObjectId profileId, string fieldName, string fieldData)
    {
        var name = (fieldName ?? "").Trim();
        if (name.Length == 0 || string.IsNullOrWhiteSpace(fieldData)) return;

        var data = await LoadAsync(profileId);
        var existing = data.Custom.FirstOrDefault(fact =>
            string.Equals(fact.FieldName, name, StringComparison.OrdinalIgnoreCase));
        if (existing != null) existing.FieldData = fieldData.Trim();
        else data.Custom.Add(new PersonFact
        {
            FieldName = name,
            FieldData = fieldData.Trim(),
            Kind = PersonFactKinds.Custom,
        });
        await SaveAsync(profileId, data);
    }

    public async Task<string> BuildReferenceJsonAsync(Profile? profile) =>
        JsonSerializer.Serialize(await BuildReferenceAsync(profile),
            new JsonSerializerOptions { WriteIndented = true });

    private static string Value(IEnumerable<PersonFact> group, string field) =>
        group.FirstOrDefault(row => row.FieldName == field)?.FieldData ?? "";

    private static void Add(List<PersonFact> facts, string field, string value, int slot, string kind)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        facts.Add(new PersonFact { FieldName = field, FieldData = value, Slot = slot, Kind = kind, SortOrder = slot });
    }

    private static void Put(Dictionary<string, string> map, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value)) map[key.Trim()] = value.Trim();
    }
}
