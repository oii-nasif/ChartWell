using HealthTracker.Models;
using HealthTracker.ViewModels;

namespace HealthTracker.Tests.ViewModels;

public class MedicineItemTests
{
    private static MedicineItem Build(double doseProgress = 0, int streak = 0, int missed = 0,
        double stockDays = 30, string stockUnit = "Tablet")
        => new()
        {
            Medicine = new Medicine { Name = "Aspirin", StockCount = 10, StockUnit = stockUnit, StartDate = "2024-01-01", CreatedAt = "2024-01-01T00:00:00", IsActive = 1 },
            DoseProgress = doseProgress,
            DoseProgressText = "1 of 2 doses taken today",
            StockDaysRemaining = stockDays,
            Streak = streak,
            MissedThisWeek = missed,
        };

    // ── ProgressColor ────────────────────────────────────────────────────────
    [Fact]
    public void ProgressColor_WhenComplete_IsSuccessToken()
        => Assert.Equal("Success", Build(doseProgress: 1.0).ProgressColor);

    [Fact]
    public void ProgressColor_WhenExceedsOne_IsSuccessToken()
        => Assert.Equal("Success", Build(doseProgress: 1.5).ProgressColor);

    [Fact]
    public void ProgressColor_WhenPartial_IsPrimaryToken()
        => Assert.Equal("Primary", Build(doseProgress: 0.5).ProgressColor);

    [Fact]
    public void ProgressColor_WhenZero_IsOutlineToken()
        => Assert.Equal("Outline", Build(doseProgress: 0).ProgressColor);

    // ── Stock ────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData(30, false, false)]
    [InlineData(5, false, false)]   // AppConstants.LowStockDays
    [InlineData(4.9, true, false)]
    [InlineData(2, true, false)]    // AppConstants.CriticalStockDays
    [InlineData(1.9, true, true)]
    [InlineData(0, true, true)]
    public void StockFlags_FollowDaysRemainingThresholds(double days, bool low, bool critical)
    {
        var item = Build(stockDays: days);
        Assert.Equal(low, item.IsLowStock);
        Assert.Equal(critical, item.IsCriticalStock);
    }

    [Fact]
    public void StockWarningColor_IsDangerWhenCriticalElseWarning()
    {
        Assert.Equal("Danger", Build(stockDays: 1).StockWarningColor);
        Assert.Equal("Warning", Build(stockDays: 3).StockWarningColor);
    }

    [Theory]
    [InlineData(30, "")]
    [InlineData(3, "Low stock")]
    [InlineData(1, "Critical stock")]
    public void StockChipText_FollowsThresholds(double days, string expected)
        => Assert.Equal(expected, Build(stockDays: days).StockChipText);

    [Fact]
    public void StockChipText_WhenNoStock_SaysOutOfStock()
    {
        var item = Build(stockDays: 0);
        item.Medicine.StockCount = 0;
        Assert.Equal("Out of stock", item.StockChipText);
    }

    [Theory]
    [InlineData("Tablet", "ic_medication.png")]
    [InlineData("Drop", "ic_water_drop.png")]
    [InlineData("Injection", "ic_vaccines.png")]
    [InlineData("Unknown", "ic_inventory_2.png")]
    public void StockUnitImage_MapsUnitToMaterialIcon(string unit, string expected)
        => Assert.Equal(expected, Build(stockUnit: unit).StockUnitImage);

    [Theory]
    [InlineData("Tablet", "💊")]
    [InlineData("Drop", "💧")]
    [InlineData("Unknown", "📦")]
    public void StockUnitIcon_MapsUnitToIcon(string unit, string expected)
        => Assert.Equal(expected, Build(stockUnit: unit).StockUnitIcon);

    // ── Streak ───────────────────────────────────────────────────────────────
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ShowStreak_WhenStreakLessThan2_IsFalse(int streak)
        => Assert.False(Build(streak: streak).ShowStreak);

    [Theory]
    [InlineData(2)]
    [InlineData(10)]
    public void ShowStreak_WhenStreakAtLeast2_IsTrue(int streak)
        => Assert.True(Build(streak: streak).ShowStreak);

    [Fact]
    public void StreakText_ContainsStreakCount()
    {
        var item = Build(streak: 5);
        Assert.Contains("5", item.StreakText);
        Assert.Contains("streak", item.StreakText);
    }

    // ── Missed ───────────────────────────────────────────────────────────────
    [Fact]
    public void ShowMissed_WhenZeroMissed_IsFalse()
        => Assert.False(Build(missed: 0).ShowMissed);

    [Fact]
    public void ShowMissed_WhenMissedGreaterThanZero_IsTrue()
        => Assert.True(Build(missed: 2).ShowMissed);

    [Fact]
    public void MissedText_ContainsMissedCount()
    {
        var item = Build(missed: 3);
        Assert.Contains("3", item.MissedText);
        Assert.Contains("missed", item.MissedText);
    }
}
