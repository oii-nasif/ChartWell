using HealthTracker.Helpers;
using HealthTracker.Models;
using HealthTracker.Repositories;

namespace HealthTracker.Services;

/// <summary>
/// Doctor visits. PrescriptionImagePath is persisted relative to the HealthApp base folder; visits handed
/// out by the read methods have it resolved to an absolute path. Follow-up reminders are kept in sync on
/// every save/update/delete.
/// </summary>
public class DoctorService
{
    private readonly DoctorVisitRepository _repo;
    private readonly MedicalReportRepository _reportRepo;
    private readonly IReminderService _reminderService;

    public DoctorService(DoctorVisitRepository repo, MedicalReportRepository reportRepo, IReminderService reminderService)
    {
        _repo = repo;
        _reportRepo = reportRepo;
        _reminderService = reminderService;
    }

    public async Task<DoctorVisit> AddVisit(string doctorName, string? hospital, string visitDate,
        string? diagnosis, string? notes, string? followUpDate, string? prescriptionImageSourcePath = null)
    {
        // RULE-06: follow-up date must be today or future
        if (followUpDate != null)
        {
            if (!DateTimeHelper.TryParseStored(followUpDate, out var fuDate))
                throw new ArgumentException("Follow-up date is not a valid date.");
            if (fuDate.Date < DateTime.Today)
                throw new ArgumentException("Follow-up date must be today or a future date.");
        }

        var visit = new DoctorVisit
        {
            DoctorName = doctorName,
            Hospital = hospital,
            VisitDate = visitDate,
            Diagnosis = diagnosis,
            Notes = notes,
            FollowUpDate = followUpDate,
            PrescriptionImagePath = ImportPrescriptionImage(prescriptionImageSourcePath, visitDate),
            CreatedAt = DateTimeHelper.ToDateTimeString(DateTime.Now)
        };

        await _repo.InsertAsync(visit);
        await _reminderService.ScheduleFollowUpReminder(visit.Id);
        return Resolve(visit);
    }

    public async Task UpdateVisit(DoctorVisit visit)
    {
        // The caller may hold a resolved (absolute) path, or a freshly picked image outside our folder.
        // ToStoredPath turns anything under the HealthApp folder (current or a legacy install's) into a
        // relative path; what is still rooted afterwards is a foreign file that has to be imported.
        var path = visit.PrescriptionImagePath;
        string? stored = null;
        if (!string.IsNullOrEmpty(path))
        {
            stored = FileStorageHelper.ToStoredPath(path);
            if (Path.IsPathRooted(stored) && File.Exists(path))
                stored = ImportPrescriptionImage(path, visit.VisitDate);
        }
        visit.PrescriptionImagePath = string.IsNullOrEmpty(stored) ? null : stored;

        // A replaced or removed image must not leave its file behind.
        var previous = await _repo.GetByIdAsync(visit.Id);
        var previousPath = previous?.PrescriptionImagePath;
        var previousStored = string.IsNullOrEmpty(previousPath) ? null : FileStorageHelper.ToStoredPath(previousPath);
        if (previousStored is not null && !string.Equals(previousStored, visit.PrescriptionImagePath, StringComparison.Ordinal))
        {
            try
            {
                var oldFile = FileStorageHelper.ResolvePath(previousStored);
                if (!string.IsNullOrEmpty(oldFile) && File.Exists(oldFile))
                    File.Delete(oldFile);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DoctorService: could not delete old prescription image: {ex.Message}");
            }
        }

        await _repo.UpdateAsync(visit);
        // Schedules when a future follow-up exists, cancels otherwise.
        await _reminderService.ScheduleFollowUpReminder(visit.Id);
        Resolve(visit);
    }

    public async Task DeleteVisit(int id)
    {
        var visit = await _repo.GetByIdAsync(id);
        if (visit is null) return;

        await _reminderService.CancelFollowUpReminder(id);

        var imagePath = FileStorageHelper.ResolvePath(visit.PrescriptionImagePath);
        if (!string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
            File.Delete(imagePath);

        // Reports stay, but must not point at a visit that no longer exists.
        await _reportRepo.ClearDoctorVisitAsync(id);
        await _repo.DeleteAsync(visit);
    }

    public async Task<List<DoctorVisit>> GetAllVisits() =>
        (await _repo.GetAllOrderedAsync()).Select(Resolve).ToList();

    public async Task<DoctorVisit?> GetVisitById(int id)
    {
        var visit = await _repo.GetByIdAsync(id);
        return visit is null ? null : Resolve(visit);
    }

    /// <summary>Copies a picked image into the prescriptions folder; returns the stored (relative) path or null.</summary>
    private static string? ImportPrescriptionImage(string? sourcePath, string visitDate)
    {
        if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath)) return null;

        FileStorageHelper.EnsureFoldersExist();
        var folder = FileStorageHelper.GetSubFolder("Prescription");
        var ext = Path.GetExtension(sourcePath).TrimStart('.');
        var fileName = FileStorageHelper.BuildFileName("Rx", visitDate, ext);
        var dest = Path.Combine(folder, fileName);
        File.Copy(sourcePath, dest, overwrite: false);
        return FileStorageHelper.ToStoredPath(dest);
    }

    private static DoctorVisit Resolve(DoctorVisit v)
    {
        if (!string.IsNullOrEmpty(v.PrescriptionImagePath))
            v.PrescriptionImagePath = FileStorageHelper.ResolvePath(v.PrescriptionImagePath);
        return v;
    }
}
