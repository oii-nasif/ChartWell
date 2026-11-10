# Chartwell Project Logic Documentation

The app is published as **Chartwell**; the solution, namespaces and `ApplicationId` keep the original `HealthTracker` name.

## 1) Project Scope
Chartwell is an offline-first, single-user personal health app built with .NET MAUI, targeting Android only (`net9.0-android`). It covers medicine management and dose tracking, lab results (numeric and positive/negative tests with reference ranges and trends), vitals, a health profile, medical reports (multi-page, OCR/AI-assisted), doctor visits with follow-up reminders, a unified timeline/history, an "Ask AI" assistant over the user's own data, PDF health-report export, local ZIP backup/restore, optional backup to the user's own Google Drive, and an app lock (PIN + optional biometrics).

Primary solution files:
- `HealthTracker/HealthTracker.sln` (includes the app and the test project)
- `HealthTracker/HealthTracker.csproj` (main app, `net9.0-android`, `ApplicationId com.healthapp.healthtracker`, min SDK 23)
- `HealthTracker.Tests/HealthTracker.Tests.csproj` (xUnit, plain `net9.0`)

Companion documents:
- `docs/DESIGN_SYSTEM.md`: the UI contract (tokens, typography, containers, buttons, icons, shared controls, page anatomy). This document does not repeat styling rules.
- `docs/CLOUD_SETUP.md`: user-facing one-time Google Cloud / OAuth client setup.
- `health_tracker_pdf.html`: the original spec.

## 2) Technical Architecture
Architecture style:
- MVVM: `Views` (XAML pages + code-behind) + `ViewModels` (hand-rolled `INotifyPropertyChanged` via `BaseViewModel`; no CommunityToolkit.Mvvm).
- Service layer owns business rules; repositories own persistence; `DatabaseService` owns the SQLite connection.
- Dependency flow: View -> ViewModel -> Service -> Repository -> `DatabaseService` -> SQLite (`sqlite-net-pcl`).
- ViewModels never navigate. They raise events (`SavedSuccessfully`, `AddRequested`, `OpenSettingsRequested`, `DoseLogged`, `RestoreCompleted`, ...) and the page navigates via `NavigationHelper`.
- ViewModels expose theme token names ("Success", "MetricHeartRate") and icon file names ("ic_check_circle.png"), never hex colours or emoji; pages resolve tokens with `ThemeColorConverter`.

DI registration (`HealthTracker/MauiProgram.cs`): all repositories and services are singletons (including `IReminderService -> ReminderService`, `IGoogleAuthService -> GoogleAuthService`, `ICloudSyncService -> GoogleDriveSyncService`, `IOcrService -> OcrPlugin.Default`, `GeminiClient`, `PrescriptionAiParser`, `HealthContextBuilder`, `ThemeService`, and under `#if ANDROID` `IBiometricService -> Platforms.Android.BiometricService`); every ViewModel and page is transient. Builder calls: `UseMauiCommunityToolkit`, `UseSkiaSharp`, `UseLiveCharts`, `UseLocalNotification`.

Key external libraries (pinned in the csproj):
- `Plugin.LocalNotification` 11.1.4 (reminders), `Plugin.Maui.OCR` 1.1.1 (on-device OCR)
- `sqlite-net-pcl` 1.9.172 + `SQLitePCLRaw.bundle_green`
- `LiveChartsCore.SkiaSharpView.Maui` 2.0.5 (metric charts), `SkiaSharp` 3.119 (PDF export via `SKDocument`)
- `UglyToad.PdfPig` (PDF text extraction), `Google.Apis.Drive.v3` (Drive upload/download)
- `CommunityToolkit.Maui` 12.x (icon tinting, toasts), `Xamarin.AndroidX.Biometric` 1.1.0.28
- QuestPDF and Microcharts were removed.

## 3) Startup and App Lifecycle Logic
Process start (`Platforms/Android/MainApplication.OnCreate`): calls `ReminderService.AttachNotificationHandlers(services)` so notification action taps ("Take"/"Snooze") are handled even when the process is cold and no MAUI `App` exists yet. Idempotent.

`App` constructor (`App.xaml.cs`): stores the service provider in `App.Services`, calls `ThemeService.Initialize(this)` (applies the saved Light/Dark/System dictionary and subscribes to `RequestedThemeChanged`). `CreateWindow` returns a `Window(new AppShell())`.

`App.OnStart` (async void; every step is individually try/caught):
1. `CheckPinAndLock()` FIRST, so no health data is visible behind later permission prompts. Reads `SecureStorage["app_pin"]`; if the read throws, it fails closed (treats as locked). If a PIN exists and no `PinLockPage` is already on the modal stack, it pushes `PinLockPage` modally (non-animated).
2. `ReminderService.Initialize()` (idempotent re-attach of channels/handlers).
3. Requests notification permission (Android 13+).
4. `CheckExactAlarmPermission()`: on Android 12+, if exact alarms are not allowed, asks once per install (`Preferences["exact_alarm_prompted"]`) and can open the system "Alarms & Reminders" settings.
5. `IReminderService.RescheduleAll()`.
6. `TriggerCloudAutoSync()` -> fire-and-forget `ICloudSyncService.AutoSyncIfDueAsync()` on a background task.

`OnSleep`: records the background timestamp and triggers cloud auto-sync again (the user has just finished editing). `OnResume`: if backgrounded >= `AppConstants.BackgroundResumeThresholdMinutes` (2), re-runs `CheckPinAndLock()`.

`MainActivity` is `LaunchMode.SingleTop` (notification taps re-use the running activity) and calls `HealthWidget.RequestUpdate(this)` in `OnResume`. `WebAuthenticatorCallbackActivity` receives the OAuth redirect (`DataScheme = CloudConstants.RedirectScheme`).

Shell (`AppShell.xaml`): five tabs (Home, Labs, Vitals, Records, Me; see section 9) plus two global toolbar items (Ask AI, Settings) pushed through a double-tap guard. Routes are registered for `MedicineDetailPage`, `MedicineListPage`, `SettingsPage`, `AskAiPage`, `PinLockPage` only so `GoToAsync` deep links could work; normal navigation uses `NavigationHelper` (section 9).

## 4) Persistence, Migrations and Data Model Logic
Database file: `healthapp.db` in `FileSystem.AppDataDirectory` (`FileStorageHelper.GetDatabasePath()`).

`Services/DatabaseService.cs`:
- `GetConnectionAsync()` lazily opens the connection under a `SemaphoreSlim` (launch-time callers race), runs `CreateTableAsync` for every model (`Medicine`, `MedicineSchedule`, `MedicineLog`, `MedicineCourse`, `HealthMetric`, `MedicalReport`, `DoctorVisit`, `PendingMedicine`, `ReportPage`, `LabResult`, `UserProfile`), then `DatabaseMigrator.MigrateAsync(conn, FileStorageHelper.ToStoredPath)`.
- `CloseAsync()` drops the connection so a backup restore can replace the file; the next `GetConnectionAsync` re-opens and re-migrates.
- `DeleteAllDataAsync()` empties every table (used by Settings "Delete all data" and the PIN "Forgot PIN" reset).

`Services/DatabaseMigrator.cs` (MAUI-free, unit-tested):
- Versioned with `PRAGMA user_version`; `CurrentVersion = 3`. Each step is idempotent, table rebuilds run in one transaction, and a stale `<table>_new` left by a crash is dropped first.
- v1: removes legacy columns by rebuilding tables (`MedicineSchedule.DoseAmount`, `Medicine.Dosage`), ensuring `IsOngoing`/`SortOrder` exist.
- v2: adds `Medicine.ExtensionDays` (backfilled to 0) and `MedicineSchedule.CreatedAt` (backfilled from the owning medicine's `CreatedAt` as `"yyyy-MM-dd HH:mm"`).
- v3: rewrites absolute file paths in `MedicalReport.FilePath`, `ReportPage.FilePath`, `DoctorVisit.PrescriptionImagePath` to relative stored paths.
- Rule: `CreateTableAsync` already appends new columns; when a change needs backfill or reshaping, bump `CurrentVersion` and add a step.

Core model entities (`Models/`; dates stored as strings, see `Helpers/DateTimeHelper.cs`):
- `Medicine`: name, `StockCount` (double) + `StockUnit`, `StartDate` (`"yyyy-MM-dd HH:mm"`), `EndDate`, `PrescribedDays`, `IsOngoing`, `IsActive`, `SortOrder`, `Instructions`, `Notes`, `CreatedAt`, and `ExtensionDays` (whole days added by auto-miss, capped at `PrescribedDays`; `EndDate` is always `StartDate + PrescribedDays - 1 + ExtensionDays`).
- `MedicineSchedule`: `ScheduleTime` (`"HH:mm"`), `IsActive`, nullable `CreatedAt` (`"yyyy-MM-dd HH:mm"`; slots earlier than this are never auto-logged as missed).
- `MedicineLog`: `ScheduleId`, `ScheduledFor`, `TakenAt`, `WasTaken`, `StockBefore/After`, `SkipReason`.
- `MedicineCourse`: start/end, `PrescribedDays`, `SchedulesPerDay`, `TotalTaken`, `TotalScheduled`, `Status` (Active/Completed/Stopped).
- `HealthMetric`: type, `Value`, optional `SecondValue` (diastolic), unit, `RecordedAt`, notes.
- `MedicalReport` (+ optional `DoctorVisitId`), `ReportPage` (one row per file, `PageNumber`), `DoctorVisit` (optional `FollowUpDate`, `PrescriptionImagePath`), `PendingMedicine` (scanner output awaiting approval), `TimelineEntry` (in-memory feed row).

`DateTimeHelper` formats: date `"yyyy-MM-dd"`, minute `"yyyy-MM-dd HH:mm"`, second `"yyyy-MM-ddTHH:mm:ss"`, time `"HH:mm"`; `TryParseStored` accepts every shape ever written (culture-invariant), `NormalizeStartDate` canonicalises to the minute form, `DatePart` returns the first 10 characters.

Repositories: `BaseRepository<T>` gives `GetAll/GetById/Insert/Update/Delete`; concrete repositories add typed queries (active/inactive medicines, schedules by medicine, logs by range and `GetLogForSlotAsync(scheduleId, day)`, active courses, latest/history metrics, ordered visits/reports, `ClearDoctorVisitAsync`, pages by report).

## 5) Business Rules and Validation Logic
Constants (`Constants/AppConstants.cs`, `MetricTypes.cs`, `CloudConstants.cs`): `LowStockDays = 5`, `CriticalStockDays = 2`, `ReportTypes = Lab/Prescription/Xray/Scan/Other`, PIN length 4-6, `MaxFailedPinAttempts = 5`, `PinCooldownSeconds = 30`, `BackgroundResumeThresholdMinutes = 2`, six metric types with units and accepted ranges (BP has separate systolic/diastolic ranges).

`Helpers/ValidationHelper.cs`: `IsMetricValueValid` (rejects NaN/Infinity, range per type, BP requires diastolic), `TryParseDouble` (accepts "72.5" and "72,5"), `IsValidFileType` (jpg/jpeg/png/pdf), `IsValidPin`.

File storage (`Helpers/FileStorageHelper.cs` + MAUI-free `StoredPathHelper.cs`):
- Base folder `AppData/HealthApp` with sub-folders `reports` (Lab), `prescriptions` (Prescription), `scans` (Xray/Scan), `other`.
- File names `"{type}_{date}_{8-char guid}.{ext}"` with user parts sanitised.
- Paths persisted in the DB are relative to the base folder with forward slashes: write with `ToStoredPath`, read with `ResolvePath` (relative values are rooted on the base folder; legacy absolute values are returned if they exist, otherwise re-rooted by the tail after `/HealthApp/`). This is what makes backup restore on another device/install resolve without rewriting.

Reachable-slot rule (`MedicineService.IsSlotReachable` / `GetReachableSlotsForDay`): a slot on a day counts only if the schedule is active, the slot is at/after the medicine's `StartDate` instant, the day is not after `EndDate`, and the slot is not before the medicine's `CreatedAt` nor the schedule row's `CreatedAt`. Used by the Today page, medicine list/detail progress, auto-miss and (mirrored in SQL) the widget. Do not re-implement.

## 6) Core Service Logic

### 6.1 MedicineService (`Services/MedicineService.cs`)
- `AddMedicine`: requires >= 1 schedule; normalises times and start date; computes `EndDate` (`ComputeEndDate`) for fixed courses; gives the new medicine the highest `SortOrder`; stamps `MedicineSchedule.CreatedAt`; creates the first Active course with `TotalScheduled = ComputeTotalScheduled` (slots over `PrescribedDays`, excluding first-day slots before start/creation; extension days never count); schedules reminders.
- `UpdateMedicine`: cancels reminders before touching schedule rows, recomputes `EndDate` only when the course definition changed (start instant, prescribed days, ongoing flag), clamps `ExtensionDays` to `[0, PrescribedDays]`, reconciles schedules with `SyncSchedules` (keeps row ids so logs stay linked: unchanged times keep rows, changed times are UPDATEd onto remaining rows, inserts/deletes only when the count changes, new rows get `CreatedAt = now`), updates the active course, re-arms reminders.
- `CheckAndCompleteCourses`: completes a course when `EndDate < today` or `TotalTaken >= TotalScheduled > 0`, then deactivates the medicine and cancels its reminders.
- `StartNewCourse`: closes the active course (end clamped to its start), resets `ExtensionDays`, makes the medicine active/fixed-length, starts "now" when the date is today, inserts a new course, schedules reminders.
- `DeactivateMedicine` cancels reminders; `DeleteMedicine` removes schedules, courses, logs and the medicine.
- `LogDoseTaken(medicineId, scheduleId, scheduledDate = today)` returns `MedicineLog?`: idempotent (returns null when a taken log already exists for that slot/day); flips an existing missed row to taken; decrements stock clamped at 0 (stock never blocks: `CanLogDose` only checks `IsActive`, `IsOutOfStock` is a soft warning); increments the active course's `TotalTaken`; calls `IReminderService.ShowRefillReminder` when stock crosses below `LowStockDays`.
- `AutoLogMissedDoses` (runs from `TodayViewModel.Load`): for each active medicine, walks from `max(start, created, yesterday - 29 days)` to yesterday, inserting a missed log for every reachable unlogged slot; for fixed courses it extends the course by one day per whole day's worth of missed doses (`ceil(missed / schedulesPerDay)`), with total `ExtensionDays` capped at `PrescribedDays`, moving `EndDate` as it iterates and mirroring the end onto the active course.
- `BackfillPastDoses(from, to)`: marks every past slot (skipping future slots and slots before start) as taken, flipping missed rows; adjusts stock and `TotalTaken`; returns the count.
- `GetAdherenceStats`: 7-day missed count and consecutive full-adherence day streak (30-day window). `GetStockDaysRemaining`, `IsLowStock`.

### 6.2 ReminderService (`Services/ReminderService.cs`, `IReminderService.cs`, `Notifications/DoseNotificationPayload.cs`)
Plugin.LocalNotification implementation. Notification ID bands (collision-free, documented in `IReminderService`):
- `schedule.Id` on-time dose (daily repeat); `1_000_000 + schedule.Id` 15-minute-early dose (daily repeat)
- `2_000_000 + visit.Id*2` follow-up evening before (18:00); `+1` follow-up morning of (08:00)
- `3_000_000 + medicine.Id` refill nudge (immediate); `4_000_000 + schedule.Id` snoozed dose (+15 min, one-shot); `5_000_000 + schedule.Id` silent "dose logged" confirmation (auto-dismisses after 8 s)

Channels: `dose_reminders` (High importance, vibration pattern, private lock-screen) and `appointments` (Default). Dose notifications use a Reminder category with two actions: `Take` (1001) and `Snooze 15 min` (1002), neither launches the app.

Initialisation: `AttachNotificationHandlers(services)` (static, idempotent under a lock) registers channels and the action category, subscribes `NotificationActionTapped`, and sets the `NotificationReceiving` pre-show hook. `Initialize()` forwards to it with the instance's provider.

Handlers:
- `Take`: parses the payload (`dose|{medicineId}|{scheduleId}|{yyyy-MM-dd}`), resolves the slot's day (`ResolveScheduledDate`: trust the embedded date only if it is today, else today, or yesterday for a late-evening slot tapped after midnight), calls `MedicineService.LogDoseTaken` (resolved lazily to avoid a DI cycle), clears the shade entries, re-arms from tomorrow, shows the silent confirmation, and `HealthWidget.RequestUpdate()`.
- `Snooze`: clears the shade and schedules the one-shot snooze notification.
- `NotificationReceiving` guard: because the plugin has no end date for repeats, a dose notification whose medicine is inactive/expired or whose schedule row is gone is suppressed and its repeat cancelled before it is shown.

Scheduling: `ScheduleReminders` / `RescheduleAll` arm early + on-time daily repeats per schedule, honouring `StartDate` (first fire not before it), `EndDate` (nothing after it), skipping slots already logged today, and bumping a slot in the past to tomorrow. `RescheduleAll` calls `CancelAll` first and also re-arms follow-ups for every visit with a future follow-up date. `CancelDoseReminders` cancels today's slot and re-arms it from tomorrow. `ScheduleFollowUpReminder(visitId)` cancels and re-arms (or just cancels when no future follow-up). `ShowRefillReminder` shows at most once per medicine per day (`Preferences["refill_shown_{id}"]`).

### 6.3 MetricsService (`Services/MetricsService.cs`)
Validated add/update (`ValidationHelper.IsMetricValueValid`, throws `ArgumentException`), delete, latest by type, history by type (limit), `GetAllLatest`.

### 6.3a Lab results (`Constants/LabCatalog.cs`, `Services/LabService.cs`, `Services/Labs/`)
All MAUI-free and unit-tested.
- **`LabCatalog`**: 13 panels (lipid, kidney, liver, blood sugar, CBC, thyroid, electrolytes, vitamins, heart and inflammation, hepatitis and infections, urine, other, plus "My tests" for custom ones) and about 100 `LabTestDefinition`s. A numeric test has a primary unit, optional alternative units with a factor to the primary (`mmol/L`, `µmol/L`, `/cmm`, ...), a general reference range and optional male/female ranges, and optional fixed decimals. A qualitative test has its `Options` (Negative/Positive, Non-reactive/Reactive, Nil/Trace/+...) and an `Expected` option, which is null when no option is "normal" (blood group, Anti-HBs, Dengue IgG, pregnancy test). `Match(printedName)` maps report spellings ("S. Creatinine", "ALT (SGPT)") through aliases and never guesses between tests ("Glucose" alone matches nothing). `UnitEquals` ignores case and the µ/u, ×/x, IU/L spellings.
- **`LabResult`** (table): one value. It stores the value **as entered** with its unit, and a snapshot of what it was judged against (`RefLow`/`RefHigh` in that unit, or `ExpectedText`). Rows saved together share a `BatchId`; `ReportId` links to the report the values were read from; `IsCalculated` marks derived values. Because the range is stored on the row, later catalog or profile changes never rewrite history.
- **`LabEvaluator`**: `Evaluate` gives `Normal/Low/High` (inclusive bounds), `Abnormal` for a qualitative result other than the expected word, `None` when there is nothing to judge against. Also formatting (`Format`, `RangeText`: "70 – 99", "up to 200", "40 or above"), unit conversion (`Convert`, null for an unknown unit) and `RangeIn` (catalog range for a sex, converted to the entry unit).
- **`LabCalculator.Derive`**: from the entered values converted to primary units: VLDL (TG ÷ 5) and LDL (Friedewald) only when TG is below 400 mg/dL, non-HDL, total/HDL ratio, indirect bilirubin, globulin, and eGFR (CKD-EPI 2021, needs adult age and sex from the profile). A value the user entered is never replaced by an estimate. Also `Bmi`.
- **`LabService.AddResults(entries, collectedOn, labName, reportId)`**: validates (number present, not negative, below 10,000,000, date not in the future, no duplicate test, qualitative word is one of the options, range not inverted), snapshots the range (the typed/printed one when given, else the catalog range for the profile's sex in the entry unit), appends the derived rows, and inserts everything in one transaction. Nothing is saved when any entry is invalid. `UpdateResult` re-validates and clears the calculated mark. Read models: `GetLatestPerTest` (newest + previous, catalog order), `GetAttention` (tests whose newest result is flagged), `GetBatches`, `GetHistory`, `GetCustomTests`, `GetPreferredUnits` (the unit last entered per test).
- **`LabFindingParser`**: turns report text into `LabDraft`s. `Parse(name, value, unit, range)` handles one tuple (used for Gemini's `keyFindings`); `ScanText(ocrText)` is the offline fallback that scans OCR lines for catalog tests only. Unusable values are dropped, never guessed.
- **`UserProfile`** (single row, Id 1): name, sex, date of birth, blood group, height, allergies, conditions, emergency contact. `UserProfileRepository.GetAsync` returns an empty profile until saved.

### 6.4 ReportService (`Services/ReportService.cs`)
- `SaveMultiPageReport(pages, type, title, ...)`: validates the type against `AppConstants.ReportTypes`, drops pages with unsupported file types, copies every page into the type sub-folder first, then inserts the `MedicalReport` and its `ReportPage` rows (numbered 1..n) in one `RunInTransactionAsync`. Any failure deletes the copied files, so no half-saved report remains. `SaveReport` is the single-page wrapper.
- Read methods (`GetAllReports`, `GetReportsByVisit`, `GetReportPages`) return models with `FilePath` already resolved to absolute paths; callers must not write those models back.
- `DeleteReport` deletes page files, page rows, the primary file and the report row, and clears `LabResult.ReportId` for values read from it (the values stay).

### 6.5 DoctorService (`Services/DoctorService.cs`)
- `AddVisit`: follow-up must be today or later; copies a picked prescription image into `prescriptions/` and stores the relative path; then `ScheduleFollowUpReminder`.
- `UpdateVisit`: imports a freshly picked image (absolute path outside the base folder), re-stores the path relative, updates, and re-syncs follow-up reminders (schedules or cancels).
- `DeleteVisit`: cancels follow-up reminders, deletes the image file, detaches linked reports (`ClearDoctorVisitAsync`), deletes the row.
- Read methods return visits with `PrescriptionImagePath` resolved.

### 6.6 TimelineService (`Services/TimelineService.cs`)
Merges visits (dated by visit date + creation time), reports, lab results (one entry per sitting, with the tests to check), vitals, dose logs (taken/missed), medicines added and completed courses into `TimelineEntry` rows, sorts descending by `SortKey`, then applies `offset`/`pageSize` in memory (correct cross-source paging on a small local DB).

### 6.7 OCR and regex parsers
- `PrescriptionParserService`: initialises the OCR plugin once (thread-safe), `RecognizeTextAsync(tryHard: true)`. `ParseMedicinesWithAiAsync(ocrText, imageBytes)` uses `PrescriptionAiParser` when a key exists (sending the image) and falls back to regex `ParseMedicines` otherwise; returns the raw `GeminiResult` too so the UI can explain failures. Regex heuristics handle list markers, form prefixes (Tab./Cap./Syp./Inj.), dose grids (`1-0-1`), OD/BD/TDS/QID/HS/SOS tokens, durations ("x 7 days", "2 weeks"), food timing, strengths; they derive `FrequencyPerDay`, default `Times` and `DurationDays`.
- `ReportTextParserService`: doctor, hospital, date (labelled dates first; strict day-first formats via `NormalizeDate`, rejecting years < 1900 or > next year), report type keywords, and a title from the first lines.
- `PdfTextExtractorService`: PdfPig text, capped pages (10) and characters (20k default), resolves relative paths, skips unreadable pages.

### 6.8 AI services (`Services/Ai/`, `AiParserService.cs`, `AiChatService.cs`)
- `GeminiApiKeyStore`: key in `SecureStorage["gemini_api_key"]`, with a one-time migration from the old plain `Preferences` slot and a `gemini_api_key_configured` Preferences flag so the synchronous `IsConfigured` works on cold start.
- `GeminiClient`: single static `HttpClient` (60 s), model from `Preferences["gemini_model"]` (default `gemini-3.8-flash`; saved 1.x/2.x choices are migrated to the default). `ListModelsAsync` queries `v1beta/models` with the user's key and returns text-capable models best-first (newest version, Flash > Pro > Lite, previews last), used by the Settings picker. If a call returns 404 / "not found" (model retired), the client switches to the best available model, saves it and retries once, key sent in the `x-goog-api-key` header (never in the URL), one retry after 2 s on 429/5xx/timeouts. `GenerateJsonAsync<T>` sends `systemInstruction`, inline images (base64, `inlineData`) before the prompt, `responseMimeType: application/json` + `responseSchema`, temperature 0.1, `thinkingBudget = 0` only for 2.5 Flash models; strips code fences and deserialises leniently. `GenerateTextAsync` is multi-turn (user/model roles, temperature 0.3). Failures are typed `GeminiResult<T>` with `GeminiErrorKind` (`NoApiKey`, `Network`, `Unauthorized` incl. 400 "API key not valid", `RateLimited`, `BadResponse` for server errors/blocked prompts/empty candidates/unparsable JSON, `Cancelled`). `GeminiImage.FromBytes` sniffs PNG vs JPEG.
- `GeminiSchema`: builder for the OpenAPI-subset schema (upper-case types, all properties required, optional ones nullable, `propertyOrdering`).
- `AiExtractionModels` + `AiFieldNormalizer`: typed results (`AiReportExtraction` with key findings, `AiDoctorVisitExtraction` with medicines, `AiPrescriptionExtraction`); post-processing forces dates to `yyyy-MM-dd`, times to `HH:mm`, enums to known lists, drops unknown medicines.
- `AiParserService`: `ExtractReportAsync` / `ExtractDoctorVisitAsync` (image + optional OCR text) plus legacy flat adapters returning null on failure; also the API-key facade (`IsConfigured`, `SaveApiKeyAsync`, `ClearApiKeyAsync`, `GetApiKeyAsync`).
- `PrescriptionAiParser`: prescription header + medicines schema shared with the visit extractor.
- `HealthContextBuilder`: read-only plain-text context for chat with labelled sections: today's doses, active medicines (schedule, stock, period, adherence, course progress), past medicines (15), 14-day dose history, latest metrics with trend, latest 10 visits, latest 5 reports with PDF text capped at 6,000 characters total.
- `AiChatService.AskTypedAsync`: requires a key, rebuilds the context on every question, puts it into the system instruction (rules: cite sections, no diagnosis/dose changes, emergency guidance, concise markdown), sends history as proper turns. `ToUserMessage` maps error kinds to chat sentences.

### 6.9 Backup, cloud sync and export
`BackupService` (`Services/BackupService.cs`):
- ZIP layout: `manifest.json` (`FormatVersion = 1`, app version, created UTC, device, `SchemaVersion` = `PRAGMA user_version`, file count), `db/healthapp.db`, `files/<path relative to base folder>`.
- `CreateBackupZipAsync`: snapshots the live DB with `VACUUM INTO` (fallback: WAL checkpoint + copy), stores attachments uncompressed, keeps the two most recent cache ZIPs, prunes abandoned staging/restore folders. `ShareBackupAsync` opens the share sheet.
- `RestoreFromZipAsync`: validates manifest (rejects newer format), checks the SQLite header, extracts to a temp folder with zip-slip guarding, then `DatabaseService.CloseAsync()` and `SwapIntoPlace`: deletes `-wal/-shm/-journal` sidecars, renames current DB and files folder to `*.bak`, moves the new ones in, rolls back on failure, deletes the `.bak` on success. Returns `RestoreResult`; never throws for expected failures. `RestoreFromPickedFileAsync` copies the picked content URI to cache first.
- After a restore the caller must `RescheduleAll()` and reload the UI (see 7.8).

Google Drive (`Services/Cloud/`, `Constants/CloudConstants.cs`):
- `GoogleAuthService`: OAuth 2.0 installed-app flow with PKCE via `WebAuthenticator` (no client secret; `access_type=offline&prompt=consent` to always get a refresh token; state check), tokens + account email in SecureStorage (email read from the id_token payload), `GetAccessTokenAsync` refreshes 2 minutes before expiry under a lock, `SignOutAsync` clears locally then revokes best-effort. `CloudConstants` holds separate debug and release client ids (an Android OAuth client is bound to one signing SHA-1) chosen with `#if DEBUG`; `IsConfigured` is false while the active configuration's id is the placeholder; `RedirectScheme` must be a compile-time constant (used in the callback activity's `IntentFilter`).
- `GoogleDriveSyncService`: exactly one `healthtracker-backup.zip` in the Drive `appDataFolder` space (scope `drive.appdata`), found by name and `modifiedTime desc` with duplicates deleted. `BackupNowAsync` builds a fresh ZIP and uploads it (resumable `Create` or `Update`, `appProperties` device/appVersion/createdUtc, progress 0..1), records `PrefLastBackupUtc` and the file id. `RestoreLatestAsync` downloads with progress and calls `BackupService.RestoreFromZipAsync`. `AutoSyncIfDueAsync` uploads only when configured, signed in, `AutoBackupEnabled` (default true), last backup older than 24 h, and online; a semaphore prevents concurrent backup/restore. `Humanize` maps API/network errors to user text.

`HealthReportService` + `Services/Pdf/` (SkiaSharp PDF):
- `GenerateAsync(includeReportFiles)` gathers data (active medicines with 30-day compliance/streak, past medicines, 3-month dose log, visits, metrics grouped by type, reports, optional report files), renders off the UI thread with `PdfWriter` (A4 flow layout over `SKDocument`: title, heading bars, key/value rows, zebra tables with repeated headers, aspect-fitted images, page footers) streaming to `FileSystem.CacheDirectory`, and warns when the file exceeds 50 MB.
- `PdfImageHelper`: decodes images at reduced size, applies EXIF orientation, downsizes to 1400 px long side, re-encodes JPEG q80; on Android renders every page of an embedded PDF via `PdfRenderer`.

### 6.10 ThemeService and BiometricService
- `ThemeService`: `AppThemeMode` (System/Light/Dark) in `Preferences["app_theme_mode"]`; `Apply` swaps `Resources/Themes/LightTheme.xaml` / `DarkTheme.xaml` (identical semantic keys: Background, Surface*, Text*, Outline*, Primary*, Accent*, Success/Warning/Danger/Info + containers, Metric* colours, nav/tab bar, brushes) in `MergedDictionaries`, sets `UserAppTheme` for platform chrome and raises `ThemeChanged` (used by `MetricHistoryPage` to rebuild chart paints). Everything binds colours with `DynamicResource`; `ChartTheme` bridges tokens to `SKColor`.
- `IBiometricService` (`Services/Security/`) with `BiometricResult` (Succeeded/Failed/Cancelled/NotAvailable/NotEnrolled). Android impl (`Platforms/Android/BiometricService.cs`) wraps `BiometricPrompt` with weak biometrics plus device credential on Android 11+, runs on the UI thread, maps error codes to results.

## 7) ViewModel Logic (UI Behavior)
All ViewModels derive from `BaseViewModel` (`IsBusy`/`IsNotBusy`, `HasError`/`ErrorMessage`, `SetError`/`ClearError`). Pages pass data by calling a page method before pushing (section 9).

### 7.1 Today
`TodayViewModel`: `Load` runs `AutoLogMissedDoses` then `CheckAndCompleteCourses`, builds `TodayScheduleItem`s for every reachable slot of every active medicine with today's log state and the out-of-stock flag, and groups them into `TodayDoseGroup`s (Due now / Later today / Done). Exposes `TakenDoses`, `TotalDoses`, `DayProgress`, `NextDoseText`, `Greeting`, `NeedsReloadForNewDay`. `RefreshWindows()` (page timer) re-evaluates time-based state and regroups. `TakeDose` calls `LogDoseTaken` (null result = already logged, no toast), `CancelDoseReminders`, updates the row, refreshes the widget and raises `DoseLogged`. `AddMedicineRequested` is the empty-state action.

`TodayScheduleItem` (linked into tests): due window 1 h before to 2 h after; `IsOverdue`, `CanActNow`, `GroupKind`, `StatusText/Icon/Color` (tokens), `TimeUntilText`; `CanTake = !IsDone` so a missed row can be flipped; `IsOutOfStock` is a warning only.

### 7.2 Medicines
`MedicineListViewModel`: three segments (Active / Past / Review). Active cards (`MedicineItem`) carry today's progress from reachable slots, schedule chips, active-course progress, 7-day adherence percent, stock days (low/critical thresholds -> Warning/Danger tokens), streak and missed counts; sorted by `SortOrder` desc with drag/drop reorder persisted via `UpdateSortOrder`. Past medicines show course/dose summary. Pending (`PendingMedicineItem`) rows can be dismissed or approved (`ApprovePendingRequested` -> form prefilled; `ApprovePending` deletes the pending row after save). Delete confirms with a history-aware message.

`MedicineFormViewModel` (single Add/Edit page `MedicineFormPage`): `Load(id)` for edit, `PrefillFromPending(pending)` to seed name/instructions/stock unit and a frequency preset from scanner output. Fields: name, prescribed days or Ongoing, stock + unit, start date and time, instructions, notes, schedule rows with presets (1-4x daily) and a 6-hour "next time" suggestion; `EndDateHint`/`StockHint`. `Save` validates, calls `AddMedicine` (offering `BackfillPastDoses` when the start date is in the past) or `UpdateMedicine`, refreshes the widget and raises `SavedSuccessfully`.

`MedicineDetailViewModel`: header status chip (Active / Low stock / Out of stock / Completed), key facts (stock and days of doses, days left or Ongoing, 30-day adherence), today's reachable slots (`TodayScheduleItem`s with Take), active-course progress, course history (`CourseDisplayItem`: Active/Completed/Incomplete), last 14 days of logs, and actions: take dose, start new course (prompt for days), backfill past doses (start date -> yesterday), stop (deactivate), delete. Raises `DoseLogged`, `ActionCompleted`, `Deleted`.

### 7.3 Metrics
`MetricsDashboardViewModel` (the Vitals tab): one `MetricSummaryItem` row per vital (latest value, relative "last recorded", change since the previous reading, a sparkline of the last 12 readings, and a status chip from `Classify`), a "last 7 days" strip of `WeekDayDot`s showing which days have readings, a BMI meter, and a soft non-diagnostic advisory for high/low BP or SpO2 < 94.

- `Classify(HealthMetric)` places the latest reading against general resting-adult ranges: blood pressure (High from 140/90, Elevated from 120/80, Low under 90/60), heart rate (60-100 normal), SpO2 (Low under 95, Very low under 90) and temperature (Raised above 37.2 °C, Fever from 38 °C). Weight and blood sugar get no chip: BMI covers weight, and the right sugar range depends on fasting.
- `BuildBmi()` combines the latest weight reading with the profile height (`LabCalculator.Bmi`), exposes the category, its status token and the healthy weight range for that height (BMI 18.5-24.9), or a prompt naming what is missing. `Controls/BmiGaugeView` draws the half-circle meter (bands at 18.5 / 25 / 30 on a 15-40 scale) and `Controls/SparklineView` the row trend lines; both resolve theme tokens at draw time.

`MetricFormViewModel` (single Add/Edit page `MetricFormPage`): `StartAdd(preselectedType)` or `LoadForEdit(metric)` (works on a copy; type locked in edit mode); type chips, value(s), date/time (not in the future), notes, range hint; `Save` calls `AddMetric`/`UpdateMetric` and raises `SavedSuccessfully`.

`MetricHistoryViewModel`: loads up to 1000 readings once, filters in memory by range (7/30/90/all), builds LiveCharts line series (systolic + diastolic for BP) with theme-resolved paints, stats (latest/min/max/average/trend), a readings list (`MetricReadingItem`) with delete, and `Rebuild()` on range or theme change.

### 7.3a Labs and profile
- `LabsViewModel` (Labs tab): loads once per appearance, groups the newest value of each test by panel, or every sitting by date (`ByDate`), with an "only results to check" filter. Deleting a sitting goes through `LabService.DeleteBatch`.
- `LabEntryViewModel`: one form for adding (a whole panel chip, searched single tests, custom tests, or values prefilled from a report) and for editing one saved result. Each `LabEntryRow` shows the live flag against its range; the unit picker converts the catalog range, and "Use the range printed on my report" overrides it. Empty rows are not saved. New rows default to the unit last used for that test.
- `LabTestDetailViewModel`: newest value with a range bar, LiveCharts trend in the newest result's unit with the reference band as a `RectangularSection`, lowest/highest, and the history list (tap to edit).
- `AddReportViewModel` collects `LabDraft`s while pages are read (AI key findings, or `ScanText` offline). After saving a report of type Lab it exposes `LabImport`; the page asks and opens `LabEntryPage.Prefill(...)` with the report linked.
- `ProfileViewModel` / `ProfileFormViewModel` (Me tab): identity, height/weight/BMI (weight from the latest vital), record counts, emergency card with a dial button, shortcuts.
- `TodayViewModel` additionally loads the greeting name, lab results to check and a follow-up due within 14 days; each part fails independently of the dose list.

### 7.4 Reports
`ReportsViewModel`: loads all reports with page counts, type filter chips and search (title/doctor/hospital/type), groups by month; `Delete` removes files and rows; `AddRequested` for FAB/empty state.

`AddReportViewModel`: queue of pages (file picker or camera with shutter muted on Android), re-numbered on removal; link to a doctor visit (`InitializeAsync` loads options; `Preset(visitId, doctor, hospital, type)` when opened from a visit). Extraction per image page: Gemini with the image when configured (`ExtractReportAsync`), applying title/doctor/hospital/type/date to empty fields only and building notes from summary + key findings; otherwise on-device OCR + `ParseReportFields` over the accumulated text. AI failures show a `ReportAiStatus` notice with an "Open Settings" action for missing/rejected keys. User-edited date/type are never overwritten. `Save` calls `SaveMultiPageReport`.

### 7.5 Doctor visits
`DoctorVisitsViewModel`: upcoming follow-ups section plus visits grouped by month (`DoctorVisitListItem` with follow-up countdown, prescription flag, diagnosis preview); delete reloads.

`DoctorVisitDetailViewModel`: visit fields, follow-up text, prescription image (replace via `UpdateVisit`), linked reports; `AttachReportRequested` pushes `AddReportPage` with `PresetForVisit`; `Delete`.

`DoctorVisitFormViewModel` (single Add/Edit page `DoctorVisitFormPage`): `Load(id)` for edit (scan card hidden until "Rescan"). Scanning a prescription (gallery/camera) sets the image and extracts: Gemini `ExtractDoctorVisitAsync` with the image when configured (fills empty fields, visit date <= today, follow-up >= today, lists `Medicines` that can be sent to Pending review), otherwise OCR + simple regex (doctor, hospital, first date). Follow-up "not in the past" is enforced on add, and on edit only when the follow-up changed. Raises `SavedSuccessfully`, `OpenSettingsRequested`, `ToastRequested`.

### 7.6 Prescription scan to Pending
`ScanPrescriptionViewModel`: picks/captures an image, OCRs it, then `ParseMedicinesWithAiAsync` (AI with image when configured, else regex); shows editable `ScannedMedicineItem`s (name, dosage, frequency, duration, times) and an AI status line; `SaveToPending` inserts selected rows as `PendingMedicine` (duration folded into frequency, form/instructions into `Form`); `ReviewRequested` opens the medicine list's Review segment.

### 7.7 History and Timeline
`HistoryViewModel`: category (Medicine / Doctor / Reports) over a date range with presets (7d/30d/90d/year). Medicine history groups courses overlapping the range with dose counts and progress; doctor and report items are filtered by date. One `Items` list, template chosen by `HistoryItemTemplateSelector`.

`TimelineViewModel`: pages of 20 from `TimelineService`, appended into month groups (continuing the last group across page boundaries), mapped to tokens/icons by event type; load-more and pull-to-refresh.

### 7.8 Security, Settings and Ask AI
`PinLockViewModel`: `InitializeAsync` reads the stored PIN once (dots sized to its length; a SecureStorage failure marks it unreadable and points to "Forgot PIN?") and resolves biometric availability (`Preferences["biometric_unlock"]` and `IBiometricService.IsAvailableAsync`). Digits auto-submit at full length; wrong PIN raises `WrongPinEntered`, after 5 failures a 30 s cooldown disables the keypad. `AutoPromptBiometricAsync` shows the prompt once per page. `EraseAllDataAndUnlockAsync` is the only reset path: cancel all reminders, `DeleteAllDataAsync`, delete the files folder, remove the PIN and biometric preference, then `UnlockSucceeded`. `PinLockPage` is modal, swallows the back button, pops itself on `UnlockSucceeded`, and double-confirms "Forgot PIN?".

`SettingsViewModel`: appearance (`ThemeService.SetMode`); PIN set/change/remove and biometric toggle (`CanUseBiometric` requires a PIN and device support); Google Drive card (configured/signed-in state, account email, sign in/out, backup now and restore with progress, auto-backup toggle, last-backup text from Drive metadata or local timestamp); local backup export (share sheet) and restore from a picked file; Gemini key (validated, masked, saved via `AiParserService.SaveApiKeyAsync`) and model picker (`GeminiClient.Model`); PDF health report with/without files (shared as `application/pdf`, large-file warning); delete all data with double confirmation (cancels reminders, empties tables, deletes files). After any successful restore, `AfterRestore` runs `RescheduleAll()` and raises `RestoreCompleted`; `SettingsPage` then replaces the window's page with a fresh `AppShell` so every ViewModel reloads from the restored database.

`AskAiViewModel`: chat over `AiChatService.AskTypedAsync` with cancellation, suggested questions, multi-turn history (errors and unanswered questions excluded), error rows typed by `GeminiErrorKind` (Retry for network, "Add Gemini key" shortcut for `NoApiKey`), `MessageAppended` for auto-scroll, `CancelPending` when the page disappears.

## 8) Android Widget Logic
Files: `Platforms/Android/HealthWidget.cs` (AppWidgetProvider, `widget_info.xml`: 4x2 cells, 30-minute update period), `WidgetDataHelper.cs`, `Resources/layout/widget_layout.xml`.
- Shows today's date, "{taken} of {total} doses taken today" with a progress bar, the next unlogged dose, latest heart rate and blood pressure; tapping opens the app.
- `WidgetDataHelper` opens `healthapp.db` read-only with raw SQL (no DI; it may run in a cold process) and mirrors the Today-page rule for medicines created today (slots before creation time excluded). Keep it in step with `GetReachableSlotsForDay` by hand.
- `HealthWidget.RequestUpdate()` never throws and is called after every dose/stock mutation (Today, detail, form, list delete, notification Take) under `#if ANDROID`, and from `MainActivity.OnResume`.

## 9) Routing and Navigation Logic
- `Helpers/NavigationHelper.PushAsync<TPage>(navigation, configure)` resolves the page from `App.Services`, runs the configure callback, pushes it, guards against double taps with an `Interlocked` flag, and catches exceptions into an alert. `PushModalAsync` likewise.
- Data is passed by page methods, not Shell query properties: `MedicineDetailPage.SetMedicineId`, `MedicineFormPage.LoadMedicine` / `PrefillFromPending`, `MedicineListPage.ShowSegment`, `MetricFormPage.SetPreselectedType` / `LoadMetric`, `MetricHistoryPage.LoadType`, `LabTestDetailPage.SetTest`, `LabEntryPage.PresetTest` / `Prefill` / `EditResult`, `AddReportPage.PresetForVisit`, `DoctorVisitDetailPage.LoadVisit` / `SetVisitId`, `DoctorVisitFormPage.LoadVisit`, `HistoryPage.SetCategory`.
- Stored files are opened/shared only via `NavigationHelper.OpenFileAsync` / `ShareFileAsync`, which resolve relative paths and show a friendly alert when the file is missing or no viewer exists.
- Five bottom tabs: Home (`today`), Labs (`labs`), Vitals (`metrics`), Records (top tabs `reports`, `doctors`, `timeline`) and Me (`me`); `NavigationHelper.GoToTabAsync(route)` switches between them. Medicines, dose history, Settings and Ask AI are pushed pages; the PIN lock is a modal page.

## 10) Test Coverage (Current)
`HealthTracker.Tests` is a plain `net9.0` xUnit project (xunit 2.9, NSubstitute, sqlite-net) that does not reference the MAUI app. It compiles MAUI-free app sources via `<Compile Include>` links: constants, all models, `DateTimeHelper`/`ValidationHelper`/`StoredPathHelper`/`StockUnitIcons`, `BaseViewModel`/`MedicineIcons`/`MedicineItem`/`TodayScheduleItem`, the five medicine repositories, `IReminderService`, `MedicineService`, `DatabaseMigrator`, and the lab stack (`LabCatalog`, `LabResult`, `UserProfile`, their repositories, `LabService`, `LabEvaluator`, `LabCalculator`, `LabFindingParser`). Two doubles shadow real types: `TestHelpers/TestDatabaseService.cs` (in-memory SQLite `DatabaseService` with `CloseAsync`; the default `:memory:` database is pooled and shared between tests, so tests that read whole tables pass a temp file path to the constructor) and `TestHelpers/NullReminderService.cs`.

319 tests across:
- `Helpers/DateTimeHelperTests` (formats, every stored shape, culture independence, `NormalizeStartDate`, round trips)
- `Helpers/StoredPathHelperTests` (relative conversion, legacy re-rooting, sanitising, round trip)
- `Helpers/ValidationHelperTests` (metric ranges incl. BP, `TryParseDouble`, file types, PIN)
- `Services/DatabaseMigratorTests` (legacy schema upgrade, idempotency, fresh DB stamping, recovery from a stale `_new` table)
- `Services/MedicineServiceTests` (add/update/delete, schedule id preservation, `TotalScheduled`, reachable slots, idempotent `LogDoseTaken` and missed-row flip, stock clamp, refill reminder once, course completion, new course, auto-miss lookback/extension cap/idempotency/creation boundaries, backfill, adherence stats)
- `Services/LabLogicTests` (catalog integrity, name and unit matching, flagging, formatting, unit conversion, Friedewald and CKD-EPI values, report text parsing) and `Services/LabServiceTests` (range snapshot by sex and unit, printed-range override, qualitative spelling, derived rows, validation, latest/attention/batches, update, delete, report detach)
- `ViewModels/BaseViewModelTests`, `MedicineItemTests` (tokens, stock thresholds, icons), `TodayScheduleItemTests` (state, grouping, overdue window, property-change notifications)

Adding a dependency to a linked file requires linking the new type and adding its table to the test `DatabaseService`; linked files must never reference `Microsoft.Maui.*`, `Preferences`, `FileSystem` or `Shell`.

## 11) Data Safety and Operational Notes
- Offline-first, single-user. All data is local SQLite plus files under the `HealthApp` folder; stored paths are relative so backups restore anywhere.
- Cloud backup is optional and goes only to the user's own Google Drive `appDataFolder` (hidden, app-scoped); there is no HealthTracker server. The OAuth client id ships as a placeholder; until replaced, the Settings card reports cloud backup as not configured.
- AI features require a user-supplied Gemini key (SecureStorage) and network; every AI path falls back to on-device OCR and regex parsing.
- App lock: PIN in SecureStorage, optional biometrics; the lock fails closed on keystore errors and the only reset is a full data wipe.
- Restore is atomic (rename-swap with rollback) and is followed by reminder rescheduling and a full UI reload.
- `AndroidManifest`: `allowBackup=false`, no cleartext traffic; permissions for notifications, exact alarms, camera, media images, biometrics, network state.

## 12) End-to-End Logic Summary
- Medicine lifecycle: add (schedules, first course, reminders) -> daily Today list from reachable slots -> take (idempotent log, stock clamp, refill nudge, reminder re-arm, widget) -> auto-miss with capped course extension -> course completion/deactivation -> optional new course.
- Reminder lifecycle: channels/actions attached at process start -> daily early/on-time repeats per schedule -> Take/Snooze handled in the background -> `RescheduleAll` on every launch and after restore -> pre-show guard suppresses stale repeats.
- Metrics lifecycle: validated insert/update -> dashboard tiles, trends and advisory -> history chart/stats per range.
- Report lifecycle: pick/capture pages -> AI (image) or OCR/regex extraction -> atomic multi-page save with relative paths -> list/search, open/share -> optional embedding in the SkiaSharp health PDF.
- Doctor lifecycle: visit with optional scanned prescription -> follow-up reminders (evening before, morning of) -> linked reports -> extracted medicines to Pending review -> approval into the medicine form.
- Backup lifecycle: `VACUUM INTO` snapshot + attachments + manifest ZIP -> share sheet or Drive resumable upload (24 h auto-sync) -> atomic restore -> reschedule + UI reload.
- Security lifecycle: PIN/biometric lock at startup and after 2 minutes in the background; fail-closed; wipe-only reset.
- Insight lifecycle: timeline/history aggregation and the Ask AI assistant grounded in `HealthContextBuilder` output.
