using HealthTracker.Constants;
using HealthTracker.Helpers;
using HealthTracker.Models;
using HealthTracker.Repositories;
using HealthTracker.Services;
using HealthTracker.Services.Labs;

namespace HealthTracker.Tests.Services;

public class LabServiceTests
{
    /// <summary>Each test gets its own database file: the default in-memory one is shared across tests.</summary>
    private static LabService CreateService(out UserProfileRepository profiles)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ht_labs_{Guid.NewGuid():N}.db");
        var db = new DatabaseService(path);
        profiles = new UserProfileRepository(db);
        return new LabService(new LabResultRepository(db), profiles);
    }

    private static LabService CreateService() => CreateService(out _);

    private static string Today => DateTimeHelper.ToDateString(DateTime.Today);
    private static string DaysAgo(int days) => DateTimeHelper.ToDateString(DateTime.Today.AddDays(-days));

    private static LabEntry Num(string code, double value, string unit = "") => new() { TestCode = code, Value = value, Unit = unit };
    private static LabEntry Word(string code, string text) => new() { TestCode = code, IsQualitative = true, ValueText = text };

    [Fact]
    public async Task AddResults_SnapshotsCatalogRange_AndSharesOneBatch()
    {
        var svc = CreateService();
        var saved = await svc.AddResults([Num("creatinine", 1.5), Num("alt", 30)], Today, "  City Lab ");

        Assert.Equal(2, saved.Count);
        Assert.Single(saved.Select(r => r.BatchId).Distinct());
        Assert.All(saved, r => Assert.Equal("City Lab", r.LabName));

        var creatinine = saved.Single(r => r.TestCode == "creatinine");
        Assert.Equal("mg/dL", creatinine.Unit);
        Assert.Equal(0.6, creatinine.RefLow);
        Assert.Equal(1.3, creatinine.RefHigh);
        Assert.Equal("kidney", creatinine.PanelCode);
        Assert.Equal(LabFlag.High, LabEvaluator.Evaluate(creatinine));
    }

    [Fact]
    public async Task AddResults_UsesSexSpecificRange_FromProfile()
    {
        var svc = CreateService(out var profiles);
        await profiles.SaveAsync(new UserProfile { Sex = UserProfile.Female });

        var saved = await svc.AddResults([Num("hdl", 45)], Today);

        Assert.Equal(50, saved[0].RefLow);
        Assert.Equal(LabFlag.Low, LabEvaluator.Evaluate(saved[0]));
    }

    [Fact]
    public async Task AddResults_AlternativeUnit_StoresValueAsEntered_WithConvertedRange()
    {
        var svc = CreateService();
        var saved = await svc.AddResults([Num("fbs", 6.4, "mmol/l")], Today);

        var r = saved[0];
        Assert.Equal(6.4, r.Value);
        Assert.Equal("mmol/L", r.Unit);
        Assert.Equal(5.5, r.RefHigh!.Value, 1);
        Assert.Equal(LabFlag.High, LabEvaluator.Evaluate(r));
        Assert.Equal(115.3, LabEvaluator.ValueInPrimary(r)!.Value, 1);
    }

    [Fact]
    public async Task AddResults_PrintedRange_OverridesCatalog()
    {
        var svc = CreateService();
        var saved = await svc.AddResults(
            [new LabEntry { TestCode = "alt", Value = 48, RefLow = null, RefHigh = 55 }], Today);

        Assert.Equal(55, saved[0].RefHigh);
        Assert.Equal(LabFlag.Normal, LabEvaluator.Evaluate(saved[0]));
    }

    [Fact]
    public async Task AddResults_Qualitative_StoresCatalogSpellingAndExpectedWord()
    {
        var svc = CreateService();
        var saved = await svc.AddResults([Word("hbsag", "positive"), Word("anti_hbs", "Positive"), Word("blood_group", "o+")], Today);

        var hbsag = saved.Single(r => r.TestCode == "hbsag");
        Assert.Equal("Positive", hbsag.ValueText);
        Assert.Equal("Negative", hbsag.ExpectedText);
        Assert.Equal(LabFlag.Abnormal, LabEvaluator.Evaluate(hbsag));

        // A positive antibody (immunity) and a blood group have no "expected" word and are never flagged.
        Assert.Equal(LabFlag.None, LabEvaluator.Evaluate(saved.Single(r => r.TestCode == "anti_hbs")));
        Assert.Equal("O+", saved.Single(r => r.TestCode == "blood_group").ValueText);
    }

    [Fact]
    public async Task AddResults_LipidPanel_AddsCalculatedValues()
    {
        var svc = CreateService();
        var saved = await svc.AddResults(
            [Num("chol_total", 220), Num("hdl", 45), Num("triglycerides", 180)], Today);

        var ldl = saved.Single(r => r.TestCode == "ldl");
        Assert.Equal(139, ldl.Value);
        Assert.Equal(1, ldl.IsCalculated);
        Assert.StartsWith("Calculated:", ldl.Notes);
        Assert.Equal(LabFlag.High, LabEvaluator.Evaluate(ldl));
        Assert.Contains(saved, r => r.TestCode == "vldl" && r.Value == 36);
        Assert.Contains(saved, r => r.TestCode == "non_hdl" && r.Value == 175);
        Assert.Contains(saved, r => r.TestCode == "chol_hdl_ratio" && r.Value == 4.9);
        Assert.Equal(7, saved.Count);
    }

    [Fact]
    public async Task AddResults_LipidPanelInMmol_ConvertsBeforeCalculating()
    {
        var svc = CreateService();
        // 5.17 mmol/L ≈ 200 mg/dL, 1.29 ≈ 50, 1.69 ≈ 150.
        var saved = await svc.AddResults(
            [Num("chol_total", 5.17, "mmol/L"), Num("hdl", 1.29, "mmol/L"), Num("triglycerides", 1.69, "mmol/L")], Today);

        var ldl = saved.Single(r => r.TestCode == "ldl");
        Assert.Equal("mg/dL", ldl.Unit);
        Assert.InRange(ldl.Value!.Value, 118, 122);
    }

    [Fact]
    public async Task AddResults_Creatinine_AddsEgfr_OnlyWhenProfileHasAgeAndSex()
    {
        var svc = CreateService(out var profiles);
        var without = await svc.AddResults([Num("creatinine", 1.0)], DaysAgo(30));
        Assert.DoesNotContain(without, r => r.TestCode == "egfr");

        await profiles.SaveAsync(new UserProfile
        {
            Sex = UserProfile.Male,
            DateOfBirth = DateTimeHelper.ToDateString(DateTime.Today.AddYears(-50).AddDays(-10)),
        });
        var with = await svc.AddResults([Num("creatinine", 1.0)], Today);

        var egfr = with.Single(r => r.TestCode == "egfr");
        Assert.Equal(92, egfr.Value);
        Assert.Equal(60, egfr.RefLow);
        Assert.Equal(LabFlag.Normal, LabEvaluator.Evaluate(egfr));
    }

    [Fact]
    public async Task AddResults_CustomTest_GetsCustomCodeAndPanel()
    {
        var svc = CreateService();
        var saved = await svc.AddResults(
            [new LabEntry { TestName = "Anti-CCP", Value = 25, Unit = "U/mL", RefHigh = 20, HasCustomRange = true }], Today);

        Assert.Equal("custom:anti-ccp", saved[0].TestCode);
        Assert.Equal(LabCatalog.CustomPanel, saved[0].PanelCode);
        Assert.Equal(LabFlag.High, LabEvaluator.Evaluate(saved[0]));
        Assert.Single(await svc.GetCustomTests());
    }

    [Theory]
    [InlineData(-1.0, "negative")]
    [InlineData(double.NaN, "number")]
    [InlineData(20_000_000.0, "too large")]
    public async Task AddResults_RejectsBadNumbers(double value, string messagePart)
    {
        var svc = CreateService();
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => svc.AddResults([Num("creatinine", value)], Today));
        Assert.Contains(messagePart, ex.Message);
        Assert.Equal(0, await svc.Count());
    }

    [Fact]
    public async Task AddResults_RejectsFutureDate_Duplicates_EmptyAndBadWords()
    {
        var svc = CreateService();
        var tomorrow = DateTimeHelper.ToDateString(DateTime.Today.AddDays(1));

        await Assert.ThrowsAsync<ArgumentException>(() => svc.AddResults([Num("alt", 30)], tomorrow));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.AddResults([Num("alt", 30), Num("alt", 31)], Today));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.AddResults([], Today));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.AddResults([Word("hbsag", "Maybe")], Today));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.AddResults([new LabEntry { TestName = " ", Value = 1 }], Today));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.AddResults([new LabEntry { TestCode = "alt", Value = 30, RefLow = 50, RefHigh = 10 }], Today));
        Assert.Equal(0, await svc.Count());
    }

    [Fact]
    public async Task GetLatestPerTest_ReturnsNewestWithPrevious_InCatalogOrder()
    {
        var svc = CreateService();
        await svc.AddResults([Num("creatinine", 1.0), Num("chol_total", 180)], DaysAgo(90));
        await svc.AddResults([Num("creatinine", 1.4)], DaysAgo(5));

        var latest = await svc.GetLatestPerTest();

        // Lipid tests come before kidney tests in the catalog.
        Assert.Equal(["chol_total", "creatinine"], latest.Select(l => l.Latest.TestCode));
        var creatinine = latest[1];
        Assert.Equal(1.4, creatinine.Latest.Value);
        Assert.Equal(1.0, creatinine.Previous!.Value);
        Assert.Equal(2, creatinine.Count);
        Assert.Equal(LabFlag.High, creatinine.Flag);
        Assert.Null(latest[0].Previous);
    }

    [Fact]
    public async Task GetAttention_OnlyListsTestsWhoseNewestResultIsOff()
    {
        var svc = CreateService();
        await svc.AddResults([Num("alt", 90), Num("creatinine", 1.6), Word("hbsag", "Negative")], DaysAgo(60));
        await svc.AddResults([Num("alt", 30)], DaysAgo(2));          // ALT recovered

        var attention = await svc.GetAttention();

        Assert.Equal(["creatinine"], attention.Select(a => a.Latest.TestCode));
    }

    [Fact]
    public async Task GetBatches_GroupsBySitting_NewestFirst()
    {
        var svc = CreateService();
        await svc.AddResults([Num("alt", 90), Num("ast", 20)], DaysAgo(10), "Lab A");
        await svc.AddResults([Num("tsh", 2.1)], DaysAgo(1), "Lab B");

        var batches = await svc.GetBatches();

        Assert.Equal(2, batches.Count);
        Assert.Equal("Lab B", batches[0].LabName);
        Assert.Equal(2, batches[1].Results.Count);
        Assert.Equal(1, batches[1].AttentionCount);
    }

    [Fact]
    public async Task UpdateResult_ClearsCalculatedMark_AndValidates()
    {
        var svc = CreateService();
        var saved = await svc.AddResults([Num("chol_total", 220), Num("hdl", 45), Num("triglycerides", 180)], Today);
        var ldl = saved.Single(r => r.TestCode == "ldl");

        ldl.Value = 128;
        await svc.UpdateResult(ldl);

        var reloaded = (await svc.GetById(ldl.Id))!;
        Assert.Equal(128, reloaded.Value);
        Assert.Equal(0, reloaded.IsCalculated);
        Assert.Null(reloaded.Notes);

        reloaded.Value = -5;
        await Assert.ThrowsAsync<ArgumentException>(() => svc.UpdateResult(reloaded));
    }

    [Fact]
    public async Task DeleteBatch_And_DetachReport()
    {
        var svc = CreateService();
        var first = await svc.AddResults([Num("alt", 30), Num("ast", 20)], DaysAgo(3), reportId: 7);
        await svc.AddResults([Num("tsh", 2.0)], Today);

        Assert.Equal(2, (await svc.GetForReport(7)).Count);
        await svc.DetachReport(7);
        Assert.Empty(await svc.GetForReport(7));
        Assert.Equal(3, await svc.Count());

        await svc.DeleteBatch(first[0].BatchId);
        Assert.Equal(1, await svc.Count());
    }

    [Fact]
    public async Task GetPreferredUnits_RemembersTheLastEnteredUnit()
    {
        var svc = CreateService();
        await svc.AddResults([Num("fbs", 95, "mg/dL")], DaysAgo(40));
        await svc.AddResults([Num("fbs", 5.4, "mmol/L")], DaysAgo(1));

        var units = await svc.GetPreferredUnits();

        Assert.Equal("mmol/L", units["fbs"]);
    }
}

public class UserProfileTests
{
    [Fact]
    public void AgeOn_CountsCompletedYears()
    {
        var p = new UserProfile { DateOfBirth = "1990-06-15" };
        Assert.Equal(35, p.AgeOn(new DateTime(2026, 6, 14)));
        Assert.Equal(36, p.AgeOn(new DateTime(2026, 6, 15)));
        Assert.Null(new UserProfile().AgeOn(DateTime.Today));
        Assert.Null(new UserProfile { DateOfBirth = "2999-01-01" }.AgeOn(DateTime.Today));
    }

    [Fact]
    public void IsEmpty_UntilAnythingIsFilledIn()
    {
        Assert.True(new UserProfile().IsEmpty);
        Assert.False(new UserProfile { BloodGroup = "O+" }.IsEmpty);
    }
}
