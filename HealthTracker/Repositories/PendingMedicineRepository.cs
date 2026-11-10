using HealthTracker.Models;
using HealthTracker.Services;

namespace HealthTracker.Repositories;

public class PendingMedicineRepository : BaseRepository<PendingMedicine>
{
    public PendingMedicineRepository(DatabaseService db) : base(db) { }
}
