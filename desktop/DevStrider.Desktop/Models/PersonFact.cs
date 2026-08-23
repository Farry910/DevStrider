using CommunityToolkit.Mvvm.ComponentModel;

namespace DevStrider.Desktop.Models;

/// <summary>
/// One piece of personal reference data, in <c>ds_person_facts</c>.
///
/// <para>
/// Deliberately holds only what <c>ds_profiles</c> does not. The profile row owns name, location,
/// phone, personal email and LinkedIn, and it is the company portal's copy as well as ours — so it
/// stays the single source of truth for those and is never mirrored here. This table carries the
/// rest of what ChatGPT needs to answer an application honestly: education, career history, and any
/// custom field the user decides matters.
/// </para>
///
/// <para>
/// <see cref="Slot"/> is what lets one flat table hold three educations and any number of careers:
/// <c>career.company</c> and <c>career.period</c> sharing a slot are one row of the editor, and the
/// unique key is (user, profile, field name, slot).
/// </para>
/// </summary>
public sealed partial class PersonFact : ObservableObject
{
    public ObjectId Id { get; set; } = ObjectId.GenerateNewId();
    public long UserId { get; set; }
    public ObjectId ProfileId { get; set; }

    [ObservableProperty] private string _fieldName = "";
    [ObservableProperty] private string _fieldData = "";

    /// <summary>1-based position within a repeating group; 0 for a field that appears once.</summary>
    public int Slot { get; set; }

    public string Kind { get; set; } = PersonFactKinds.Text;
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public static class PersonFactKinds
{
    public const string Text = "text";
    public const string Education = "education";
    public const string Career = "career";
    public const string Custom = "custom";
}

public static class PersonFactFields
{
    public const string EducationDegree = "education.degree";
    public const string EducationSchool = "education.school";
    public const string EducationPeriod = "education.period";
    public const string CareerCompany = "career.company";
    public const string CareerPeriod = "career.period";

    /// <summary>BS through PhD — more than three on a resume is noise, and the macro has no room.</summary>
    public const int MaxEducation = 3;
}

/// <summary>One education row as the editor shows it, flattened to three facts on save.</summary>
public sealed partial class EducationEntry : ObservableObject
{
    public int Slot { get; set; }
    [ObservableProperty] private string _degree = "";
    [ObservableProperty] private string _school = "";
    [ObservableProperty] private string _period = "";

    public bool IsEmpty => string.IsNullOrWhiteSpace(Degree)
        && string.IsNullOrWhiteSpace(School) && string.IsNullOrWhiteSpace(Period);

    public string Describe() => string.Join(" — ",
        new[] { Degree, School, Period }.Where(part => !string.IsNullOrWhiteSpace(part)));
}

/// <summary>One career row: the company and when, which is all the resume needs from this side.</summary>
public sealed partial class CareerEntry : ObservableObject
{
    public int Slot { get; set; }
    [ObservableProperty] private string _company = "";
    [ObservableProperty] private string _period = "";

    public bool IsEmpty => string.IsNullOrWhiteSpace(Company) && string.IsNullOrWhiteSpace(Period);

    public string Describe() => string.Join(" — ",
        new[] { Company, Period }.Where(part => !string.IsNullOrWhiteSpace(part)));
}
