using System.Globalization;
using HealthTracker.Helpers;
using HealthTracker.Models;
using HealthTracker.Repositories;
using HealthTracker.Services.Labs;

namespace HealthTracker.Services;

/// <summary>
/// Merged, newest-first feed of visits, reports, lab results, vitals, dose logs, medicines and completed courses.
///
/// Paging: every source is loaded fully (each is a single indexed, ordered query on a single-user local
/// database), merged in memory, sorted by SortKey and then sliced with offset/pageSize. This keeps paging
/// correct across sources — no per-source LIMIT can hide older entries of one type behind newer entries
/// of another — at the cost of one in-memory sort per page, which is negligible at this data volume.
/// </summary>
public class TimelineService
{
    private readonly DoctorVisitRepository _visitRepo;
    private readonly MedicalReportRepository _reportRepo;
    private readonly HealthMetricRepository _metricRepo;
    private readonly MedicineLogRepository _logRepo;
    private readonly MedicineRepository _medicineRepo;
    private readonly MedicineCourseRepository _courseRepo;
    private readonly LabResultRepository _labRepo;

    public TimelineService(DoctorVisitRepository visitRepo, MedicalReportRepository reportRepo,
        HealthMetricRepository metricRepo, MedicineLogRepository logRepo, MedicineRepository medicineRepo,
        MedicineCourseRepository courseRepo, LabResultRepository labRepo)
    {
        _labRepo = labRepo;
        _visitRepo = visitRepo;
        _reportRepo = reportRepo;
        _metricRepo = metricRepo;
        _logRepo = logRepo;
        _medicineRepo = medicineRepo;
        _courseRepo = courseRepo;
    }

    public async Task<List<TimelineEntry>> GetTimeline(int pageSize = 20, int offset = 0)
    {
        var entries = new List<TimelineEntry>();

        // Medicines are needed by name for logs and courses: fetch once instead of per row.
        var medicines = await _medicineRepo.GetAllAsync();
        var medicineNames = medicines.ToDictionary(m => m.Id, m => m.Name);
        string NameOf(int medicineId) => medicineNames.TryGetValue(medicineId, out var n) ? n : "Unknown";

        var visits = await _visitRepo.GetAllOrderedAsync();
        foreach (var v in visits)
        {
            // Dated by the visit itself, not by when it was typed in.
            var dateOnly = DateTimeHelper.DatePart(v.VisitDate);
            entries.Add(new TimelineEntry
            {
                SourceId = v.Id,
                EventType = "doctor_visit",
                DisplayLabel = "Doctor Visit",
                Description = $"{v.DoctorName}{(v.Hospital != null ? $" — {v.Hospital}" : "")}",
                EventDate = dateOnly,
                SortKey = SortKeyFor(v.VisitDate, v.CreatedAt),
                TimeText = ParseTimeAmPm(v.VisitDate),
                SubText = v.FollowUpDate is null ? null : $"Follow-up: {v.FollowUpDate}",
                MonthYearGroup = DateTimeHelper.ToMonthYearGroup(dateOnly),
                TagColor = "#1B5FA8",
                TagIcon = "🏥"
            });
        }

        var reports = await _reportRepo.GetAllOrderedAsync();
        foreach (var r in reports)
        {
            var dateOnly = DateTimeHelper.DatePart(r.CreatedAt);
            entries.Add(new TimelineEntry
            {
                SourceId = r.Id,
                EventType = "report_added",
                DisplayLabel = "Report",
                Description = $"{r.Title} ({r.ReportType})",
                EventDate = dateOnly,
                SortKey = r.CreatedAt ?? dateOnly,
                TimeText = ParseTimeAmPm(r.CreatedAt),
                MonthYearGroup = DateTimeHelper.ToMonthYearGroup(dateOnly),
                TagColor = "#7B2FBE",
                TagIcon = "📄"
            });
        }

        // One entry per sitting at the lab, not one per value.
        var labRows = await _labRepo.GetAllOrderedAsync();
        foreach (var batch in labRows.GroupBy(r => r.BatchId))
        {
            var rows = batch.OrderBy(r => r.Id).ToList();
            var first = rows[0];
            var panels = rows.Select(r => Constants.LabCatalog.Panel(r.PanelCode).Name).Distinct().ToList();
            var what = panels.Count <= 2 ? string.Join(", ", panels) : $"{panels[0]}, {panels[1]} and {panels.Count - 2} more";
            var count = rows.Count == 1 ? "1 result" : $"{rows.Count} results";
            var toCheck = rows.Where(r => LabEvaluator.NeedsAttention(LabEvaluator.Evaluate(r))).Select(r => r.TestName).ToList();
            entries.Add(new TimelineEntry
            {
                SourceId = first.Id,
                EventType = "lab_results",
                DisplayLabel = "Lab results",
                Description = $"{what} · {count}",
                EventDate = first.CollectedOn,
                SortKey = SortKeyFor(first.CollectedOn, first.CreatedAt),
                TimeText = null,
                SubText = toCheck.Count == 0
                    ? first.LabName
                    : $"To check: {string.Join(", ", toCheck.Take(4))}{(toCheck.Count > 4 ? $" and {toCheck.Count - 4} more" : "")}",
                MonthYearGroup = DateTimeHelper.ToMonthYearGroup(first.CollectedOn),
            });
        }

        var metrics = await _metricRepo.GetAllOrderedAsync();
        foreach (var m in metrics)
        {
            var dateOnly = DateTimeHelper.DatePart(m.RecordedAt);
            entries.Add(new TimelineEntry
            {
                SourceId = m.Id,
                EventType = "metric_recorded",
                DisplayLabel = "Vital recorded",
                Description = $"{m.MetricType}: {m.Value}{(m.SecondValue.HasValue ? $"/{m.SecondValue}" : "")} {m.Unit}",
                EventDate = dateOnly,
                SortKey = m.RecordedAt ?? dateOnly,
                TimeText = ParseTimeAmPm(m.RecordedAt),
                MonthYearGroup = DateTimeHelper.ToMonthYearGroup(dateOnly),
                TagColor = "#1A8A4A",
                TagIcon = "📊"
            });
        }

        var logs = await _logRepo.GetAllOrderedAsync();
        foreach (var l in logs)
        {
            var dateOnly = DateTimeHelper.DatePart(l.ScheduledFor);
            var taken = l.WasTaken == 1;
            entries.Add(new TimelineEntry
            {
                SourceId = l.Id,
                EventType = taken ? "dose_taken" : "dose_missed",
                DisplayLabel = taken ? "Dose Taken" : "Dose Missed",
                Description = NameOf(l.MedicineId),
                EventDate = dateOnly,
                SortKey = l.ScheduledFor ?? dateOnly,
                TimeText = ParseTimeAmPm(l.ScheduledFor),
                MonthYearGroup = DateTimeHelper.ToMonthYearGroup(dateOnly),
                TagColor = taken ? "#1A8A4A" : "#C0392B",
                TagIcon = taken ? "✅" : "❌"
            });
        }

        foreach (var med in medicines)
        {
            var createdDateOnly = DateTimeHelper.DatePart(med.CreatedAt);
            entries.Add(new TimelineEntry
            {
                SourceId = med.Id,
                EventType = "medicine_added",
                DisplayLabel = "Medicine Added",
                Description = med.Name,
                EventDate = createdDateOnly,
                SortKey = med.CreatedAt ?? createdDateOnly,
                TimeText = ParseTimeAmPm(med.CreatedAt),
                MonthYearGroup = DateTimeHelper.ToMonthYearGroup(createdDateOnly),
                TagColor = "#E67E22",
                TagIcon = "💊"
            });
        }

        // Completed courses
        var allCourses = await _courseRepo.GetAllAsync();
        foreach (var c in allCourses.Where(c => c.Status == "Completed" && !string.IsNullOrEmpty(c.EndDate)))
        {
            var dateOnly = DateTimeHelper.DatePart(c.EndDate);
            entries.Add(new TimelineEntry
            {
                SourceId = c.Id,
                EventType = "course_completed",
                DisplayLabel = "Course Completed",
                Description = NameOf(c.MedicineId),
                EventDate = dateOnly,
                SortKey = c.EndDate!,
                TimeText = null,
                MonthYearGroup = DateTimeHelper.ToMonthYearGroup(dateOnly),
                TagColor = "#1A8A4A",
                TagIcon = "✅"
            });
        }

        return entries
            .OrderByDescending(e => e.SortKey, StringComparer.Ordinal)
            .Skip(offset)
            .Take(pageSize)
            .ToList();
    }

    /// <summary>
    /// Sort key for an event dated by a date-only string: the date plus the creation time, so several
    /// events on the same day keep a stable, meaningful order against date-time keyed events.
    /// </summary>
    private static string SortKeyFor(string dateOnly, string? createdAt)
    {
        if (DateTimeHelper.TryParseStored(dateOnly, out var d) && d.TimeOfDay == TimeSpan.Zero
            && DateTimeHelper.TryParseStored(createdAt, out var c))
            return DateTimeHelper.ToDateTimeString(d.Date.Add(c.TimeOfDay));
        return DateTimeHelper.TryParseStored(dateOnly, out var dt) ? DateTimeHelper.ToDateTimeString(dt) : dateOnly;
    }

    private static string? ParseTimeAmPm(string? dateTimeStr)
    {
        if (string.IsNullOrEmpty(dateTimeStr)) return null;
        if (DateTimeHelper.TryParseStored(dateTimeStr, out var dt) && dt.TimeOfDay != TimeSpan.Zero)
            return dt.ToString("h:mm tt", CultureInfo.InvariantCulture);
        // Try parsing just a time component like "HH:mm"
        if (dateTimeStr.Length == 5 && DateTimeHelper.TryParseTime(dateTimeStr, out var ts))
            return DateTime.Today.Add(ts).ToString("h:mm tt", CultureInfo.InvariantCulture);
        return null;
    }
}
