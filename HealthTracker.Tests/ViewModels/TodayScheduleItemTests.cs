using HealthTracker.ViewModels;

namespace HealthTracker.Tests.ViewModels;

public class TodayScheduleItemTests
{
    // ── Logged + Taken ────────────────────────────────────────────────────────
    [Fact]
    public void WhenLoggedAndTaken_StatusText_IsTaken()
    {
        var item = new TodayScheduleItem { IsLogged = true, WasTaken = true };
        Assert.Equal("Taken", item.StatusText);
    }

    [Fact]
    public void WhenLoggedAndTaken_StatusIcon_IsCheckIcon()
    {
        var item = new TodayScheduleItem { IsLogged = true, WasTaken = true };
        Assert.Equal("ic_check_circle.png", item.StatusIcon);
    }

    [Fact]
    public void WhenLoggedAndTaken_IsDone_AndCannotTakeAgain()
    {
        var item = new TodayScheduleItem { IsLogged = true, WasTaken = true };
        Assert.True(item.IsDone);
        Assert.False(item.CanTake);
        Assert.Equal(TodayDoseGroupKind.Done, item.GroupKind);
    }

    [Fact]
    public void WhenLoggedAndTaken_StatusColor_IsSuccessToken()
    {
        var item = new TodayScheduleItem { IsLogged = true, WasTaken = true };
        Assert.Equal("Success", item.StatusColor);
    }

    [Fact]
    public void WhenLoggedAndTaken_TimeUntilText_IsTaken()
    {
        var item = new TodayScheduleItem { IsLogged = true, WasTaken = true };
        Assert.Equal("Taken", item.TimeUntilText);
    }

    [Fact]
    public void WhenLoggedAndTaken_CanActNow_IsFalse()
    {
        var item = new TodayScheduleItem { IsLogged = true, WasTaken = true };
        Assert.False(item.CanActNow);
    }

    // ── Logged + Missed (auto-logged, not taken) ─────────────────────────────────────────────────────
    [Fact]
    public void WhenLoggedAndMissed_StatusText_IsMissed()
    {
        var item = new TodayScheduleItem { IsLogged = true, WasTaken = false };
        Assert.Equal("Missed", item.StatusText);
    }

    [Fact]
    public void WhenLoggedAndMissed_StatusIcon_IsCancelIcon()
    {
        var item = new TodayScheduleItem { IsLogged = true, WasTaken = false };
        Assert.Equal("ic_cancel.png", item.StatusIcon);
    }

    [Fact]
    public void WhenLoggedAndMissed_CanStillTake_ToFlipTheLog()
    {
        var item = new TodayScheduleItem { IsLogged = true, WasTaken = false };
        Assert.True(item.IsMissed);
        Assert.True(item.CanTake);
        Assert.Equal(TodayDoseGroupKind.Done, item.GroupKind);
    }

    [Fact]
    public void WhenLoggedAndMissed_StatusColor_IsDangerToken()
    {
        var item = new TodayScheduleItem { IsLogged = true, WasTaken = false };
        Assert.Equal("Danger", item.StatusColor);
    }

    [Fact]
    public void WhenLoggedAndMissed_TimeUntilText_IsMissed()
    {
        var item = new TodayScheduleItem { IsLogged = true, WasTaken = false };
        Assert.Equal("Missed", item.TimeUntilText);
    }

    [Fact]
    public void WhenLoggedAndMissed_CanActNow_IsFalse()
    {
        var item = new TodayScheduleItem { IsLogged = true, WasTaken = false };
        Assert.False(item.CanActNow);
    }

    // ── Invalid schedule time ─────────────────────────────────────────────────
    [Fact]
    public void WhenInvalidScheduleTime_StatusText_IsUpcoming()
    {
        var item = new TodayScheduleItem { ScheduleTime = "not-a-time" };
        Assert.Equal("Upcoming", item.StatusText);
    }

    [Fact]
    public void WhenInvalidScheduleTime_CanActNow_IsFalse()
    {
        var item = new TodayScheduleItem { ScheduleTime = "not-a-time" };
        Assert.False(item.CanActNow);
    }

    [Fact]
    public void WhenInvalidScheduleTime_TimeUntilText_ReturnsScheduleTime()
    {
        var item = new TodayScheduleItem { ScheduleTime = "bad" };
        Assert.Equal("bad", item.TimeUntilText);
    }

    [Fact]
    public void WhenInvalidScheduleTime_IsOverdue_IsFalse_AndGroupIsLater()
    {
        var item = new TodayScheduleItem { ScheduleTime = "bad" };
        Assert.False(item.IsOverdue);
        Assert.Equal(TodayDoseGroupKind.Later, item.GroupKind);
    }

    // ── Overdue / due-now windows ─────────────────────────────────────────────
    [Fact]
    public void WhenSlotIsMoreThanTwoHoursAgo_IsOverdue_AndGroupedDueNow()
    {
        var threeHoursAgo = DateTime.Now.AddHours(-3);
        if (threeHoursAgo.Date != DateTime.Today) return; // not meaningful just after midnight
        var item = new TodayScheduleItem { ScheduleTime = threeHoursAgo.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) };
        Assert.True(item.IsOverdue);
        Assert.False(item.CanActNow);
        Assert.True(item.CanTake);
        Assert.Equal("Overdue", item.StatusText);
        Assert.Equal("Warning", item.StatusColor);
        Assert.Equal(TodayDoseGroupKind.DueNow, item.GroupKind);
    }

    [Fact]
    public void WhenSlotIsFarInTheFuture_GroupedLater()
    {
        var later = DateTime.Now.AddHours(5);
        if (later.Date != DateTime.Today) return;
        var item = new TodayScheduleItem { ScheduleTime = later.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) };
        Assert.False(item.IsOverdue);
        Assert.False(item.CanActNow);
        Assert.Equal("Upcoming", item.StatusText);
        Assert.Equal(TodayDoseGroupKind.Later, item.GroupKind);
    }

    // ── IsLogged setter fires PropertyChanged ────────────────────────────────
    [Fact]
    public void IsLogged_WhenSet_FiresPropertyChangedForDependentProps()
    {
        var item = new TodayScheduleItem { ScheduleTime = "08:00" };
        var raised = new List<string>();
        item.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        item.IsLogged = true;

        Assert.Contains(nameof(TodayScheduleItem.IsLogged), raised);
        Assert.Contains(nameof(TodayScheduleItem.CanActNow), raised);
        Assert.Contains(nameof(TodayScheduleItem.StatusText), raised);
        Assert.Contains(nameof(TodayScheduleItem.StatusIcon), raised);
        Assert.Contains(nameof(TodayScheduleItem.StatusColor), raised);
        Assert.Contains(nameof(TodayScheduleItem.TimeUntilText), raised);
        Assert.Contains(nameof(TodayScheduleItem.IsDone), raised);
        Assert.Contains(nameof(TodayScheduleItem.CanTake), raised);
        Assert.Contains(nameof(TodayScheduleItem.GroupKind), raised);
    }

    // ── Out-of-stock is a soft warning ───────────────────────────────────────
    [Fact]
    public void IsOutOfStock_DoesNotBlockCanActNow_AndRaisesWarningProps()
    {
        var item = new TodayScheduleItem { ScheduleTime = DateTime.Now.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) };
        var raised = new List<string>();
        item.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        item.IsOutOfStock = true;

        Assert.True(item.CanActNow);
        Assert.True(item.HasInsufficientStock);
        Assert.True(item.ShowOutOfStockWarning);
        Assert.Contains(nameof(TodayScheduleItem.IsOutOfStock), raised);
        Assert.Contains(nameof(TodayScheduleItem.HasInsufficientStock), raised);
        Assert.Contains(nameof(TodayScheduleItem.ShowOutOfStockWarning), raised);

        item.IsLogged = true;
        Assert.False(item.ShowOutOfStockWarning);
    }

    [Theory]
    [InlineData("Tablet", "💊")]
    [InlineData("Syrup", "🧴")]
    [InlineData("Unknown", "📦")]
    public void StockUnitIcon_UsesSharedHelper(string unit, string expected)
    {
        var item = new TodayScheduleItem { StockUnit = unit };
        Assert.Equal(expected, item.StockUnitIcon);
        Assert.Equal(HealthTracker.Helpers.StockUnitIcons.For(unit), item.StockUnitIcon);
    }

    // ── RefreshWindow fires all props ─────────────────────────────────────────
    [Fact]
    public void RefreshWindow_FiresAllComputedPropertyChangedEvents()
    {
        var item = new TodayScheduleItem { ScheduleTime = "09:00" };
        var raised = new List<string>();
        item.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        item.RefreshWindow();

        Assert.Contains(nameof(TodayScheduleItem.CanActNow), raised);
        Assert.Contains(nameof(TodayScheduleItem.StatusText), raised);
        Assert.Contains(nameof(TodayScheduleItem.StatusIcon), raised);
        Assert.Contains(nameof(TodayScheduleItem.StatusColor), raised);
        Assert.Contains(nameof(TodayScheduleItem.TimeUntilText), raised);
    }
}
