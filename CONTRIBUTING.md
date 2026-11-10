# Contributing to Chartwell

Thanks for taking a look. This is a small personal project, so the process is light. Contributions
are accepted under the project licence, GPL v3 or later (see [`LICENSE`](LICENSE)).

## Before you start

- Read [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) for the architecture and its rules. It is short and is the source of truth.
- For business logic, read the matching section of [`PROJECT_LOGIC_DOCUMENTATION.md`](PROJECT_LOGIC_DOCUMENTATION.md).
- For anything on screen, read [`docs/DESIGN_SYSTEM.md`](docs/DESIGN_SYSTEM.md).
- For a larger change, open an issue first so we can agree on the approach.

## Setup

See "Requirements" and "Build and run" in the [README](README.md). The solution and namespaces are
named `HealthTracker`; do not rename them or the `ApplicationId`.

## Making a change

1. Keep business rules in services, not ViewModels. Dose, stock and course rules live in
   `MedicineService`; lab results are saved only through `LabService.AddResults`.
2. Files linked into the test project must not reference `Microsoft.Maui.*`, `Preferences`,
   `FileSystem` or `Shell`. If you add a dependency to a linked file, link it in
   `HealthTracker.Tests.csproj` too.
3. When you add a model, register it in `DatabaseService.GetConnectionAsync`, in
   `DeleteAllDataAsync` and in the test `DatabaseService`. When you change a column in a way
   `CreateTableAsync` cannot handle, add a `DatabaseMigrator` step.
4. UI: colours come from theme tokens through `DynamicResource`, never hex in a page. No `Frame`, no
   emoji, icons are Material SVGs. A theme token key must never equal a style key.
5. Adding a lab test means adding one definition to `LabCatalog.Build()` with a unique code, aliases
   and a typical adult range. Cite a source for the range in the pull request.

## Checking a change

```powershell
dotnet test HealthTracker.Tests/HealthTracker.Tests.csproj
dotnet build HealthTracker/HealthTracker.csproj -f net9.0-android "-p:AndroidSdkDirectory=<path to your Android SDK>"
```

- The build must finish with 0 errors. `XC0022` hints are informational.
- XAML and resource mistakes (a missing key, a token and style sharing a key) only fail at runtime,
  so launch the app and open the pages you touched, in both light and dark theme.
- Add or update unit tests for logic changes.

## Never commit

- API keys, OAuth client ids, keystores or signing passwords. `CloudConstants.cs` must keep its
  placeholders in the repository.
- Real health data: databases, backups, report scans, or screenshots showing someone's records.

## Reporting a security problem

Please do not open a public issue for a vulnerability. Use GitHub's "Report a vulnerability" option
on the repository's Security tab.
