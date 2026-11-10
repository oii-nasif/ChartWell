using CommunityToolkit.Maui.Alerts;
using HealthTracker.Helpers;
using HealthTracker.ViewModels;

namespace HealthTracker.Views;

public partial class DoctorVisitDetailPage : ContentPage
{
    private readonly DoctorVisitDetailViewModel _vm;
    private int? _visitId;
    private bool _loaded;
    private bool _refreshOnAppearing;

    public DoctorVisitDetailPage(DoctorVisitDetailViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = vm;
        vm.AttachReportRequested += OnAttachReportRequested;
    }

    /// <summary>Loads the visit now (callers await this before pushing). Used by HistoryPage and DoctorVisitsPage.</summary>
    public async Task LoadVisit(int visitId)
    {
        _visitId = visitId;
        _loaded = true;
        await _vm.Load(visitId);
    }

    /// <summary>Defers loading to OnAppearing (lighter alternative to <see cref="LoadVisit"/>).</summary>
    public void SetVisitId(int visitId)
    {
        _visitId = visitId;
        _loaded = false;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_visitId is not { } id) return;
        try
        {
            if (!_loaded)
            {
                _loaded = true;
                await _vm.Load(id);
            }
            else if (_refreshOnAppearing)
            {
                _refreshOnAppearing = false;
                await _vm.Load(id);
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
    }

    private async void OnEditClicked(object sender, EventArgs e)
    {
        if (_visitId is not { } id) return;
        _refreshOnAppearing = true;
        await NavigationHelper.PushAsync<DoctorVisitFormPage>(Navigation, p => p.LoadVisit(id));
    }

    private async void OnDeleteClicked(object sender, EventArgs e)
    {
        try
        {
            if (_vm.Visit is null) return;
            bool confirmed = await DisplayAlert("Delete visit",
                $"Delete the visit with {_vm.DoctorName}? Attached reports are kept. This cannot be undone.",
                "Delete", "Cancel");
            if (!confirmed) return;

            if (await _vm.Delete())
            {
                await Toast.Make("Visit deleted").Show();
                await Navigation.PopAsync();
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
    }

    private async void OnAttachReportRequested(object? sender, EventArgs e)
    {
        if (_vm.Visit is not { } visit) return;
        _refreshOnAppearing = true;
        await NavigationHelper.PushAsync<AddReportPage>(Navigation,
            p => p.PresetForVisit(visit.Id, visit.DoctorName, visit.Hospital));
    }

    private async void OnChangeImageClicked(object sender, EventArgs e)
    {
        try
        {
            var choice = await DisplayActionSheet("Prescription image", "Cancel", null, "Take photo", "Choose from gallery");
            FileResult? file = choice switch
            {
                "Take photo" => await CapturePhoto(),
                "Choose from gallery" => await MediaPicker.Default.PickPhotoAsync(),
                _ => null,
            };
            if (file is null) return;

            if (await _vm.ReplacePrescriptionImage(file.FullPath))
                await Toast.Make("Prescription image saved").Show();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            await DisplayAlert("Something went wrong", "Could not update the prescription image.", "OK");
        }
    }

    private async Task<FileResult?> CapturePhoto()
    {
        var status = await Permissions.RequestAsync<Permissions.Camera>();
        if (status != PermissionStatus.Granted)
        {
            await DisplayAlert("Camera", "Camera permission is required to take a photo.", "OK");
            return null;
        }
        if (!MediaPicker.Default.IsCaptureSupported)
        {
            await DisplayAlert("Camera", "No camera is available on this device.", "OK");
            return null;
        }
        return await MediaPicker.Default.CapturePhotoAsync();
    }

    private async void OnPrescriptionTapped(object sender, TappedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_vm.PrescriptionImagePath))
            await NavigationHelper.OpenFileAsync(_vm.PrescriptionImagePath, "Prescription");
    }

    private async void OnReportTapped(object sender, TappedEventArgs e)
    {
        if (e.Parameter is ReportListItem item)
            await NavigationHelper.OpenFileAsync(item.FilePath, item.Title);
    }

    private async void OnShareReportClicked(object sender, EventArgs e)
    {
        if ((sender as ImageButton)?.CommandParameter is ReportListItem item)
            await NavigationHelper.ShareFileAsync(item.FilePath, item.Title);
    }
}
