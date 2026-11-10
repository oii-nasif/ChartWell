# Third-party notices

Chartwell includes or depends on the following third-party work. Each remains under its own licence.

## Assets included in this repository

| Asset | Location | Licence |
| --- | --- | --- |
| Plus Jakarta Sans, © 2020 The Plus Jakarta Sans Project Authors | `HealthTracker/Resources/Fonts/PlusJakartaSans-*.ttf` | SIL Open Font License 1.1, full text in [`licenses/OFL-PlusJakartaSans.txt`](licenses/OFL-PlusJakartaSans.txt) |
| Material Symbols and Material Icons, © Google LLC | `HealthTracker/Resources/Images/ic_*.svg` (some recoloured or combined) | [Apache License 2.0](https://www.apache.org/licenses/LICENSE-2.0) |

## NuGet packages (restored at build time, not stored in this repository)

| Package | Licence |
| --- | --- |
| Microsoft.Maui.Controls, Microsoft.Maui.Controls.Compatibility | MIT |
| CommunityToolkit.Maui | MIT |
| sqlite-net-pcl | MIT |
| SQLitePCLRaw.bundle_green | Apache-2.0 |
| LiveChartsCore.SkiaSharpView.Maui | MIT |
| SkiaSharp | MIT |
| Plugin.LocalNotification | MIT |
| Plugin.Maui.OCR | MIT |
| UglyToad.PdfPig | Apache-2.0 |
| Google.Apis.Drive.v3 | Apache-2.0 |
| Xamarin.AndroidX.Biometric | MIT (binding), Apache-2.0 (AndroidX) |
| xunit, NSubstitute, coverlet.collector, Microsoft.NET.Test.Sdk (tests only) | Apache-2.0, BSD-3-Clause, MIT, MIT |

Licences are listed as published on nuget.org at the time of writing; check each package page for
the authoritative text.

## Services

Optional features call Google's Gemini API and Google Drive API under the user's own credentials.
Use of those services is subject to Google's terms.
