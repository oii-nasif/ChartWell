using CommunityToolkit.Maui.Alerts;
using HealthTracker.Helpers;
using HealthTracker.ViewModels;

namespace HealthTracker.Views;

public partial class DoctorVisitsPage : ContentPage
{
    private readonly DoctorVisitsViewModel _vm;

    public DoctorVisitsPage(DoctorVisitsViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = vm;
        // EmptyView is not guaranteed to inherit the page's BindingContext, so wire it explicitly.
        EmptyState.BindingContext = vm;
        vm.AddRequested += OnAddRequested;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try { await _vm.Load(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
    }

    // NavigationHelper catches its own exceptions, so these handlers are safe as async void.
    private async void OnAddRequested(object? sender, EventArgs e)
        => await NavigationHelper.PushAsync<DoctorVisitFormPage>(Navigation);

    private async void OnVisitTapped(object sender, TappedEventArgs e)
    {
        if (e.Parameter is DoctorVisitListItem item)
            await NavigationHelper.PushAsync<DoctorVisitDetailPage>(Navigation, p => p.LoadVisit(item.Id));
    }

    private async void OnFollowUpTapped(object sender, TappedEventArgs e)
    {
        if (e.Parameter is DoctorFollowUpItem item)
            await NavigationHelper.PushAsync<DoctorVisitDetailPage>(Navigation, p => p.LoadVisit(item.VisitId));
    }

    private async void OnDeleteInvoked(object sender, EventArgs e)
    {
        try
        {
            if ((sender as SwipeItem)?.BindingContext is not DoctorVisitListItem item) return;

            bool confirmed = await DisplayAlert("Delete visit",
                $"Delete the visit with {item.DoctorName}? Attached reports are kept. This cannot be undone.",
                "Delete", "Cancel");
            if (!confirmed) return;

            if (await _vm.Delete(item))
                await Toast.Make("Visit deleted").Show();
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
    }
}
