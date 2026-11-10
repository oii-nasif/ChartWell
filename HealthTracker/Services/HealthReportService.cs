using System.Globalization;
using HealthTracker.Constants;
using HealthTracker.Services.Labs;
using HealthTracker.Helpers;
using HealthTracker.Models;
using HealthTracker.Repositories;
using HealthTracker.Services.Pdf;
using SkiaSharp;

namespace HealthTracker.Services;

public class HealthReportService
{
    private const long WarningSizeBytes = 50 * 1024 * 1024; // 50 MB

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

    public HealthReportService(
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

    /// <summary>Returns (filePath, warningMessage). warningMessage is null if size is OK.</summary>
    public async Task<(string FilePath, string? Warning)> GenerateAsync(bool includeReportFiles)
    {
        var data = await GatherDataAsync(includeReportFiles);
        var filePath = Path.Combine(FileSystem.CacheDirectory,
            $"HealthReport_{DateTime.Now:yyyyMMdd_HHmmss}.pdf");

        // Rendering (and image decoding) is CPU-bound; keep it off the UI thread and stream to disk.
        await Task.Run(() => RenderToFile(data, filePath));

        var fileInfo = new FileInfo(filePath);
        string? warning = fileInfo.Length > WarningSizeBytes
            ? $"Report file is large ({fileInfo.Length / 1024 / 1024} MB). Consider sharing via a file manager or cloud storage."
            : null;

        return (filePath, warning);
    }

    // ─── Rendering ───────────────────────────────────────────────────────────

    private static void RenderToFile(ReportData data, string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024);
        using var pdf = new PdfWriter(stream, "Personal Health Report", data.GeneratedAt);

        pdf.NewPage();
        pdf.Title("Personal Health Report", $"Generated: {data.GeneratedAt:MMMM d, yyyy  h:mm tt}");

        // Sections are numbered as they are written, so an empty (skipped) section leaves no gap.
        var section = 0;
        string H(string title) => $"{++section}. {title}";

        if (!data.Profile.IsEmpty)
        {
            pdf.Heading(H("Profile"));
            WriteProfile(pdf, data.Profile, data.GeneratedAt);
        }

        if (data.ActiveMedicines.Count > 0)
        {
            pdf.Heading(H("Active Medicines"));
            foreach (var m in data.ActiveMedicines)
                WriteMedicineCard(pdf, m);
        }

        if (data.PastMedicines.Count > 0)
        {
            pdf.Heading(H("Past Medicines"));
            WritePastMedicinesTable(pdf, data.PastMedicines);
        }

        if (data.DoseLogs.Count > 0)
        {
            pdf.Heading(H("Medicine Dose Log (Last 3 Months)"));
            WriteDoseLogTable(pdf, data.DoseLogs);
        }

        if (data.DoctorVisits.Count > 0)
        {
            pdf.Heading(H("Doctor Visits"));
            foreach (var v in data.DoctorVisits)
                WriteDoctorVisitCard(pdf, v);
        }

        if (data.MetricGroups.Count > 0)
        {
            pdf.Heading(H("Health Metrics"));
            foreach (var (type, metrics) in data.MetricGroups)
                WriteMetricsTable(pdf, type, metrics);
        }

        if (data.LabResults.Count > 0)
        {
            pdf.Heading(H("Lab Results"));
            WriteLabResults(pdf, data.LabResults);
        }

        if (data.MedicalReports.Count > 0)
        {
            pdf.Heading(H("Medical Reports"));
            WriteReportsTable(pdf, data.MedicalReports);
        }

        if (data.ReportFiles.Count > 0)
        {
            // Embedded pages are usually close to a full page tall; start the section on a fresh page
            // rather than leaving its heading stranded at the bottom of the previous one.
            if (pdf.RemainingHeight < pdf.ContentHeight * 0.6f) pdf.NewPage();
            pdf.Heading(H("Report Files"));
            foreach (var file in data.ReportFiles)
                WriteReportFile(pdf, file);
        }

        pdf.Finish();
    }

    private static void WriteMedicineCard(PdfWriter pdf, ActiveMedicineData m)
    {
        pdf.EnsureSpace(90); // keep the name together with the first few rows
        pdf.TextRow(m.Name, m.StockUnit, fontSize: 12);
        pdf.KeyValue("Start Date", m.StartDate);
        pdf.KeyValue("End Date", m.EndDate ?? "Ongoing");
        pdf.KeyValue("Schedule", m.ScheduleTimes);
        pdf.KeyValue("Stock", $"{m.Stock} {m.StockUnit}(s) remaining");
        pdf.KeyValue("Course Progress", m.CourseProgress);
        pdf.KeyValue("Adherence Streak", $"{m.Streak} day(s)");
        pdf.KeyValue("Missed This Week", m.MissedThisWeek.ToString());
        pdf.KeyValue("Compliance (30d)", $"{m.Compliance30d:P0}");
        if (!string.IsNullOrEmpty(m.Instructions)) pdf.KeyValue("Instructions", m.Instructions);
        if (!string.IsNullOrEmpty(m.Notes)) pdf.KeyValue("Notes", m.Notes);
        pdf.Space(4);
        pdf.Rule();
        pdf.Space(8);
    }

    private static void WriteProfile(PdfWriter pdf, UserProfile p, DateTime asOf)
    {
        if (!string.IsNullOrWhiteSpace(p.FullName)) pdf.KeyValue("Name", p.FullName!);
        if (p.AgeOn(asOf) is { } age) pdf.KeyValue("Age", $"{age} (born {p.DateOfBirth})");
        if (!string.IsNullOrWhiteSpace(p.Sex)) pdf.KeyValue("Sex", p.Sex!);
        if (!string.IsNullOrWhiteSpace(p.BloodGroup)) pdf.KeyValue("Blood group", p.BloodGroup!);
        if (p.HeightCm is { } h) pdf.KeyValue("Height", $"{h.ToString("0.#", CultureInfo.InvariantCulture)} cm");
        if (!string.IsNullOrWhiteSpace(p.Allergies)) pdf.KeyValue("Allergies", p.Allergies!);
        if (!string.IsNullOrWhiteSpace(p.Conditions)) pdf.KeyValue("Conditions", p.Conditions!);
        var contact = string.Join(" · ", new[] { p.EmergencyContactName, p.EmergencyContactPhone }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (contact.Length > 0) pdf.KeyValue("Emergency contact", contact);
        pdf.Space(8);
    }

    /// <summary>Newest value of every test by panel, then the full history of tests measured more than once.</summary>
    private static void WriteLabResults(PdfWriter pdf, List<LabResult> newestFirst)
    {
        static PdfTableCell Status(LabResult r)
        {
            var flag = LabEvaluator.Evaluate(r);
            return flag switch
            {
                LabFlag.Normal => new PdfTableCell(r.IsQualitative == 1 ? "As expected" : "In range", Color: PdfWriter.Success),
                LabFlag.Low => new PdfTableCell("Low", Bold: true, Color: PdfWriter.Danger),
                LabFlag.High => new PdfTableCell("High", Bold: true, Color: PdfWriter.Danger),
                LabFlag.Abnormal => new PdfTableCell("Check", Bold: true, Color: PdfWriter.Danger),
                _ => PdfTableCell.Empty
            };
        }

        static string Reference(LabResult r)
        {
            if (r.IsQualitative == 1) return r.ExpectedText ?? string.Empty;
            return LabEvaluator.RangeText(r.RefLow, r.RefHigh, LabEvaluator.DecimalsFor(LabCatalog.Find(r.TestCode), r.Unit));
        }

        var latest = LabService.LatestPerTest(newestFirst);
        foreach (var panel in latest.GroupBy(l => LabCatalog.Panel(l.Latest.PanelCode).Name))
        {
            pdf.Subheading(panel.Key);
            var table = new PdfTable(
                new PdfTableColumn("Test", 4), new PdfTableColumn("Result", 3), new PdfTableColumn("Reference", 3),
                new PdfTableColumn("Status", 2), new PdfTableColumn("Date", 2));
            foreach (var l in panel)
                table.AddRow(new PdfTableCell(l.Latest.TestName, Bold: true), LabEvaluator.ValueWithUnit(l.Latest),
                    Reference(l.Latest), Status(l.Latest), l.Latest.CollectedOn);
            pdf.Table(table);
        }

        var repeated = latest.Where(l => l.Count > 1).Select(l => l.Latest.TestCode).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (repeated.Count == 0) return;

        pdf.Subheading("History of repeated tests");
        var history = new PdfTable(
            new PdfTableColumn("Test", 4), new PdfTableColumn("Date", 2), new PdfTableColumn("Result", 3),
            new PdfTableColumn("Reference", 3), new PdfTableColumn("Status", 2));
        foreach (var group in newestFirst.Where(r => repeated.Contains(r.TestCode)).GroupBy(r => r.TestCode, StringComparer.OrdinalIgnoreCase))
            foreach (var r in group)
                history.AddRow(new PdfTableCell(r.TestName, Bold: true), r.CollectedOn, LabEvaluator.ValueWithUnit(r),
                    Reference(r), Status(r));
        pdf.Table(history);
    }

    private static void WritePastMedicinesTable(PdfWriter pdf, List<PastMedicineData> medicines)
    {
        var table = new PdfTable(
            new PdfTableColumn("Name", 3), new PdfTableColumn("Start", 2), new PdfTableColumn("End", 2),
            new PdfTableColumn("Courses", 1), new PdfTableColumn("Total Doses", 2));
        foreach (var m in medicines)
            table.AddRow(new PdfTableCell(m.Name, Bold: true), m.StartDate, m.EndDate ?? "—",
                m.Courses.ToString(), m.TotalDoses.ToString());
        pdf.Table(table);
    }

    private static void WriteDoseLogTable(PdfWriter pdf, List<DoseLogEntry> logs)
    {
        var table = new PdfTable(
            new PdfTableColumn("Date", 2), new PdfTableColumn("Medicine", 3),
            new PdfTableColumn("Scheduled", 2), new PdfTableColumn("Status", 2));
        foreach (var log in logs)
            table.AddRow(log.Date, log.MedicineName, log.ScheduledTime,
                new PdfTableCell(log.WasTaken ? "Taken" : "Missed", Bold: true,
                    Color: log.WasTaken ? PdfWriter.Success : PdfWriter.Danger));
        pdf.Table(table);
    }

    private static void WriteDoctorVisitCard(PdfWriter pdf, DoctorVisit v)
    {
        pdf.EnsureSpace(60);
        pdf.TextRow(v.DoctorName, v.VisitDate, fontSize: 11);
        if (!string.IsNullOrEmpty(v.Hospital))
            pdf.Paragraph(v.Hospital, fontSize: 9, color: PdfWriter.SecondaryText);
        if (!string.IsNullOrEmpty(v.Diagnosis))
            pdf.Paragraph($"Diagnosis: {v.Diagnosis}");
        if (!string.IsNullOrEmpty(v.Notes))
            pdf.Paragraph($"Notes: {v.Notes}", color: SKColor.Parse("#444444"));
        if (!string.IsNullOrEmpty(v.FollowUpDate))
            pdf.Paragraph($"Follow-up: {v.FollowUpDate}", color: PdfWriter.Primary);
        pdf.Space(4);
        pdf.Rule();
        pdf.Space(8);
    }

    private static void WriteMetricsTable(PdfWriter pdf, string metricType, List<HealthMetric> metrics)
    {
        pdf.Subheading(MetricDisplayName(metricType));
        var table = new PdfTable(
            new PdfTableColumn("Date", 3), new PdfTableColumn("Value", 2), new PdfTableColumn("Notes", 4));
        foreach (var m in metrics)
        {
            var val = m.SecondValue.HasValue ? $"{m.Value}/{m.SecondValue} {m.Unit}" : $"{m.Value} {m.Unit}";
            table.AddRow(DatePart(m.RecordedAt), new PdfTableCell(val, Bold: true), m.Notes ?? "—");
        }
        pdf.Table(table);
    }

    private static void WriteReportsTable(PdfWriter pdf, List<MedicalReport> reports)
    {
        var table = new PdfTable(
            new PdfTableColumn("Date", 2), new PdfTableColumn("Title", 3), new PdfTableColumn("Type", 1),
            new PdfTableColumn("Doctor", 2), new PdfTableColumn("Hospital", 2));
        foreach (var r in reports)
            table.AddRow(r.ReportDate, new PdfTableCell(r.Title, Bold: true), r.ReportType,
                r.DoctorName ?? "—", r.Hospital ?? "—");
        pdf.Table(table);
    }

    /// <summary>Embeds one stored report file. A failure only produces a "could not embed" line.</summary>
    private static void WriteReportFile(PdfWriter pdf, ReportFileRef file)
    {
        try
        {
            var path = FileStorageHelper.ResolvePath(file.FilePath);
            if (!File.Exists(path))
            {
                WriteEmbedFailure(pdf, file.Label, "file not found");
                return;
            }

            if (file.IsPdf)
            {
                // ReportPage.PageNumber is the file's ordinal within the report, not a page inside
                // the PDF, so every page of the PDF file is rendered.
                var pages = PdfImageHelper.RenderPdfPages(path);
                if (pages.Count == 0)
                {
                    WriteEmbedFailure(pdf, file.Label, "PDF could not be rendered");
                    return;
                }
                for (int i = 0; i < pages.Count; i++)
                {
                    var label = pages.Count > 1 ? $"{file.Label} — PDF page {i + 1}/{pages.Count}" : file.Label;
                    using var image = PdfImageHelper.FromEncodedBytes(pages[i]);
                    if (image == null) WriteEmbedFailure(pdf, label, "page image was empty");
                    else pdf.Image(image, label);
                    pages[i] = Array.Empty<byte>();
                }
            }
            else
            {
                using var image = PdfImageHelper.LoadImageFile(path);
                if (image == null) WriteEmbedFailure(pdf, file.Label, "image could not be decoded");
                else pdf.Image(image, file.Label);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Embedding {file.FilePath} failed: {ex}");
            WriteEmbedFailure(pdf, file.Label, ex.Message);
        }
    }

    private static void WriteEmbedFailure(PdfWriter pdf, string label, string reason)
    {
        pdf.Paragraph(label, fontSize: 11, bold: true);
        pdf.Paragraph($"Could not embed this file ({reason}).", fontSize: 9, color: PdfWriter.SecondaryText);
        pdf.Space(6);
    }

    private static string DatePart(string dateTime) =>
        dateTime.Length >= 10 ? dateTime[..10] : dateTime;

    // ─── Data Gathering ───────────────────────────────────────────────────────

    private async Task<ReportData> GatherDataAsync(bool includeReportFiles)
    {
        var data = new ReportData { GeneratedAt = DateTime.Now };

        var activeMeds = await _medicineRepo.GetAllActiveAsync();
        var pastMeds = await _medicineRepo.GetAllInactiveAsync();

        await GatherActiveMedicinesAsync(data, activeMeds);
        await GatherPastMedicinesAsync(data, pastMeds);
        await GatherDoseLogsAsync(data, activeMeds.Concat(pastMeds));
        data.DoctorVisits = await _doctorRepo.GetAllOrderedAsync();
        await GatherMetricsAsync(data);
        data.MedicalReports = await _reportRepo.GetAllOrderedAsync();
        data.LabResults = await _labRepo.GetAllOrderedAsync();
        data.Profile = await _profileRepo.GetAsync();
        if (includeReportFiles)
            await GatherReportFilesAsync(data);

        return data;
    }

    private async Task GatherActiveMedicinesAsync(ReportData data, List<Medicine> activeMeds)
    {
        var from30d = DateTimeHelper.ToDateTimeString(DateTime.Today.AddDays(-30));
        var toNow = DateTimeHelper.ToDateTimeString(DateTime.Today.AddDays(1).AddSeconds(-1));

        foreach (var med in activeMeds)
        {
            var schedules = await _scheduleRepo.GetByMedicineIdAsync(med.Id);
            var (streak, missedThisWeek) = await _medicineService.GetAdherenceStats(med.Id);
            var course = await _courseRepo.GetActiveCourseAsync(med.Id);
            var logs30d = await _logRepo.GetLogsForMedicineAsync(med.Id, from30d, toNow);
            var taken30d = logs30d.Count(l => l.WasTaken == 1);
            var compliance = logs30d.Count > 0 ? (double)taken30d / logs30d.Count : 0;

            data.ActiveMedicines.Add(new ActiveMedicineData
            {
                Name = med.Name,
                StockUnit = med.StockUnit,
                StartDate = med.StartDate,
                EndDate = med.EndDate,
                ScheduleTimes = string.Join(", ", schedules.Select(s => s.ScheduleTime)) +
                                $" ({schedules.Count}x daily)",
                Stock = med.StockCount,
                CourseProgress = course != null
                    ? $"{course.TotalTaken}/{(course.TotalScheduled > 0 ? course.TotalScheduled.ToString() : "∞")} doses"
                    : "—",
                Streak = streak,
                MissedThisWeek = missedThisWeek,
                Compliance30d = compliance,
                Instructions = med.Instructions ?? string.Empty,
                Notes = med.Notes ?? string.Empty
            });
        }
    }

    private async Task GatherPastMedicinesAsync(ReportData data, List<Medicine> pastMeds)
    {
        foreach (var med in pastMeds)
        {
            var courses = await _courseRepo.GetByMedicineIdAsync(med.Id);
            data.PastMedicines.Add(new PastMedicineData
            {
                Name = med.Name,
                StartDate = med.StartDate,
                EndDate = med.EndDate,
                Courses = courses.Count,
                TotalDoses = courses.Sum(c => c.TotalTaken)
            });
        }
    }

    private async Task GatherDoseLogsAsync(ReportData data, IEnumerable<Medicine> medicines)
    {
        var from3m = DateTimeHelper.ToDateTimeString(DateTime.Today.AddMonths(-3));
        var toNow = DateTimeHelper.ToDateTimeString(DateTime.Today.AddDays(1).AddSeconds(-1));

        foreach (var med in medicines)
        {
            var schedules = await _scheduleRepo.GetByMedicineIdAsync(med.Id);
            var logs = await _logRepo.GetLogsForMedicineAsync(med.Id, from3m, toNow);
            foreach (var log in logs.OrderByDescending(l => l.ScheduledFor))
            {
                var sched = schedules.FirstOrDefault(s => s.Id == log.ScheduleId);
                data.DoseLogs.Add(new DoseLogEntry
                {
                    Date = DatePart(log.ScheduledFor),
                    MedicineName = med.Name,
                    ScheduledTime = sched != null
                        ? (TimeSpan.TryParse(sched.ScheduleTime, out var t)
                            ? DateTime.Today.Add(t).ToString("h:mm tt")
                            : sched.ScheduleTime)
                        : "—",
                    WasTaken = log.WasTaken == 1
                });
            }
        }
    }

    private async Task GatherMetricsAsync(ReportData data)
    {
        var allMetrics = await _metricRepo.GetRecentAsync(500);
        data.MetricGroups = allMetrics
            .GroupBy(m => m.MetricType)
            .OrderBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.RecordedAt).ToList());
    }

    /// <summary>Collects file references only; decoding happens during rendering, one file at a time.</summary>
    private async Task GatherReportFilesAsync(ReportData data)
    {
        foreach (var report in data.MedicalReports)
        {
            var pages = await _reportPageRepo.GetByReportIdAsync(report.Id);
            foreach (var page in pages.OrderBy(p => p.PageNumber))
            {
                if (string.IsNullOrWhiteSpace(page.FilePath)) continue;

                var label = $"{report.Title} — {report.ReportDate}" +
                            (pages.Count > 1 ? $" (Page {page.PageNumber})" : "");
                data.ReportFiles.Add(new ReportFileRef(label, page.FilePath,
                    page.FileType.Equals("pdf", StringComparison.OrdinalIgnoreCase)));
            }
        }
    }

    // ─── Data models ──────────────────────────────────────────────────────────

    private class ReportData
    {
        public DateTime GeneratedAt { get; set; }
        public List<ActiveMedicineData> ActiveMedicines { get; } = new();
        public List<PastMedicineData> PastMedicines { get; } = new();
        public List<DoseLogEntry> DoseLogs { get; } = new();
        public List<DoctorVisit> DoctorVisits { get; set; } = new();
        public Dictionary<string, List<HealthMetric>> MetricGroups { get; set; } = new();
        public List<MedicalReport> MedicalReports { get; set; } = new();
        public List<LabResult> LabResults { get; set; } = new();
        public UserProfile Profile { get; set; } = new();
        public List<ReportFileRef> ReportFiles { get; } = new();
    }

    private record ReportFileRef(string Label, string FilePath, bool IsPdf);

    private class ActiveMedicineData
    {
        public string Name { get; set; } = string.Empty;
        public string StockUnit { get; set; } = string.Empty;
        public string StartDate { get; set; } = string.Empty;
        public string? EndDate { get; set; }
        public string ScheduleTimes { get; set; } = string.Empty;
        public double Stock { get; set; }
        public string CourseProgress { get; set; } = string.Empty;
        public int Streak { get; set; }
        public int MissedThisWeek { get; set; }
        public double Compliance30d { get; set; }
        public string Instructions { get; set; } = string.Empty;
        public string Notes { get; set; } = string.Empty;
    }

    private class PastMedicineData
    {
        public string Name { get; set; } = string.Empty;
        public string StartDate { get; set; } = string.Empty;
        public string? EndDate { get; set; }
        public int Courses { get; set; }
        public int TotalDoses { get; set; }
    }

    private class DoseLogEntry
    {
        public string Date { get; set; } = string.Empty;
        public string MedicineName { get; set; } = string.Empty;
        public string ScheduledTime { get; set; } = string.Empty;
        public bool WasTaken { get; set; }
    }

    private static string MetricDisplayName(string metricType) => metricType switch
    {
        Constants.MetricTypes.BloodPressure => "Blood pressure",
        Constants.MetricTypes.HeartRate => "Heart rate",
        Constants.MetricTypes.BloodSugar => "Blood sugar",
        _ => metricType
    };
}
