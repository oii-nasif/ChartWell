using SQLite;

namespace HealthTracker.Services;

/// <summary>
/// Hand-written schema migrations, versioned with <c>PRAGMA user_version</c>. MAUI-free so the test
/// project can run it against an in-memory database. Every step is idempotent and every table rebuild
/// runs inside one transaction, so a crash mid-migration leaves the previous data intact.
///
/// Call after <c>CreateTableAsync</c> has run for every model: sqlite-net already appends newly mapped
/// columns, and the steps here backfill them and reshape what sqlite-net cannot.
/// </summary>
public static class DatabaseMigrator
{
    public const int CurrentVersion = 3;

    /// <param name="db">Open connection; tables already created/upgraded by sqlite-net.</param>
    /// <param name="toStoredPath">Converts an absolute file path to the relative form the app now stores (v3).</param>
    public static async Task MigrateAsync(SQLiteAsyncConnection db, Func<string, string> toStoredPath)
    {
        var version = await GetUserVersionAsync(db);

        if (version < 1)
        {
            await RemoveDroppedColumnsAsync(db);
            await SetUserVersionAsync(db, 1);
        }
        if (version < 2)
        {
            await AddCourseExtensionAndScheduleCreatedAtAsync(db);
            await SetUserVersionAsync(db, 2);
        }
        if (version < 3)
        {
            await RewriteAbsoluteFilePathsAsync(db, toStoredPath);
            await SetUserVersionAsync(db, 3);
        }
    }

    // ── v1: legacy column removals (MedicineSchedule.DoseAmount, Medicine.Dosage) ─────────────

    private static async Task RemoveDroppedColumnsAsync(SQLiteAsyncConnection db)
    {
        var schedCols = await GetColumnsAsync(db, "MedicineSchedule");
        if (schedCols.Contains("DoseAmount"))
        {
            await RebuildTableAsync(db, "MedicineSchedule", schedCols, @"
                Id INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
                MedicineId INTEGER NOT NULL,
                ScheduleTime TEXT NOT NULL,
                IsActive INTEGER NOT NULL DEFAULT 1,
                CreatedAt TEXT",
                [
                    ("Id", null), ("MedicineId", "0"), ("ScheduleTime", "''"), ("IsActive", "1"), ("CreatedAt", "NULL")
                ]);
        }

        // The rebuilt table must carry every column the Medicine model maps, otherwise inserts fail
        // until the next launch re-adds them.
        var medCols = await GetColumnsAsync(db, "Medicine");
        if (medCols.Contains("Dosage"))
        {
            await RebuildTableAsync(db, "Medicine", medCols, @"
                Id INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
                Name TEXT NOT NULL,
                IsActive INTEGER NOT NULL DEFAULT 1,
                StockCount REAL NOT NULL DEFAULT 0,
                StockUnit TEXT NOT NULL DEFAULT '',
                PrescribedDays INTEGER NOT NULL DEFAULT 0,
                IsOngoing INTEGER DEFAULT 0,
                SortOrder INTEGER NOT NULL DEFAULT 0,
                ExtensionDays INTEGER DEFAULT 0,
                StartDate TEXT,
                EndDate TEXT,
                Instructions TEXT,
                Notes TEXT,
                CreatedAt TEXT NOT NULL DEFAULT ''",
                [
                    ("Id", null), ("Name", "''"), ("IsActive", "1"), ("StockCount", "0"), ("StockUnit", "''"),
                    ("PrescribedDays", "0"), ("IsOngoing", "0"), ("SortOrder", "0"), ("ExtensionDays", "0"),
                    ("StartDate", "NULL"), ("EndDate", "NULL"), ("Instructions", "NULL"), ("Notes", "NULL"),
                    ("CreatedAt", "''")
                ]);
        }

        // Ensure columns added after the first release exist (handles any prior broken migration)
        var medColsFinal = await GetColumnsAsync(db, "Medicine");
        if (!medColsFinal.Contains("IsOngoing"))
            await db.ExecuteAsync("ALTER TABLE Medicine ADD COLUMN IsOngoing INTEGER DEFAULT 0");
        if (!medColsFinal.Contains("SortOrder"))
            await db.ExecuteAsync("ALTER TABLE Medicine ADD COLUMN SortOrder INTEGER NOT NULL DEFAULT 0");
    }

    // ── v2: Medicine.ExtensionDays, MedicineSchedule.CreatedAt ───────────────────────────────

    private static async Task AddCourseExtensionAndScheduleCreatedAtAsync(SQLiteAsyncConnection db)
    {
        var medCols = await GetColumnsAsync(db, "Medicine");
        if (!medCols.Contains("ExtensionDays"))
            await db.ExecuteAsync("ALTER TABLE Medicine ADD COLUMN ExtensionDays INTEGER DEFAULT 0");
        await db.ExecuteAsync("UPDATE Medicine SET ExtensionDays = 0 WHERE ExtensionDays IS NULL");

        var schedCols = await GetColumnsAsync(db, "MedicineSchedule");
        if (!schedCols.Contains("CreatedAt"))
            await db.ExecuteAsync("ALTER TABLE MedicineSchedule ADD COLUMN CreatedAt TEXT");

        // Backfill from the owning medicine's CreatedAt ("yyyy-MM-ddTHH:mm:ss" → "yyyy-MM-dd HH:mm").
        await db.ExecuteAsync(@"
            UPDATE MedicineSchedule
               SET CreatedAt = (SELECT substr(replace(m.CreatedAt, 'T', ' '), 1, 16)
                                  FROM Medicine m WHERE m.Id = MedicineSchedule.MedicineId)
             WHERE (CreatedAt IS NULL OR CreatedAt = '')
               AND EXISTS (SELECT 1 FROM Medicine m WHERE m.Id = MedicineSchedule.MedicineId
                                                      AND m.CreatedAt IS NOT NULL AND m.CreatedAt <> '')");
    }

    // ── v3: absolute → relative file paths ───────────────────────────────────────────────────

    private static async Task RewriteAbsoluteFilePathsAsync(SQLiteAsyncConnection db, Func<string, string> toStoredPath)
    {
        await RewritePathColumnAsync(db, "MedicalReport", "FilePath", toStoredPath);
        await RewritePathColumnAsync(db, "ReportPage", "FilePath", toStoredPath);
        await RewritePathColumnAsync(db, "DoctorVisit", "PrescriptionImagePath", toStoredPath);
    }

    private static async Task RewritePathColumnAsync(SQLiteAsyncConnection db, string table, string column,
        Func<string, string> toStoredPath)
    {
        if (!(await GetColumnsAsync(db, table)).Contains(column)) return;

        var rows = await db.QueryAsync<PathRow>($"SELECT Id, {column} AS Path FROM {table} WHERE {column} IS NOT NULL AND {column} <> ''");
        var changes = rows
            .Select(r => (r.Id, OldPath: r.Path, NewPath: toStoredPath(r.Path)))
            .Where(c => !string.IsNullOrEmpty(c.NewPath) && c.NewPath != c.OldPath)
            .Select(c => (c.Id, c.NewPath))
            .ToList();
        if (changes.Count == 0) return;

        await db.RunInTransactionAsync(conn =>
        {
            foreach (var (id, newPath) in changes)
                conn.Execute($"UPDATE {table} SET {column} = ? WHERE Id = ?", newPath, id);
        });
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Rebuilds <paramref name="table"/> with <paramref name="createColumns"/>, copying every target column
    /// that exists in the old table (others take their default). Runs in one transaction and drops any
    /// stale <c>_new</c> table left by an earlier crash first.
    /// </summary>
    private static async Task RebuildTableAsync(SQLiteAsyncConnection db, string table, HashSet<string> oldColumns,
        string createColumns, (string Name, string? Default)[] targetColumns)
    {
        var newTable = table + "_new";
        var names = string.Join(", ", targetColumns.Select(c => c.Name));
        var selects = string.Join(", ", targetColumns.Select(c =>
            oldColumns.Contains(c.Name)
                ? (c.Default is null ? c.Name : $"COALESCE({c.Name}, {c.Default})")
                : (c.Default ?? "NULL")));

        await db.ExecuteAsync($"DROP TABLE IF EXISTS {newTable}");
        await db.RunInTransactionAsync(conn =>
        {
            conn.Execute($"CREATE TABLE {newTable} ({createColumns})");
            conn.Execute($"INSERT INTO {newTable} ({names}) SELECT {selects} FROM {table}");
            conn.Execute($"DROP TABLE {table}");
            conn.Execute($"ALTER TABLE {newTable} RENAME TO {table}");
        });
    }

    public static async Task<HashSet<string>> GetColumnsAsync(SQLiteAsyncConnection db, string table)
    {
        var cols = await db.QueryAsync<ColumnInfo>($"PRAGMA table_info({table})");
        return cols.Select(c => c.name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public static async Task<int> GetUserVersionAsync(SQLiteAsyncConnection db) =>
        await db.ExecuteScalarAsync<int>("PRAGMA user_version");

    private static Task SetUserVersionAsync(SQLiteAsyncConnection db, int version) =>
        db.ExecuteAsync($"PRAGMA user_version = {version}");

    private class ColumnInfo { public string name { get; set; } = ""; }
    private class PathRow { public int Id { get; set; } public string Path { get; set; } = ""; }
}
