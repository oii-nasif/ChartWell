using System.Globalization;
using System.Windows.Input;
using HealthTracker.Constants;
using HealthTracker.Models;
using HealthTracker.Repositories;
using HealthTracker.Services;
using HealthTracker.Services.Labs;

namespace HealthTracker.ViewModels;

/// <summary>"Me" tab: who the user is, a count of what the app holds, emergency details and shortcuts.</summary>
public class ProfileViewModel : BaseViewModel
{
    private readonly UserProfileRepository _profiles;
    private readonly MedicineService _medicines;
    private readonly LabService _labs;
    private readonly ReportService _reports;
    private readonly DoctorService _doctors;
    private readonly MetricsService _metrics;
    private UserProfile _profile = new();
    private bool _isRefreshing;

    public ProfileViewModel(UserProfileRepository profiles, MedicineService medicines, LabService labs,
        ReportService reports, DoctorService doctors, MetricsService metrics)
    {
        _profiles = profiles;
        _medicines = medicines;
        _labs = labs;
        _reports = reports;
        _doctors = doctors;
        _metrics = metrics;
        LoadCommand = new Command(async () => await Load());
    }

    public ICommand LoadCommand { get; }

    public bool IsRefreshing
    {
        get => _isRefreshing;
        set { if (_isRefreshing == value) return; _isRefreshing = value; OnPropertyChanged(); }
    }

    public bool HasProfile => !_profile.IsEmpty;
    public string DisplayName => string.IsNullOrWhiteSpace(_profile.FullName) ? "Your health profile" : _profile.FullName!.Trim();

    public string Initials
    {
        get
        {
            var parts = (_profile.FullName ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length switch
            {
                0 => "N",
                1 => parts[0][..1].ToUpperInvariant(),
                _ => (parts[0][..1] + parts[^1][..1]).ToUpperInvariant()
            };
        }
    }

    /// <summary>"34 years · Male · O+" from whatever is filled in.</summary>
    public string Tagline
    {
        get
        {
            var bits = new List<string>();
            if (_profile.AgeOn(DateTime.Today) is { } age) bits.Add(age == 1 ? "1 year" : $"{age} years");
            if (!string.IsNullOrWhiteSpace(_profile.Sex)) bits.Add(_profile.Sex!);
            if (!string.IsNullOrWhiteSpace(_profile.BloodGroup)) bits.Add($"Blood group {_profile.BloodGroup}");
            return bits.Count > 0
                ? string.Join(" · ", bits)
                : "Add your age and sex so lab ranges and kidney estimates fit you.";
        }
    }

    public string EditButtonText => HasProfile ? "Edit profile" : "Set up profile";

    // Body measurements
    public string HeightText { get; private set; } = "—";
    public string WeightText { get; private set; } = "—";
    public string BmiText { get; private set; } = "—";
    public string BmiCategory { get; private set; } = string.Empty;
    public bool HasBmi => BmiCategory.Length > 0;

    // What the app holds
    public string MedicineCount { get; private set; } = "0";
    public string LabCount { get; private set; } = "0";
    public string ReportCount { get; private set; } = "0";
    public string VisitCount { get; private set; } = "0";

    // Emergency card
    public string Allergies => _profile.Allergies?.Trim() ?? string.Empty;
    public bool HasAllergies => Allergies.Length > 0;
    public string Conditions => _profile.Conditions?.Trim() ?? string.Empty;
    public bool HasConditions => Conditions.Length > 0;
    public string EmergencyContact
    {
        get
        {
            var name = _profile.EmergencyContactName?.Trim() ?? string.Empty;
            var phone = _profile.EmergencyContactPhone?.Trim() ?? string.Empty;
            return name.Length > 0 && phone.Length > 0 ? $"{name} · {phone}" : name + phone;
        }
    }
    public bool HasEmergencyContact => EmergencyContact.Length > 0;
    public string EmergencyPhone => _profile.EmergencyContactPhone?.Trim() ?? string.Empty;
    public bool CanCall => EmergencyPhone.Length > 0;
    public bool HasEmergencyInfo => HasAllergies || HasConditions || HasEmergencyContact;

    public async Task Load()
    {
        if (IsBusy) { IsRefreshing = false; return; }
        IsBusy = true; ClearError();
        try
        {
            _profile = await _profiles.GetAsync();

            var weight = await _metrics.GetLatestByType(MetricTypes.Weight);
            HeightText = _profile.HeightCm is { } h ? $"{h.ToString("0.#", CultureInfo.InvariantCulture)} cm" : "—";
            WeightText = weight is not null ? $"{weight.Value.ToString("0.#", CultureInfo.InvariantCulture)} kg" : "—";
            var bmi = LabCalculator.Bmi(weight?.Value, _profile.HeightCm);
            BmiText = bmi is { } b ? b.ToString("0.0", CultureInfo.InvariantCulture) : "—";
            BmiCategory = bmi is { } c ? LabCalculator.BmiCategory(c) : string.Empty;

            MedicineCount = (await _medicines.GetAllActiveMedicines()).Count.ToString(CultureInfo.InvariantCulture);
            LabCount = (await _labs.GetLatestPerTest()).Count.ToString(CultureInfo.InvariantCulture);
            ReportCount = (await _reports.GetAllReports()).Count.ToString(CultureInfo.InvariantCulture);
            VisitCount = (await _doctors.GetAllVisits()).Count.ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            SetError("Couldn't load your profile.");
            System.Diagnostics.Debug.WriteLine(ex);
        }
        finally
        {
            IsBusy = false;
            IsRefreshing = false;
            // Every property depends on the reloaded data.
            RefreshThemeBindings();
        }
    }
}
