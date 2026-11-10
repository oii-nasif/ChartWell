using System.Globalization;
using SQLite;

namespace HealthTracker.Models;

/// <summary>
/// The single user's health profile (always row Id = 1). Sex and date of birth pick lab reference
/// ranges and feed eGFR; the rest is shown on the profile card, in the PDF export and to the AI assistant.
/// </summary>
[Table("UserProfile")]
public class UserProfile
{
    public const int SingletonId = 1;
    public const string Male = "Male";
    public const string Female = "Female";

    public static readonly string[] BloodGroups = ["A+", "A-", "B+", "B-", "AB+", "AB-", "O+", "O-"];

    [PrimaryKey]
    public int Id { get; set; } = SingletonId;

    public string? FullName { get; set; }

    /// <summary>"Male", "Female" or null when not given.</summary>
    public string? Sex { get; set; }

    /// <summary>"yyyy-MM-dd" or null.</summary>
    public string? DateOfBirth { get; set; }

    public string? BloodGroup { get; set; }
    public double? HeightCm { get; set; }
    public string? Allergies { get; set; }
    public string? Conditions { get; set; }
    public string? EmergencyContactName { get; set; }
    public string? EmergencyContactPhone { get; set; }
    public string? UpdatedAt { get; set; }

    /// <summary>Completed years on <paramref name="onDate"/>, or null without a valid date of birth.</summary>
    public int? AgeOn(DateTime onDate)
    {
        if (!DateTime.TryParseExact(DateOfBirth, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dob))
            return null;
        if (dob > onDate) return null;
        var age = onDate.Year - dob.Year;
        if (onDate < dob.AddYears(age)) age--;
        return age;
    }

    [Ignore]
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(FullName) && string.IsNullOrWhiteSpace(Sex) && string.IsNullOrWhiteSpace(DateOfBirth)
        && string.IsNullOrWhiteSpace(BloodGroup) && HeightCm is null && string.IsNullOrWhiteSpace(Allergies)
        && string.IsNullOrWhiteSpace(Conditions) && string.IsNullOrWhiteSpace(EmergencyContactName)
        && string.IsNullOrWhiteSpace(EmergencyContactPhone);
}
