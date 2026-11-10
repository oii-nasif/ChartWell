using HealthTracker.Constants;
using HealthTracker.Helpers;
using HealthTracker.Models;
using HealthTracker.Repositories;

namespace HealthTracker.Services;

/// <summary>
/// Medical report storage. File paths are persisted relative to the HealthApp base folder; every model
/// handed out by the read methods has <c>FilePath</c> already resolved to an absolute path, so callers
/// can open/share it directly (they should still not write a resolved model back to the database).
/// </summary>
public class ReportService
{
    private readonly MedicalReportRepository _repo;
    private readonly ReportPageRepository _pageRepo;
    private readonly DatabaseService _databaseService;

    public ReportService(MedicalReportRepository repo, ReportPageRepository pageRepo, DatabaseService databaseService)
    {
        _repo = repo;
        _pageRepo = pageRepo;
        _databaseService = databaseService;
    }

    public async Task<MedicalReport> SaveReport(string sourcePath, string type, string title,
        string fileType, string? doctorName, string? hospital, string reportDate,
        string? notes, int? visitId)
    {
        return await SaveMultiPageReport([(sourcePath, fileType)], type, title, doctorName,
            hospital, reportDate, notes, visitId);
    }

    /// <summary>
    /// Copies every page into the report folder first, then inserts the report and its pages in a single
    /// transaction. If anything fails the copied files are removed, so no half-saved report is left behind.
    /// Pages with an unsupported file type are skipped; the remaining pages are numbered 1..n without gaps.
    /// </summary>
    public async Task<MedicalReport> SaveMultiPageReport(List<(string Path, string FileType)> pages,
        string type, string title, string? doctorName, string? hospital, string reportDate,
        string? notes, int? visitId)
    {
        if (pages.Count == 0)
            throw new ArgumentException("At least one page is required.");
        if (!AppConstants.ReportTypes.Contains(type))
            throw new ArgumentException($"Unknown report type '{type}'.");

        var validPages = pages.Where(p => ValidationHelper.IsValidFileType(p.FileType, out _)).ToList();
        if (validPages.Count == 0)
        {
            ValidationHelper.IsValidFileType(pages[0].FileType, out var error);
            throw new ArgumentException(error ?? "Unsupported file type.");
        }

        FileStorageHelper.EnsureFoldersExist();
        var destFolder = FileStorageHelper.GetSubFolder(type);

        // 1. Copy all files. Any failure here leaves the database untouched.
        var copied = new List<(string AbsolutePath, string FileType)>();
        try
        {
            foreach (var (srcPath, ext) in validPages)
            {
                var dest = Path.Combine(destFolder, FileStorageHelper.BuildFileName(type, reportDate, ext));
                File.Copy(srcPath, dest, overwrite: false);
                if (!File.Exists(dest))
                    throw new IOException("File copy failed. Report not saved.");
                copied.Add((dest, ext.ToLowerInvariant().TrimStart('.')));
            }

            // 2. Insert report + pages atomically.
            var report = new MedicalReport
            {
                DoctorVisitId = visitId,
                Title = title,
                ReportType = type,
                FilePath = FileStorageHelper.ToStoredPath(copied[0].AbsolutePath),
                FileType = copied[0].FileType,
                ReportDate = reportDate,
                DoctorName = doctorName,
                Hospital = hospital,
                Notes = notes,
                CreatedAt = DateTimeHelper.ToDateTimeString(DateTime.Now)
            };

            var db = await _databaseService.GetConnectionAsync();
            await db.RunInTransactionAsync(conn =>
            {
                conn.Insert(report);
                for (int i = 0; i < copied.Count; i++)
                {
                    conn.Insert(new ReportPage
                    {
                        ReportId = report.Id,
                        FilePath = FileStorageHelper.ToStoredPath(copied[i].AbsolutePath),
                        FileType = copied[i].FileType,
                        PageNumber = i + 1
                    });
                }
            });

            return Resolve(report);
        }
        catch
        {
            foreach (var (path, _) in copied)
                TryDelete(path);
            throw;
        }
    }

    public async Task DeleteReport(int id)
    {
        var report = await _repo.GetByIdAsync(id);
        if (report is null) return;

        // Delete page files
        var pages = await _pageRepo.GetByReportIdAsync(id);
        foreach (var page in pages)
            TryDelete(FileStorageHelper.ResolvePath(page.FilePath));
        await _pageRepo.DeleteByReportIdAsync(id);

        // Delete primary file if not already deleted via pages
        TryDelete(FileStorageHelper.ResolvePath(report.FilePath));

        await _repo.DeleteAsync(report);

        // Lab values read from this report stay in Labs; only their link to the deleted report goes.
        var db = await _databaseService.GetConnectionAsync();
        await db.ExecuteAsync("UPDATE LabResult SET ReportId = NULL WHERE ReportId = ?", id);
    }

    public async Task<List<MedicalReport>> GetAllReports() =>
        (await _repo.GetAllOrderedAsync()).Select(Resolve).ToList();

    public async Task<List<MedicalReport>> GetReportsByVisit(int visitId) =>
        (await _repo.GetByVisitIdAsync(visitId)).Select(Resolve).ToList();

    public async Task<List<ReportPage>> GetReportPages(int reportId) =>
        (await _pageRepo.GetByReportIdAsync(reportId)).Select(Resolve).ToList();

    /// <summary>Absolute path of a stored report/page file path.</summary>
    public static string ResolveFilePath(string? storedPath) => FileStorageHelper.ResolvePath(storedPath);

    private static MedicalReport Resolve(MedicalReport r)
    {
        r.FilePath = FileStorageHelper.ResolvePath(r.FilePath);
        return r;
    }

    private static ReportPage Resolve(ReportPage p)
    {
        p.FilePath = FileStorageHelper.ResolvePath(p.FilePath);
        return p;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                File.Delete(path);
        }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }
}
