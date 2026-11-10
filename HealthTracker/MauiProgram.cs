using CommunityToolkit.Maui;
using HealthTracker.Repositories;
using HealthTracker.Services;
using HealthTracker.Services.Ai;
using HealthTracker.Services.Cloud;
using HealthTracker.Services.Security;
using HealthTracker.ViewModels;
using HealthTracker.Views;
using LiveChartsCore.SkiaSharpView.Maui;
using Plugin.LocalNotification;
using Plugin.Maui.OCR;
using SkiaSharp.Views.Maui.Controls.Hosting;

namespace HealthTracker;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .UseMauiCommunityToolkit()
            .ConfigureMauiHandlers(_ => Controls.TintedImageHandlers.Register())
            .UseSkiaSharp()
            .UseLiveCharts()
            .UseLocalNotification()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("PlusJakartaSans-Regular.ttf", "AppRegular");
                fonts.AddFont("PlusJakartaSans-SemiBold.ttf", "AppSemibold");
                fonts.AddFont("PlusJakartaSans-Bold.ttf", "AppBold");
                fonts.AddFont("PlusJakartaSans-Medium.ttf", "AppMedium");
                fonts.AddFont("PlusJakartaSans-ExtraBold.ttf", "AppExtraBold");
            });

        var services = builder.Services;

        // Database
        services.AddSingleton<DatabaseService>();

        // Repositories
        services.AddSingleton<MedicineRepository>();
        services.AddSingleton<MedicineScheduleRepository>();
        services.AddSingleton<MedicineLogRepository>();
        services.AddSingleton<MedicineCourseRepository>();
        services.AddSingleton<HealthMetricRepository>();
        services.AddSingleton<MedicalReportRepository>();
        services.AddSingleton<DoctorVisitRepository>();
        services.AddSingleton<PendingMedicineRepository>();
        services.AddSingleton<ReportPageRepository>();
        services.AddSingleton<LabResultRepository>();
        services.AddSingleton<UserProfileRepository>();

        // Platform / app-level services
        services.AddSingleton<ThemeService>();
#if ANDROID
        services.AddSingleton<IBiometricService, Platforms.Android.BiometricService>();
#endif

        // Services
        services.AddSingleton<IReminderService, ReminderService>();
        services.AddSingleton<MedicineService>();
        services.AddSingleton<MetricsService>();
        services.AddSingleton<LabService>();
        services.AddSingleton<ReportService>();
        services.AddSingleton<DoctorService>();
        services.AddSingleton<TimelineService>();
        services.AddSingleton<BackupService>();
        services.AddSingleton<IGoogleAuthService, GoogleAuthService>();
        services.AddSingleton<ICloudSyncService, GoogleDriveSyncService>();
        services.AddSingleton<IOcrService>(OcrPlugin.Default);
        services.AddSingleton<GeminiClient>();
        services.AddSingleton<PrescriptionAiParser>();
        services.AddSingleton<HealthContextBuilder>();
        services.AddSingleton<PrescriptionParserService>();
        services.AddSingleton<ReportTextParserService>();
        services.AddSingleton<AiParserService>();
        services.AddSingleton<PdfTextExtractorService>();
        services.AddSingleton<HealthReportService>();
        services.AddSingleton<AiChatService>();

        // ViewModels
        services.AddTransient<MedicineListViewModel>();
        services.AddTransient<MedicineDetailViewModel>();
        services.AddTransient<MedicineFormViewModel>();
        services.AddTransient<MetricsDashboardViewModel>();
        services.AddTransient<MetricHistoryViewModel>();
        services.AddTransient<MetricFormViewModel>();
        services.AddTransient<ReportsViewModel>();
        services.AddTransient<AddReportViewModel>();
        services.AddTransient<DoctorVisitsViewModel>();
        services.AddTransient<DoctorVisitDetailViewModel>();
        services.AddTransient<DoctorVisitFormViewModel>();
        services.AddTransient<TodayViewModel>();
        services.AddTransient<TimelineViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<PinLockViewModel>();
        services.AddTransient<HistoryViewModel>();
        services.AddTransient<ScanPrescriptionViewModel>();
        services.AddTransient<AskAiViewModel>();
        services.AddTransient<LabsViewModel>();
        services.AddTransient<LabEntryViewModel>();
        services.AddTransient<LabTestDetailViewModel>();
        services.AddTransient<ProfileViewModel>();
        services.AddTransient<ProfileFormViewModel>();

        // Views
        services.AddTransient<MedicineListPage>();
        services.AddTransient<MedicineDetailPage>();
        services.AddTransient<MedicineFormPage>();
        services.AddTransient<MetricsDashboardPage>();
        services.AddTransient<MetricHistoryPage>();
        services.AddTransient<MetricFormPage>();
        services.AddTransient<ReportsPage>();
        services.AddTransient<AddReportPage>();
        services.AddTransient<DoctorVisitsPage>();
        services.AddTransient<DoctorVisitDetailPage>();
        services.AddTransient<DoctorVisitFormPage>();
        services.AddTransient<TodayPage>();
        services.AddTransient<TimelinePage>();
        services.AddTransient<SettingsPage>();
        services.AddTransient<PinLockPage>();
        services.AddTransient<HistoryPage>();
        services.AddTransient<ScanPrescriptionPage>();
        services.AddTransient<AskAiPage>();
        services.AddTransient<LabsPage>();
        services.AddTransient<LabEntryPage>();
        services.AddTransient<LabTestDetailPage>();
        services.AddTransient<ProfilePage>();
        services.AddTransient<ProfileFormPage>();

        return builder.Build();
    }
}
