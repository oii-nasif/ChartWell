# Chartwell design system

How every page in `HealthTracker/Views` is built. Read this before writing or changing XAML.

## Brand

- **Name**: Chartwell. The display name lives in `ApplicationTitle` (csproj) and in user-facing strings; the
  namespace, folders and `ApplicationId` (`com.healthapp.healthtracker`) are unchanged on purpose: the
  Android OAuth client and existing installs are bound to that id.
- **Mark**: `Resources/appiconfg.svg`, a white open "C" holding a chart line that climbs out through the
  opening and ends in a mint dot, on the teal gradient of `Resources/appicon.svg`. Keep the mark inside the centre 66% of the
  456 canvas (adaptive-icon safe zone).
- **Colour**: deep teal-green `Primary` on a warm sage-white canvas. A brighter cyan-teal (`Accent*`) marks the
  assistant and AI-read content. Status colours keep their meaning everywhere: `Success` in range / taken, `Warning` out of
  range / overdue, `Danger` unexpected qualitative result / missed / destructive.
- **Type**: Plus Jakarta Sans, registered as `AppRegular`, `AppMedium`, `AppSemibold`, `AppBold`,
  `AppExtraBold`.
- **Shape**: cards 20, hero 24, inputs and icon tiles 14, pill buttons, FAB 18.

## Tokens and theming

- Colours live in `Resources/Themes/LightTheme.xaml` and `DarkTheme.xaml` (identical keys). `ThemeService` swaps the dictionary at runtime, so **always use `{DynamicResource Key}` for colours**, never `StaticResource` and never a hex literal in a page.
- Semantic keys: `Background`, `Surface`, `SurfaceVariant`, `SurfaceElevated`, `TextPrimary`, `TextSecondary`, `TextTertiary`, `TextDisabled`, `TextInverse`, `Outline`, `OutlineStrong`, `Primary`, `PrimaryPressed`, `OnPrimary`, `PrimaryContainer`, `OnPrimaryContainer`, `Accent*` (AI features), `Success/Warning/Danger/Info` each with `*Container` and `On*Container`, `MetricWeight`, `MetricBloodPressure`, `MetricHeartRate`, `MetricBloodSugar`, `MetricSpO2`, `MetricTemperature`, one `Lab*` accent per lab panel (`LabLipid`, `LabKidney`, `LabLiver`, `LabBlood`, `LabDiabetes`, `LabThyroid`, `LabElectrolyte`, `LabVitamin`, `LabInfection`, `LabUrine`, `LabCardiac`, `LabOther`), and the hero set: `HeroBrush` / `AccentHeroBrush` (gradients), `OnHero`, `OnHeroMuted`, `HeroChipFill`.
- **A token key must never equal a style key.** Theme dictionaries are merged after `Styles.xaml`, so a colour named like a style silently replaces it and the page crashes at runtime with "Cannot assign property Style" (the build does not catch it). That is why the hero pill's colour is `HeroChipFill` while the style is `HeroChip`.
- ViewModels never expose hex colours. They expose a **token name** (`"Success"`, `"Warning"`, `"Danger"`, `"Primary"`, `"MetricHeartRate"`, `"TextSecondary"`) and the page binds with `Converter={StaticResource ThemeColorConverter}`. For a status chip pass `ConverterParameter=Container` (background, `{Token}Container`) and `ConverterParameter=OnContainer` (text, `On{Token}Container`); tokens without a pair fall back to the neutral chip colours.
- Styles are in `Resources/Styles.xaml`; styles are referenced with `StaticResource` (the style itself is static, its colour setters are dynamic).

## Typography (keyed Label styles)

`DisplayText` 32 · `TitleLarge` 24 · `TitleMedium` 18 · `TitleSmall` 16 · `BodyLarge` 16 · `Body` 14 · `BodyMedium` · `BodyStrong` · `BodySecondary` · `Caption` 12 · `CaptionStrong` · `Overline` 11 uppercase · `SectionHeader` 16 bold, sentence case · `StatValue` 26 · `StatUnit` · `ErrorText` · `SuccessText` · `LinkText` · `FieldLabel` · `FieldHint`. On a hero card use `HeroOverline`, `HeroTitle`, `HeroBody`, `HeroChipText`.
Fonts: `AppRegular`, `AppMedium`, `AppSemibold`, `AppBold`, `AppExtraBold`. Minimum text size 12. Never use `FontAttributes="Bold"`; use a Semibold/Bold style.

## Containers (Border styles)

`Card` (surface + 1px outline, radius 20, padding 16, bottom margin 12) · `CardFlat` · `CardHero` (teal gradient; text and icons inside use `OnHero` / `OnHeroMuted`, which are white in both themes, never `OnPrimary`) · `CardHeroAccent` (cyan-teal gradient, assistant) · `HeroChip` (translucent pill on a hero) · `ActionTile` (quick-action tile) · `CardInfo/Success/Warning/Danger/Accent` · `ErrorBanner` · `InputBorder` / `InputBorderMultiline` (filled) · `Chip*` with `ChipText*` · `IconCircle` / `IconCircleLarge` (rounded-square icon tiles) · `Divider` (BoxView) · `SegmentTrack` with `SegmentButton` / `SegmentButtonSelected` (segmented control; swap the style with a DataTrigger on a ViewModel bool).
**Never use `Frame`** (deprecated); always `Border`.

## Buttons

`PrimaryButton` (implicit default), `SecondaryButton` (tonal), `OutlineButton`, `TextButton`, `DangerButton`, `DangerOutlineButton`, `SuccessButton`, `AccentButton`, `Small*` variants (40 tall), `Fab` (ImageButton 56, place as the last child of the page's root Grid with `Grid.Row` spanning so it floats bottom-right), `IconButton` (ImageButton 44x44).
Give every icon-only button `SemanticProperties.Description`.

## Icons

Material icons are in `Resources/Images/ic_*.svg` and referenced as `ic_name.png` (MAUI converts SVG). They are black; tint them with the app's own controls (NOT CommunityToolkit `IconTintColorBehavior`: a Behavior cannot resolve DynamicResource and it crashes on `Button`):

```xml
xmlns:controls="clr-namespace:HealthTracker.Controls"
<controls:TintedImage Source="ic_alarm.png" WidthRequest="24" HeightRequest="24" TintColor="{DynamicResource Primary}"/>
<controls:TintedImageButton Source="ic_add.png" Style="{StaticResource Fab}" TintColor="{DynamicResource OnPrimary}"
                            SemanticProperties.Description="Add medicine"/>
```

`Button.ImageSource` cannot be tinted; keep Buttons text-only (or put a TintedImage next to a Label inside a tappable Border).

Icons are Material Symbols Rounded (filled). Lab and profile additions: science, labs, biotech, experiment, lab_panel, lab_research, cardiology, nephrology, gastroenterology, hematology, endocrinology, glucose, nutrition, ecg_heart, microbiology, urology, immunology, coronavirus, stethoscope, pill, home, account_circle, folder_shared, emergency, contact_emergency, call, allergy, cake, straighten, height, weight, calculate, tune, expand_more, swap_vert, arrow_upward, arrow_downward, add_circle, edit_note, summarize, star.
Also available: today, monitor_heart, description, medical_services, timeline, settings, auto_awesome (AI features; ToolbarIconAskAi in the toolbar is an accent-coloured variant), add, medication, alarm, check_circle, delete, edit, share, visibility, camera_alt, attach_file, lock, fingerprint, cloud_upload, cloud_done, cloud_off, history, search, warning, info, chevron_right, close, drag_handle, inventory_2, event, local_hospital, picture_as_pdf, image, send, refresh, more_vert, notifications, download, restore, logout, login, dark_mode, trending_up, trending_down, trending_flat, schedule, check, done_all, snooze, bloodtype, scale, thermostat, water_drop, air, bedtime, favorite, vaccines, arrow_forward, calendar_month, filter_list, sync, backup, folder_open, photo_library, document_scanner, psychology, insights, person, task_alt, cancel, error, help, key, shield, pending_actions, radio_button_unchecked.
No emoji in UI text (status, buttons, titles). Use icons.

## Shared controls (`Controls/`)

- `controls:EmptyStateView Icon="ic_medication.png" Title="..." Message="..." ActionText="..." ActionCommand="{Binding ...}"` — use as `CollectionView.EmptyView` and for empty sections.
- `controls:SectionHeaderView Title="Active medicines" ActionText="See all" ActionCommand="..."`.
- `xmlns:controls="clr-namespace:HealthTracker.Controls"`.

## Page anatomy

- Root is a `Grid`. Lists: `RefreshView` → `CollectionView` with `ItemsSource`, `EmptyView` (EmptyStateView), header/footer templates for anything above/below the list. **Never nest a CollectionView inside a ScrollView**; use `CollectionView.Header`/`Footer` or `IsGrouped`.
- Forms: `ScrollView` → `VerticalStackLayout Spacing="16" Padding="{StaticResource PagePadding}"`, fields grouped in `Card`s with `FieldLabel` above each `InputBorder`. Primary action button pinned at the bottom in a separate grid row (`Grid RowDefinitions="*,Auto"`), with `IsEnabled="{Binding IsBusy, Converter={StaticResource InverseBoolConverter}}"`.
- Busy: an `ActivityIndicator Style="{StaticResource LoadingIndicator}" IsVisible="{Binding IsBusy}"` overlay or inline. OCR/AI/backup show a labelled progress row (indicator + Caption text).
- Errors: `Border Style="{StaticResource ErrorBanner}" IsVisible="{Binding HasError}"` with a `Label Style="{StaticResource ErrorText}" Text="{Binding ErrorMessage}"` near the top of the content.
- Success feedback: CommunityToolkit `Toast.Make("Dose logged").Show()` from the page code-behind (not from the ViewModel).
- Padding: `{StaticResource PagePadding}` for forms, `{StaticResource ListPadding}` for lists that have a FAB (leaves room under the last item).
- Navigation: `Helpers.NavigationHelper.PushAsync<SomePage>(Navigation, p => p.SetId(id))` — never `Navigation.PushAsync(App.Services.GetService<...>())` directly, and never an un-guarded async void handler. Open/share stored files with `NavigationHelper.OpenFileAsync(storedPath)` / `ShareFileAsync` (they resolve relative paths).
- Accessibility: `SemanticProperties.Description` on icon buttons and images that convey meaning; `SemanticProperties.HeadingLevel="Level1"` on page titles inside hero cards; touch targets ≥ 44.
- Dates shown to users: `dd MMM yyyy` / `ddd, dd MMM`; times `HH:mm`.
- Charts: LiveCharts2 (`xmlns:lvc="clr-namespace:LiveChartsCore.SkiaSharpView.Maui;assembly=LiveChartsCore.SkiaSharpView.Maui"`, `lvc:CartesianChart`). Series colours come from the Metric* tokens, read in the ViewModel through `ChartTheme` helpers (see `Helpers/ChartTheme.cs`).

## Navigation map

Five bottom tabs (`AppShell.xaml`): **Home** (`today`), **Labs** (`labs`), **Vitals** (`metrics`),
**Records** (top tabs: `reports`, `doctors`, `timeline`), **Me** (`me`). Switch tabs from code with
`NavigationHelper.GoToTabAsync("labs")`. Medicines, dose history, Ask AI and Settings are pushed pages
reached from Home, Me and the global toolbar.

## Lab results UI

- A result row shows the value in `ValueToken` colour (primary text when fine, the flag colour when it
  needs a look) and a status chip built from `FlagToken` with the `Container` / `OnContainer` converter
  parameters. Numbers read "In range / Low / High"; word results read "As expected / Check".
- Never colour a result that has nothing to be judged against (no range, no expected word): no chip.
- Every screen that shows ranges carries the line that they are typical adult values and not a diagnosis.
