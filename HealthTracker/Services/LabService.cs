using HealthTracker.Constants;
using HealthTracker.Helpers;
using HealthTracker.Models;
using HealthTracker.Repositories;
using HealthTracker.Services.Labs;

namespace HealthTracker.Services;

/// <summary>What the entry form hands to <see cref="LabService.AddResults"/> for one test.</summary>
public sealed class LabEntry
{
    /// <summary>Catalog code; leave empty for a custom test (the code is derived from <see cref="TestName"/>).</summary>
    public string TestCode { get; init; } = string.Empty;
    public string TestName { get; init; } = string.Empty;
    public bool IsQualitative { get; init; }
    public double? Value { get; init; }
    public string? ValueText { get; init; }
    public string Unit { get; init; } = string.Empty;
    /// <summary>Range printed on the report, in <see cref="Unit"/>. When both are null the catalog range is used.</summary>
    public double? RefLow { get; init; }
    public double? RefHigh { get; init; }
    /// <summary>True when the user typed the range (so "no range" is respected instead of falling back to the catalog).</summary>
    public bool HasCustomRange { get; init; }
    /// <summary>Expected word for a custom qualitative test (catalog tests use their own).</summary>
    public string? ExpectedText { get; init; }
    public string? Notes { get; init; }
}

/// <summary>The newest result of a test plus the one before it (for the trend arrow).</summary>
public sealed record LabLatest(LabResult Latest, LabResult? Previous, int Count)
{
    public LabFlag Flag => LabEvaluator.Evaluate(Latest);
}

/// <summary>All results saved in one sitting.</summary>
public sealed record LabBatch(string BatchId, string CollectedOn, string? LabName, int? ReportId, List<LabResult> Results)
{
    public int AttentionCount => Results.Count(r => LabEvaluator.NeedsAttention(LabEvaluator.Evaluate(r)));
}

/// <summary>
/// Lab results: validation, reference-range snapshotting, derived values and the read models the Labs
/// pages use. MAUI-free (linked into the test project).
/// </summary>
public class LabService
{
    /// <summary>Guards against typos such as a missing decimal point; no lab value is this large.</summary>
    public const double MaxValue = 10_000_000;

    private readonly LabResultRepository _repo;
    private readonly UserProfileRepository _profileRepo;

    public LabService(LabResultRepository repo, UserProfileRepository profileRepo)
    {
        _repo = repo;
        _profileRepo = profileRepo;
    }

    /// <summary>
    /// Saves one sitting's results as a batch and appends any values that can be derived from them
    /// (LDL, VLDL, non-HDL, ratio, eGFR, indirect bilirubin, globulin). Throws <see cref="ArgumentException"/>
    /// with a user-readable message when an entry is invalid; nothing is saved in that case.
    /// </summary>
    public async Task<List<LabResult>> AddResults(IReadOnlyCollection<LabEntry> entries, string collectedOn,
        string? labName = null, int? reportId = null)
    {
        if (entries.Count == 0) throw new ArgumentException("Enter at least one result.");
        if (!DateTimeHelper.TryParseStored(collectedOn, out var date))
            throw new ArgumentException("Choose the date the sample was collected.");
        if (date.Date > DateTime.Today)
            throw new ArgumentException("The collection date cannot be in the future.");

        var profile = await _profileRepo.GetAsync();
        var dateText = DateTimeHelper.ToDateString(date);
        var batchId = Guid.NewGuid().ToString("N");
        var now = DateTimeHelper.ToDateTimeString(DateTime.Now);
        labName = string.IsNullOrWhiteSpace(labName) ? null : labName.Trim();

        var rows = new List<LabResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var row = BuildRow(entry, profile.Sex);
            if (!seen.Add(row.TestCode))
                throw new ArgumentException($"{row.TestName} is entered twice.");
            rows.Add(row);
        }

        rows.AddRange(BuildDerivedRows(rows, profile, date));

        foreach (var row in rows)
        {
            row.CollectedOn = dateText;
            row.LabName = labName;
            row.ReportId = reportId;
            row.BatchId = batchId;
            row.CreatedAt = now;
        }

        await _repo.InsertAllAsync(rows);
        return rows;
    }

    private static LabResult BuildRow(LabEntry entry, string? sex)
    {
        var def = LabCatalog.Find(entry.TestCode);
        var name = def?.Name ?? entry.TestName.Trim();
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Give the test a name.");

        var row = new LabResult
        {
            TestCode = def?.Code ?? LabCatalog.CustomCode(name),
            TestName = name,
            PanelCode = def?.PanelCode ?? LabCatalog.CustomPanel,
            Notes = string.IsNullOrWhiteSpace(entry.Notes) ? null : entry.Notes.Trim(),
        };

        var qualitative = def?.IsQualitative ?? entry.IsQualitative;
        if (qualitative)
        {
            var text = entry.ValueText?.Trim();
            if (string.IsNullOrEmpty(text)) throw new ArgumentException($"Choose a result for {name}.");
            if (def is not null)
            {
                // Store the catalog spelling so history and flags compare cleanly.
                text = def.Options.FirstOrDefault(o => o.Equals(text, StringComparison.OrdinalIgnoreCase))
                       ?? throw new ArgumentException($"\"{text}\" is not a valid result for {name}.");
            }
            row.IsQualitative = 1;
            row.ValueText = text;
            row.ExpectedText = def is not null ? def.Expected
                : string.IsNullOrWhiteSpace(entry.ExpectedText) ? null : entry.ExpectedText.Trim();
            return row;
        }

        if (entry.Value is not { } value || double.IsNaN(value) || double.IsInfinity(value))
            throw new ArgumentException($"Enter a number for {name}.");
        if (value < 0) throw new ArgumentException($"{name} cannot be negative.");
        if (value > MaxValue) throw new ArgumentException($"{name} looks too large. Check the value and unit.");

        var unit = entry.Unit?.Trim() ?? string.Empty;
        if (def is not null) unit = def.CanonicalUnit(unit) ?? (unit.Length == 0 ? def.Unit : unit);

        row.Value = value;
        row.Unit = unit;

        if (entry.HasCustomRange || entry.RefLow is not null || entry.RefHigh is not null)
        {
            if (entry.RefLow is { } l && entry.RefHigh is { } h && l > h)
                throw new ArgumentException($"The reference range for {name} is the wrong way round.");
            row.RefLow = entry.RefLow;
            row.RefHigh = entry.RefHigh;
        }
        else if (def is not null && def.FactorToPrimary(unit) is not null)
        {
            (row.RefLow, row.RefHigh) = LabEvaluator.RangeIn(def, sex, unit);
        }
        return row;
    }

    private static List<LabResult> BuildDerivedRows(List<LabResult> rows, UserProfile profile, DateTime collectedOn)
    {
        var primary = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (row.IsQualitative == 1 || row.Value is null) continue;
            var def = LabCatalog.Find(row.TestCode);
            if (def is null) continue;
            // A value in a unit the catalog cannot convert must not feed a formula.
            if (LabEvaluator.Convert(def, row.Value.Value, row.Unit, def.Unit) is { } v) primary[def.Code] = v;
        }

        var derivedRows = new List<LabResult>();
        foreach (var d in LabCalculator.Derive(primary, profile.Sex, profile.AgeOn(collectedOn)))
        {
            var def = LabCatalog.Find(d.TestCode);
            if (def is null) continue;
            var (low, high) = def.RangeFor(profile.Sex);
            derivedRows.Add(new LabResult
            {
                TestCode = def.Code,
                TestName = def.Name,
                PanelCode = def.PanelCode,
                Value = d.Value,
                Unit = def.Unit,
                RefLow = low,
                RefHigh = high,
                IsCalculated = 1,
                Notes = $"Calculated: {d.Formula}",
            });
        }
        return derivedRows;
    }

    /// <summary>Updates the editable fields of one result (value, unit, range, date, lab, notes).</summary>
    public async Task UpdateResult(LabResult result)
    {
        if (!DateTimeHelper.TryParseStored(result.CollectedOn, out var date))
            throw new ArgumentException("Choose the date the sample was collected.");
        if (date.Date > DateTime.Today)
            throw new ArgumentException("The collection date cannot be in the future.");
        result.CollectedOn = DateTimeHelper.ToDateString(date);

        if (result.IsQualitative == 1)
        {
            if (string.IsNullOrWhiteSpace(result.ValueText))
                throw new ArgumentException($"Choose a result for {result.TestName}.");
        }
        else
        {
            if (result.Value is not { } v || double.IsNaN(v) || double.IsInfinity(v))
                throw new ArgumentException($"Enter a number for {result.TestName}.");
            if (v < 0) throw new ArgumentException($"{result.TestName} cannot be negative.");
            if (v > MaxValue) throw new ArgumentException($"{result.TestName} looks too large. Check the value and unit.");
            if (result.RefLow is { } l && result.RefHigh is { } h && l > h)
                throw new ArgumentException("The reference range is the wrong way round.");
        }
        // An edited value is the user's own number, no longer an estimate.
        result.IsCalculated = 0;
        if (result.Notes is not null && result.Notes.StartsWith("Calculated:", StringComparison.Ordinal)) result.Notes = null;
        await _repo.UpdateAsync(result);
    }

    public Task DeleteResult(LabResult result) => _repo.DeleteAsync(result);

    public Task DeleteBatch(string batchId) => _repo.DeleteBatchAsync(batchId);

    public Task<LabResult?> GetById(int id) => _repo.GetByIdAsync(id);

    public Task<List<LabResult>> GetAll() => _repo.GetAllOrderedAsync();

    /// <summary>Newest first.</summary>
    public Task<List<LabResult>> GetHistory(string testCode) => _repo.GetByTestAsync(testCode);

    public Task<List<LabResult>> GetForReport(int reportId) => _repo.GetByReportAsync(reportId);

    public Task DetachReport(int reportId) => _repo.DetachReportAsync(reportId);

    public Task<int> Count() => _repo.CountAsync();

    /// <summary>One entry per test that has ever been recorded, in catalog order (custom tests last, by name).</summary>
    public async Task<List<LabLatest>> GetLatestPerTest()
    {
        var all = await _repo.GetAllOrderedAsync();
        return LatestPerTest(all);
    }

    public static List<LabLatest> LatestPerTest(IEnumerable<LabResult> newestFirst)
    {
        var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < LabCatalog.Tests.Count; i++) order[LabCatalog.Tests[i].Code] = i;

        return newestFirst
            .GroupBy(r => r.TestCode, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var list = g.ToList();
                return new LabLatest(list[0], list.Count > 1 ? list[1] : null, list.Count);
            })
            .OrderBy(l => order.TryGetValue(l.Latest.TestCode, out var i) ? i : int.MaxValue)
            .ThenBy(l => l.Latest.TestName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Tests whose newest result is outside its range or not the expected word.</summary>
    public async Task<List<LabLatest>> GetAttention()
        => (await GetLatestPerTest()).Where(l => LabEvaluator.NeedsAttention(l.Flag)).ToList();

    /// <summary>Sittings, newest first.</summary>
    public async Task<List<LabBatch>> GetBatches()
    {
        var all = await _repo.GetAllOrderedAsync();
        return all
            .GroupBy(r => r.BatchId)
            .Select(g =>
            {
                var rows = g.OrderBy(r => r.Id).ToList();
                var first = rows[0];
                return new LabBatch(g.Key, first.CollectedOn, first.LabName, first.ReportId, rows);
            })
            .OrderByDescending(b => b.CollectedOn, StringComparer.Ordinal)
            .ThenByDescending(b => b.Results[0].Id)
            .ToList();
    }

    /// <summary>
    /// The custom tests entered so far, newest definition per name, so the entry form can offer them
    /// again with the same unit and range.
    /// </summary>
    public async Task<List<LabResult>> GetCustomTests()
    {
        var all = await _repo.GetAllOrderedAsync();
        return all.Where(r => LabCatalog.IsCustom(r.TestCode))
            .GroupBy(r => r.TestCode, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(r => r.TestName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The unit the user last entered this test in, or the catalog's primary unit.</summary>
    public async Task<Dictionary<string, string>> GetPreferredUnits()
    {
        var all = await _repo.GetAllOrderedAsync();
        return all.Where(r => r.IsQualitative == 0 && r.IsCalculated == 0 && !string.IsNullOrEmpty(r.Unit))
            .GroupBy(r => r.TestCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Unit, StringComparer.OrdinalIgnoreCase);
    }
}
