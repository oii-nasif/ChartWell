using System.Globalization;
using System.Windows.Input;
using HealthTracker.Helpers;
using HealthTracker.Models;
using HealthTracker.Repositories;

namespace HealthTracker.ViewModels;

/// <summary>Edit the health profile. Every field is optional.</summary>
public class ProfileFormViewModel : BaseViewModel
{
    private const string NotSet = "Prefer not to say";

    private readonly UserProfileRepository _profiles;
    private bool _initialized;
    private string _fullName = string.Empty;
    private string _selectedSex = NotSet;
    private bool _hasDateOfBirth;
    private DateTime _dateOfBirth = new(1990, 1, 1);
    private string _selectedBloodGroup = "Unknown";
    private string _heightInput = string.Empty;
    private string _allergies = string.Empty;
    private string _conditions = string.Empty;
    private string _emergencyName = string.Empty;
    private string _emergencyPhone = string.Empty;

    public ProfileFormViewModel(UserProfileRepository profiles)
    {
        _profiles = profiles;
        SaveCommand = new Command(async () => await Save());
    }

    public ICommand SaveCommand { get; }
    public event EventHandler? Saved;

    public List<string> SexOptions { get; } = [NotSet, UserProfile.Male, UserProfile.Female];
    public List<string> BloodGroupOptions { get; } = ["Unknown", .. UserProfile.BloodGroups];
    public DateTime MaxDateOfBirth => DateTime.Today;
    public DateTime MinDateOfBirth => new(1900, 1, 1);

    public string FullName { get => _fullName; set { _fullName = value ?? string.Empty; OnPropertyChanged(); } }
    public string SelectedSex { get => _selectedSex; set { if (value is null) return; _selectedSex = value; OnPropertyChanged(); } }
    public bool HasDateOfBirth { get => _hasDateOfBirth; set { _hasDateOfBirth = value; OnPropertyChanged(); } }
    public DateTime DateOfBirth { get => _dateOfBirth; set { _dateOfBirth = value; OnPropertyChanged(); } }
    public string SelectedBloodGroup { get => _selectedBloodGroup; set { if (value is null) return; _selectedBloodGroup = value; OnPropertyChanged(); } }
    public string HeightInput { get => _heightInput; set { _heightInput = value ?? string.Empty; OnPropertyChanged(); } }
    public string Allergies { get => _allergies; set { _allergies = value ?? string.Empty; OnPropertyChanged(); } }
    public string Conditions { get => _conditions; set { _conditions = value ?? string.Empty; OnPropertyChanged(); } }
    public string EmergencyName { get => _emergencyName; set { _emergencyName = value ?? string.Empty; OnPropertyChanged(); } }
    public string EmergencyPhone { get => _emergencyPhone; set { _emergencyPhone = value ?? string.Empty; OnPropertyChanged(); } }

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;
        IsBusy = true; ClearError();
        try
        {
            var p = await _profiles.GetAsync();
            FullName = p.FullName ?? string.Empty;
            SelectedSex = SexOptions.Contains(p.Sex ?? string.Empty) ? p.Sex! : NotSet;
            if (DateTime.TryParseExact(p.DateOfBirth, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dob))
            {
                DateOfBirth = dob;
                HasDateOfBirth = true;
            }
            SelectedBloodGroup = BloodGroupOptions.Contains(p.BloodGroup ?? string.Empty) ? p.BloodGroup! : "Unknown";
            HeightInput = p.HeightCm is { } h ? h.ToString("0.#", CultureInfo.InvariantCulture) : string.Empty;
            Allergies = p.Allergies ?? string.Empty;
            Conditions = p.Conditions ?? string.Empty;
            EmergencyName = p.EmergencyContactName ?? string.Empty;
            EmergencyPhone = p.EmergencyContactPhone ?? string.Empty;
        }
        catch (Exception ex)
        {
            SetError("Couldn't load your profile.");
            System.Diagnostics.Debug.WriteLine(ex);
        }
        finally { IsBusy = false; }
    }

    private async Task Save()
    {
        if (IsBusy) return;
        ClearError();

        double? height = null;
        if (!string.IsNullOrWhiteSpace(HeightInput))
        {
            if (!ValidationHelper.TryParseDouble(HeightInput, out var h) || h < 30 || h > 272)
            {
                SetError("Enter your height in centimetres, between 30 and 272.");
                return;
            }
            height = h;
        }
        if (HasDateOfBirth && DateOfBirth.Date > DateTime.Today)
        {
            SetError("Date of birth cannot be in the future.");
            return;
        }

        IsBusy = true;
        try
        {
            static string? Clean(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
            await _profiles.SaveAsync(new UserProfile
            {
                FullName = Clean(FullName),
                Sex = SelectedSex == NotSet ? null : SelectedSex,
                DateOfBirth = HasDateOfBirth ? DateTimeHelper.ToDateString(DateOfBirth) : null,
                BloodGroup = SelectedBloodGroup == "Unknown" ? null : SelectedBloodGroup,
                HeightCm = height,
                Allergies = Clean(Allergies),
                Conditions = Clean(Conditions),
                EmergencyContactName = Clean(EmergencyName),
                EmergencyContactPhone = Clean(EmergencyPhone),
                UpdatedAt = DateTimeHelper.ToDateTimeString(DateTime.Now),
            });
            Saved?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            SetError("Couldn't save your profile.");
            System.Diagnostics.Debug.WriteLine(ex);
        }
        finally { IsBusy = false; }
    }
}
