using System.Globalization;
using System.Text;
using HealthTracker.Constants;
using HealthTracker.Helpers;
using HealthTracker.Models;
using HealthTracker.Repositories;
using HealthTracker.Services.Labs;

namespace HealthTracker.Services.Ai;

/// <summary>
/// Turns the local database into the plain-text "what the assistant knows" block for
/// <see cref="Services.AiChatService"/>. Sections are labelled so the model can cite them:
/// ABOUT THE USER, TODAY, ACTIVE MEDICINES, PAST MEDICINES, DOSE HISTORY (14 days), HEALTH METRICS,
/// LAB RESULTS (newest value of each test against its reference range), DOCTOR VISITS,
/// MEDICAL REPORTS (with PDF text, capped at <see cref="MaxReportTextChars"/> in total).
/// Read-only: it never writes to the repositories.
/// </summary>
public class HealthContextBuilder
{
    public const int LogHistoryDays = 14;
    public const int MaxReportTextChars = 6_000;
    private const int MaxReports = 5;
    private const int MaxVisits = 10;

    private readonly MedicineRepository _medicineRepo;
    private readonly MedicineScheduleRepository _scheduleRepo;
    private readonly MedicineLogRepository _logRepo;
    private readonly MedicineCourseRepository _courseRepo;
    private readonly DoctorVisitRepository _doctorRepo;
    private readonly MedicalReportRepository _reportRepo;
    private readonly ReportPageRepository _reportPageRepo;
    private readonly HealthMetricRepository _metricRepo;
    private readonly MedicineService _medicineService;
    private readonly PdfTextExtractorService _pdfExtractor;
    private readonly LabResultRepository _labRepo;
    private readonly UserProfileRepository _profileRepo;

    public HealthContextBuilder(
        MedicineRepository medicineRepo,
        MedicineScheduleRepository scheduleRepo,
        MedicineLogRepository logRepo,
        MedicineCourseRepository courseRepo,
        DoctorVisitRepository doctorRepo,
        MedicalReportRepository reportRepo,
        ReportPageRepository reportPageRepo,
        HealthMetricRepository metricRepo,
        MedicineService medicineService,
        PdfTextExtractorService pdfExtractor,
        LabResultRepository labRepo,
        UserProfileRepository profileRepo)
    {
        _labRepo = labRepo;
        _profileRepo = profileRepo;
        _medicineRepo = medicineRepo;
        _scheduleRepo = scheduleRepo;
        _logRepo = logRepo;
        _courseRepo = courseRepo;
        _doctorRepo = doctorRepo;
        _reportRepo = reportRepo;
        _reportPageRepo = reportPageRepo;
        _metricRepo = metricRepo;
        _medicineService = medicineService;
        _pdfExtractor = pdfExtractor;
    }

    private async Task AppendProfileAsync(StringBuilder sb, DateTime today)
    {
        var p = await _profileRepo.GetAsync();
        if (p.IsEmpty) return;

        sb.AppendLine("ABOUT THE USER");
        if (!string.IsNullOrWhiteSpace(p.FullName)) sb.AppendLine($"- Name: {p.FullName}");
        if (p.AgeOn(today) is { } age) sb.AppendLine($"- Age: {age}");
        if (!string.IsNullOrWhiteSpace(p.Sex)) sb.AppendLine($"- Sex: {p.Sex}");
        if (!string.IsNullOrWhiteSpace(p.BloodGroup)) sb.AppendLine($"- Blood group: {p.BloodGroup}");
        if (p.HeightCm is { } h) sb.AppendLine($"- Height: {h.ToString("0.#", CultureInfo.InvariantCulture)} cm");
        if (!string.IsNullOrWhiteSpace(p.Allergies)) sb.AppendLine($"- Allergies: {p.Allergies}");
        if (!string.IsNullOrWhiteSpace(p.Conditions)) sb.AppendLine($"- Long-term conditions: {p.Conditions}");
        sb.AppendLine();
    }

    /// <summary>Newest value of every test, with the range it was judged against and the previous value.</summary>
    private async Task AppendLabsAsync(StringBuilder sb)
    {
        var latest = LabService.LatestPerTest(await _labRepo.GetAllOrderedAsync());
        if (latest.Count == 0) return;

        sb.AppendLine("LAB RESULTS (newest value of each test; the flag compares it with the reference range stored for that result)");
        foreach (var panel in latest.GroupBy(l => LabCatalog.Panel(l.Latest.PanelCode).Name))
        {
            sb.AppendLine($"{panel.Key}:");
            foreach (var l in panel)
            {
                var r = l.Latest;
                var line = new StringBuilder($"- {r.TestName}: {LabEvaluator.ValueWithUnit(r)} on {r.CollectedOn}");
                var reference = LabEvaluator.ReferenceText(r);
                if (reference.Length > 0) line.Append($" ({reference})");
                line.Append(l.Flag switch
                {
                    LabFlag.Normal => " [in range]",
                    LabFlag.Low => " [LOW]",
                    LabFlag.High => " [HIGH]",
                    LabFlag.Abnormal => " [NOT the expected result]",
                    _ => string.Empty
                });
                if (r.IsCalculated == 1) line.Append(" [calculated by the app]");
                if (l.Previous is { } prev) line.Append($"; previous {LabEvaluator.ValueWithUnit(prev)} on {prev.CollectedOn}");
                sb.AppendLine(line.ToString());
            }
        }
        sb.AppendLine();
    }

    public async Task<string> BuildAsync(CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        var today = DateTime.Today;
        sb.AppendLine($"Today's date: {DateTimeHelper.ToDateString(today)} ({today:dddd}); current time {DateTime.Now:HH:mm}.");
        sb.AppendLine();

        await AppendProfileAsync(sb, today);

        var activeMeds = await _medicineRepo.GetAllActiveAsync();
        var pastMeds = await _medicineRepo.GetAllInactiveAsync();
        ct.ThrowIfCancellationRequested();

        await AppendTodayAsync(sb, activeMeds, today);
        await AppendActiveMedicinesAsync(sb, activeMeds);
        await AppendPastMedicinesAsync(sb, pastMeds);
        await AppendDoseHistoryAsync(sb, activeMeds.Concat(pastMeds).ToList(), today);
        ct.ThrowIfCancellationRequested();
        await AppendMetricsAsync(sb);
        await AppendLabsAsync(sb);
        await AppendVisitsAsync(sb);
        ct.ThrowIfCancellationRequested();
        await AppendReportsAsync(sb);

        return sb.ToString().TrimEnd();
    }

    // ----- Today ---------------------------------------------------------------------------------

    private async Task AppendTodayAsync(StringBuilder sb, List<Medicine> activeMeds, DateTime today)
    {
        if (activeMeds.Count == 0) return;

        var from = DateTimeHelper.ToDateTimeString(today);
        var to = DateTimeHelper.ToDateTimeString(today.AddDays(1).AddSeconds(-1));
        var now = DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture);

        sb.AppendLine("=== TODAY'S DOSES ===");
        foreach (var med in activeMeds)
        {
            var schedules = (await _scheduleRepo.GetByMedicineIdAsync(med.Id)).OrderBy(s => s.ScheduleTime).ToList();
            if (schedules.Count == 0) continue;
            var logs = await _logRepo.GetLogsForMedicineAsync(med.Id, from, to);

            var parts = new List<string>();
            foreach (var s in schedules)
            {
                var log = logs.FirstOrDefault(l => l.ScheduleId == s.Id);
                var status = log == null
                    ? (string.CompareOrdinal(s.ScheduleTime, now) <= 0 ? "due/not logged" : "upcoming")
                    : log.WasTaken == 1 ? "taken" : $"missed{(log.SkipReason != null ? $" ({log.SkipReason})" : "")}";
                parts.Add($"{s.ScheduleTime} {status}");
            }
            sb.AppendLine($"- {med.Name}: {string.Join(", ", parts)}");
        }
        sb.AppendLine();
    }

    // ----- Medicines -----------------------------------------------------------------------------

    private async Task AppendActiveMedicinesAsync(StringBuilder sb, List<Medicine> activeMeds)
    {
        if (activeMeds.Count == 0)
        {
            sb.AppendLine("=== ACTIVE MEDICINES ===");
            sb.AppendLine("(none)");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("=== ACTIVE MEDICINES ===");
        foreach (var med in activeMeds)
        {
            var schedules = (await _scheduleRepo.GetByMedicineIdAsync(med.Id)).OrderBy(s => s.ScheduleTime).ToList();
            var (streak, missedThisWeek) = await _medicineService.GetAdherenceStats(med.Id);
            var course = await _courseRepo.GetActiveCourseAsync(med.Id);

            sb.AppendLine($"- {med.Name}");
            sb.AppendLine($"  Schedule: {(schedules.Count > 0 ? string.Join(", ", schedules.Select(s => s.ScheduleTime)) : "none")} ({schedules.Count}x daily)");
            sb.AppendLine($"  Stock: {med.StockCount:0.##} {med.StockUnit}" +
                          (schedules.Count > 0 && med.IsOngoing == 0 ? $" (~{Math.Floor(med.StockCount / schedules.Count)} days left)" : ""));
            sb.AppendLine($"  Period: {med.StartDate} to {(med.IsOngoing == 1 ? "ongoing" : med.EndDate ?? "not set")}" +
                          (med.PrescribedDays > 0 ? $", prescribed {med.PrescribedDays} days" : ""));
            if (!string.IsNullOrEmpty(med.Instructions)) sb.AppendLine($"  Instructions: {med.Instructions}");
            if (!string.IsNullOrEmpty(med.Notes)) sb.AppendLine($"  Notes: {med.Notes}");
            sb.AppendLine($"  Adherence: {streak}-day full-adherence streak, {missedThisWeek} missed in the last 7 days");
            if (course != null)
                sb.AppendLine($"  Course progress: {course.TotalTaken}/{(course.TotalScheduled > 0 ? course.TotalScheduled.ToString() : "ongoing")} doses, started {course.StartDate}");
        }
        sb.AppendLine();
    }

    private async Task AppendPastMedicinesAsync(StringBuilder sb, List<Medicine> pastMeds)
    {
        if (pastMeds.Count == 0) return;

        sb.AppendLine("=== PAST MEDICINES ===");
        foreach (var med in pastMeds.Take(15))
        {
            var courses = await _courseRepo.GetByMedicineIdAsync(med.Id);
            var totalTaken = courses.Sum(c => c.TotalTaken);
            sb.AppendLine($"- {med.Name}: {med.StartDate} to {med.EndDate ?? "unknown"}, {courses.Count} course(s), {totalTaken} doses taken" +
                          (!string.IsNullOrEmpty(med.Notes) ? $". Notes: {med.Notes}" : ""));
        }
        sb.AppendLine();
    }

    private async Task AppendDoseHistoryAsync(StringBuilder sb, List<Medicine> allMeds, DateTime today)
    {
        if (allMeds.Count == 0) return;

        var from = DateTimeHelper.ToDateTimeString(today.AddDays(-(LogHistoryDays - 1)));
        var to = DateTimeHelper.ToDateTimeString(today.AddDays(1).AddSeconds(-1));

        var lines = new List<string>();
        var totalTaken = 0;
        var totalMissed = 0;
        foreach (var med in allMeds)
        {
            var logs = await _logRepo.GetLogsForMedicineAsync(med.Id, from, to);
            if (logs.Count == 0) continue;
            var taken = logs.Count(l => l.WasTaken == 1);
            var missed = logs.Count - taken;
            totalTaken += taken;
            totalMissed += missed;

            var missedDays = logs.Where(l => l.WasTaken == 0)
                .Select(l => l.ScheduledFor.Length >= 10 ? l.ScheduledFor[..10] : l.ScheduledFor)
                .Distinct().OrderBy(d => d, StringComparer.Ordinal).ToList();
            lines.Add($"- {med.Name}: {taken} taken, {missed} missed" +
                      (missedDays.Count > 0 ? $" (missed on {string.Join(", ", missedDays.TakeLast(5))})" : ""));
        }
        if (lines.Count == 0) return;

        var total = totalTaken + totalMissed;
        sb.AppendLine($"=== DOSE HISTORY (last {LogHistoryDays} days) ===");
        sb.AppendLine($"Overall: {totalTaken}/{total} doses taken ({(total > 0 ? totalTaken * 100 / total : 0)}%)");
        foreach (var line in lines) sb.AppendLine(line);
        sb.AppendLine();
    }

    // ----- Metrics ------------------------------------------------------------------------------

    private async Task AppendMetricsAsync(StringBuilder sb)
    {
        var any = false;
        var section = new StringBuilder();

        foreach (var type in MetricTypes.All)
        {
            var history = await _metricRepo.GetHistoryByTypeAsync(type, 5);
            if (history.Count == 0) continue;
            any = true;

            var latest = history[0];
            var line = $"- {type}: {FormatMetric(latest)} on {latest.RecordedAt[..Math.Min(10, latest.RecordedAt.Length)]}";
            if (history.Count > 1)
            {
                var previous = history[1];
                line += $" (previous {FormatMetric(previous)} on {previous.RecordedAt[..Math.Min(10, previous.RecordedAt.Length)]}; {Trend(latest, previous, type)})";
            }
            if (!string.IsNullOrEmpty(latest.Notes)) line += $". Note: {latest.Notes}";
            section.AppendLine(line);

            if (history.Count > 2)
                section.AppendLine($"  Recent readings: {string.Join(", ", history.Select(FormatMetric))}");
        }

        if (!any) return;
        sb.AppendLine("=== HEALTH METRICS (latest per type) ===");
        sb.Append(section);
        sb.AppendLine();
    }

    private static string FormatMetric(HealthMetric m) => m.SecondValue.HasValue
        ? $"{m.Value:0.#}/{m.SecondValue.Value:0.#} {m.Unit}"
        : $"{m.Value:0.#} {m.Unit}";

    private static string Trend(HealthMetric latest, HealthMetric previous, string type)
    {
        var delta = latest.Value - previous.Value;
        if (type == MetricTypes.BloodPressure)
        {
            var dDelta = (latest.SecondValue ?? 0) - (previous.SecondValue ?? 0);
            if (Math.Abs(delta) < 1 && Math.Abs(dDelta) < 1) return "unchanged";
            return $"systolic {Signed(delta)}, diastolic {Signed(dDelta)}";
        }
        if (Math.Abs(delta) < 0.05) return "unchanged";
        return $"{(delta > 0 ? "up" : "down")} {Math.Abs(delta):0.#} {latest.Unit}";
    }

    private static string Signed(double d) => d >= 0 ? $"+{d:0.#}" : $"{d:0.#}";

    // ----- Visits & reports ---------------------------------------------------------------------

    private async Task AppendVisitsAsync(StringBuilder sb)
    {
        var visits = await _doctorRepo.GetAllOrderedAsync();
        if (visits.Count == 0) return;

        sb.AppendLine($"=== DOCTOR VISITS (latest {Math.Min(MaxVisits, visits.Count)} of {visits.Count}) ===");
        foreach (var v in visits.Take(MaxVisits))
        {
            sb.AppendLine($"- {v.VisitDate}: {v.DoctorName}" + (!string.IsNullOrEmpty(v.Hospital) ? $" at {v.Hospital}" : ""));
            if (!string.IsNullOrEmpty(v.Diagnosis)) sb.AppendLine($"  Diagnosis: {v.Diagnosis}");
            if (!string.IsNullOrEmpty(v.Notes)) sb.AppendLine($"  Notes: {v.Notes}");
            if (!string.IsNullOrEmpty(v.FollowUpDate)) sb.AppendLine($"  Follow-up: {v.FollowUpDate}");
        }
        sb.AppendLine();
    }

    private async Task AppendReportsAsync(StringBuilder sb)
    {
        var reports = await _reportRepo.GetAllOrderedAsync();
        if (reports.Count == 0) return;

        sb.AppendLine($"=== MEDICAL REPORTS (latest {Math.Min(MaxReports, reports.Count)} of {reports.Count}) ===");
        var remainingChars = MaxReportTextChars;

        foreach (var r in reports.Take(MaxReports))
        {
            sb.AppendLine($"- {r.ReportDate}: {r.Title} [{r.ReportType}]" +
                          (!string.IsNullOrEmpty(r.DoctorName) ? $" by {r.DoctorName}" : "") +
                          (!string.IsNullOrEmpty(r.Hospital) ? $" at {r.Hospital}" : ""));
            if (!string.IsNullOrEmpty(r.Notes)) sb.AppendLine($"  Notes: {r.Notes}");

            if (remainingChars <= 0) continue;

            // Text only from PDFs (images would need OCR, which is too slow for every chat turn).
            var text = ExtractPdfText(r, await _reportPageRepo.GetByReportIdAsync(r.Id), remainingChars);
            if (string.IsNullOrWhiteSpace(text)) continue;

            remainingChars -= text.Length;
            sb.AppendLine("  Extracted text:");
            sb.AppendLine("  " + text.Replace("\n", "\n  "));
        }
        sb.AppendLine();
    }

    /// <summary>Concatenates PDF text from the report's pages (or its single file), capped at <paramref name="maxChars"/>.</summary>
    private string ExtractPdfText(MedicalReport report, List<ReportPage> pages, int maxChars)
    {
        List<string> sources;
        if (pages.Count > 0)
            sources = pages.OrderBy(p => p.PageNumber).Where(p => IsPdf(p.FileType)).Select(p => p.FilePath).ToList();
        else if (IsPdf(report.FileType))
            sources = [report.FilePath];
        else
            return string.Empty;
        if (sources.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        foreach (var path in sources)
        {
            if (sb.Length >= maxChars) break;
            var text = _pdfExtractor.ExtractText(path, maxPages: 10, maxChars: maxChars - sb.Length);
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (sb.Length > 0) sb.AppendLine();
            sb.Append(text);
        }

        var result = CollapseWhitespace(sb.ToString());
        return result.Length > maxChars ? result[..maxChars] + "..." : result;
    }

    private static bool IsPdf(string? fileType) => string.Equals(fileType, "pdf", StringComparison.OrdinalIgnoreCase);

    private static string CollapseWhitespace(string s)
        => System.Text.RegularExpressions.Regex.Replace(s, @"[ \t]{2,}", " ").Trim();
}
