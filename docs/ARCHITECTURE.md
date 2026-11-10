# Architecture and conventions

Architecture notes, build commands and the rules to follow when changing this codebase.

## What this is

Chartwell (the product name; solution, namespaces, folders and `ApplicationId com.healthapp.healthtracker` keep the original `HealthTracker` name, do not rename them: the OAuth client and installed data are bound to the id) is an offline-first, single-user .NET MAUI app targeting **Android only** (`net9.0-android`). It tracks medicines/doses, lab results, vitals, a health profile, medical reports, doctor visits, and a timeline, with optional Gemini-powered OCR parsing and chat, and optional backup/restore to the user's own Google Drive (hidden app folder; no Chartwell server). All data is local SQLite plus files under the app data directory.

`PROJECT_LOGIC_DOCUMENTATION.md` at the repo root is a detailed, accurate walkthrough of every service and ViewModel; read it before touching business logic. `DESIGN_SYSTEM.md` (this folder) is the contract for every XAML page (tokens, styles, controls, page anatomy); read it before touching UI. `CLOUD_SETUP.md` is the user-facing Google Drive setup.

## Commands

The solution file lives in the app folder, not the repo root: `HealthTracker/HealthTracker.sln`. It includes both the app and the test project.

```powershell
# Build the Android app. Name the csproj explicitly: the folder also holds the .sln, and building
# the .sln with -f net9.0-android fails because the test project has no Android target.
dotnet build HealthTracker/HealthTracker.csproj -f net9.0-android "-p:AndroidSdkDirectory=<path to your Android SDK>"

# Run unit tests (from repo root)
dotnet test HealthTracker.Tests/HealthTracker.Tests.csproj

# Run a single test class / method
dotnet test HealthTracker.Tests/HealthTracker.Tests.csproj --filter "FullyQualifiedName~MedicineServiceTests"
dotnet test HealthTracker.Tests/HealthTracker.Tests.csproj --filter "FullyQualifiedName~MedicineServiceTests.AddMedicine_WithNoSchedules_ThrowsInvalidOperationException"
```

Toolchain notes:
- `HealthTracker/global.json` pins the SDK to 9.0.3xx. The `maui-android` workload for .NET 9 is installed.
- `HealthTracker/NuGet.Config` and `HealthTracker.Tests/nuget.config` both `<clear/>` sources and use only nuget.org (a corporate Telerik feed was blocking workload restore). Keep them.
- A clean app build produces 0 errors; the ~1 warning is an SDK EOL notice. XamlC `XC0022` hints (missing `x:DataType`) are informational.
- Package pins that matter: `CommunityToolkit.Maui` 12.x (13+ is .NET 10), `Xamarin.AndroidX.Biometric` 1.1.0.28 (newer pulls AndroidX 1.13+ and breaks dex merging), `LiveChartsCore.SkiaSharpView.Maui` 2.0.5 (has a net9 target). QuestPDF was removed because it ships no Android native runtime.
- Device deploy is done from Visual Studio 2022 by opening the `.sln`; there is no CLI deploy script.

## Architecture

- **Navigation** goes through `Helpers/NavigationHelper.cs`: `NavigationHelper.PushAsync<SomePage>(Navigation, p => p.SetMedicineId(id))` resolves the page from `App.Services`, guards against double taps and catches exceptions. Data is passed by calling a method on the page first (`SetMedicineId`, `LoadMedicine`, `LoadVisit`, `PresetForVisit`, `LoadType`, `ShowSegment`), not Shell query properties. Open/share stored files only via `NavigationHelper.OpenFileAsync/ShareFileAsync` (they resolve relative paths). Add/Edit flows are single pages: `MedicineFormPage`, `MetricFormPage`, `DoctorVisitFormPage`.
- **Tabs** (`AppShell.xaml`): Home (`today`), Labs (`labs`), Vitals (`metrics`), Records (top tabs `reports` / `doctors` / `timeline`), Me (`me`). Switch tabs with `NavigationHelper.GoToTabAsync(route)`.
- **Lab results** (`Constants/LabCatalog.cs`, `Services/LabService.cs`, `Services/Labs/*`, all MAUI-free and linked into the tests). A `LabResult` stores the value as entered with its unit plus a snapshot of the range / expected word it was judged against, so never re-derive a saved result's flag from the catalog; use `LabEvaluator.Evaluate(result)`. Add a test by adding a definition to `LabCatalog.Build()` (unique code, aliases, typical adult range; the catalog tests check integrity). Saving goes through `LabService.AddResults`, which validates, snapshots ranges for the profile's sex and appends derived values (`LabCalculator`); don't insert `LabResult` rows from ViewModels. `UserProfile` is a single row (Id 1) read through `UserProfileRepository.GetAsync`.
- **Resource keys**: a theme token key must never equal a style key in `Styles.xaml` (the theme dictionary is merged later and wins; the page then crashes at runtime with "Cannot assign property Style" and the build does not catch it). After adding tokens or styles, launch the app.
- **Theming**: colours are semantic tokens in `Resources/Themes/LightTheme.xaml` + `DarkTheme.xaml` (identical keys); `Services/ThemeService.cs` swaps the dictionary at runtime (System/Light/Dark preference). Everything binds colours with `DynamicResource`; keyed styles live in `Resources/Styles.xaml`; ViewModels expose token names ("Success", "MetricHeartRate") consumed via `ThemeColorConverter`, never hex. Icons are Material SVGs in `Resources/Images/ic_*.svg`, tinted with CommunityToolkit `IconTintColorBehavior`. No `Frame`, no emoji.
- **`BaseViewModel`** is a hand-rolled `INotifyPropertyChanged` with `IsBusy`/`HasError`/`ErrorMessage`. No CommunityToolkit.Mvvm; properties and commands are written out manually.
- **`BaseRepository<T>`** gives generic CRUD over `sqlite-net-pcl`; concrete repositories add typed queries. Models are plain classes with SQLite attributes. Dates and times are stored as strings (`"yyyy-MM-dd"`, `"HH:mm"`); see `Helpers/DateTimeHelper.cs`.
- **`DatabaseService.GetConnectionAsync`** lazily opens `healthapp.db`, calls `CreateTableAsync` for every model, then runs `DatabaseMigrator.MigrateAsync` (versioned via `PRAGMA user_version`, each step in a transaction, idempotent). When you add a model, add it to both `GetConnectionAsync` and `DeleteAllDataAsync` (and the test `DatabaseService`). When you change a column in a way `CreateTableAsync` won't handle, bump `DatabaseMigrator.CurrentVersion` and add a step. `CloseAsync()` exists so backup restore can swap the file.
- **Stored file paths are relative** to `FileStorageHelper.GetBaseFolder()` (`MedicalReport.FilePath`, `ReportPage.FilePath`, `DoctorVisit.PrescriptionImagePath`). Write with `FileStorageHelper.ToStoredPath`, read with `ResolvePath`. `ReportService`/`DoctorService` read methods already return resolved absolute paths; never write a resolved path back through a repository.
- **Reminders** (`Services/ReminderService.cs`, behind `IReminderService`) use Plugin.LocalNotification with deterministic IDs (bands documented in `IReminderService.cs`): dose on-time/early, doctor follow-up (evening before + morning of), refill nudge, snooze, confirmation. Dose notifications carry Take / Snooze actions handled in the background (`ReminderService.AttachNotificationHandlers` is called from `MainApplication.OnCreate`; `Initialize()` again from `App.OnStart` is idempotent). `RescheduleAll()` runs every launch and honours StartDate/EndDate and slots already logged today.
- **Daily housekeeping** runs from `TodayViewModel.Load`: `MedicineService.AutoLogMissedDoses()` and `CheckAndCompleteCourses()` run before the Today list is rendered. Dose/stock/course invariants live in `MedicineService`; don't update those tables directly from ViewModels. Key rules: `LogDoseTaken` is idempotent (returns null when already logged) and never blocks on stock (stock clamps at 0, `IsOutOfStock` is a soft warning); the "which slots count today" rule is `MedicineService.GetReachableSlotsForDay` (honours start time, per-schedule `CreatedAt`, EndDate) — use it, don't re-implement; auto-miss extends the course via `Medicine.ExtensionDays`, capped at `PrescribedDays`. After any dose/stock mutation call `HealthWidget.RequestUpdate()` under `#if ANDROID`.
- **Android-specific code** lives in `Platforms/Android/` (home-screen widget that reads SQLite read-only, exact-alarm permission prompt in `App.xaml.cs` under `#if ANDROID`). Only `net9.0-android` is in `TargetFrameworks`, so `Platforms/Android/` is the only platform folder.
- **AI features** go through `Services/Ai/GeminiClient.cs` (key in `SecureStorage["gemini_api_key"]`, sent as `x-goog-api-key`, JSON-schema structured output, multimodal images, typed `GeminiResult<T>` with `GeminiErrorKind`). `AiParserService`, `PrescriptionAiParser`, `AiChatService` (context from `HealthContextBuilder`) sit on top. Optional; every path falls back to on-device OCR + regex parsers.
- **Cloud backup** (`Services/Cloud/`): `IGoogleAuthService` (WebAuthenticator + PKCE, tokens in SecureStorage) and `ICloudSyncService` (Drive `appDataFolder`, one ZIP, resumable upload, 24h auto-sync from `App.OnSleep`). `BackupService` builds the ZIP with `VACUUM INTO` and restores atomically. The OAuth client ids are compile-time constants in `Constants/CloudConstants.cs`, one per build configuration (`#if DEBUG`), because an Android OAuth client is bound to one signing SHA-1 (placeholders ship; see `docs/CLOUD_SETUP.md`).
- **Security**: PIN in `SecureStorage["app_pin"]`, optional biometric unlock (`Services/Security/IBiometricService`, Android impl in `Platforms/Android/BiometricService.cs`, preference `biometric_unlock`). `App.OnStart` locks BEFORE any permission prompt and fails closed if SecureStorage throws.
- **PDF export** (`Services/HealthReportService.cs` + `Services/Pdf/`) renders with SkiaSharp `SKDocument` off the UI thread; images are downscaled before embedding.

## Tests

`HealthTracker.Tests` is a plain `net9.0` xUnit project that **does not reference the MAUI app**. It compiles app sources in via `<Compile Include="..\HealthTracker\...">` links, and only files with no MAUI dependency can be linked. Two test doubles live in the same namespaces as the real types and shadow them:

- `TestHelpers/TestDatabaseService.cs` defines `HealthTracker.Services.DatabaseService` backed by an in-memory SQLite connection. sqlite-net pools `:memory:`, so that database is shared by every test using the default constructor; a test that reads whole tables should pass its own temp file path (`new DatabaseService(path)`, see `LabServiceTests`).
- `TestHelpers/NullReminderService.cs` is a no-op `IReminderService`.

Consequences:
- If you add a dependency to a linked file (e.g. a new repository injected into `MedicineService`), you must also link the new model/repository in `HealthTracker.Tests.csproj` and add its `CreateTableAsync` to the test `DatabaseService`, or the test project stops compiling.
- Never reference `Microsoft.Maui.*`, `Preferences`, `FileSystem`, `Shell`, etc. from a linked file. Put MAUI-touching code in files that are not linked (e.g. `FileStorageHelper.cs`, `ReminderService.cs`).

- **PIN lock** is pushed as a modal page from `App.CheckPinAndLock`, and `PinLockPage` swallows the back button. Keep it modal; a pushed page can be popped by the Shell back arrow.
- **Notification IDs**: see the band table in `Services/IReminderService.cs`. `RescheduleAll` runs `CancelAll` first, so the scheme can change between versions safely.
- **Parallel builds**: concurrent `dotnet build` runs in the same folder corrupt `obj/`. Use `-p:BaseIntermediateOutputPath=<short temp path>/obj/ -p:BaseOutputPath=.../bin/ "-p:DefaultItemExcludes=obj/**"` for an isolated build (deep temp paths break aapt2).
