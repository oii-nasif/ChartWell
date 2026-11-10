using HealthTracker.Helpers;

namespace HealthTracker.Tests.Helpers;

public class DateTimeHelperTests
{
    [Fact]
    public void ToDateString_FormatsAsYyyyMmDd()
    {
        var dt = new DateTime(2024, 3, 7);
        Assert.Equal("2024-03-07", DateTimeHelper.ToDateString(dt));
    }

    [Fact]
    public void ToDateTimeString_FormatsWithTime()
    {
        var dt = new DateTime(2024, 3, 7, 14, 30, 5);
        Assert.Equal("2024-03-07T14:30:05", DateTimeHelper.ToDateTimeString(dt));
    }

    [Fact]
    public void ParseDate_ParsesDateOnlyString()
    {
        var result = DateTimeHelper.ParseDate("2024-03-07");
        Assert.Equal(new DateTime(2024, 3, 7), result.Date);
    }

    [Fact]
    public void ParseDateTime_ParsesFullDateTimeString()
    {
        var result = DateTimeHelper.ParseDateTime("2024-03-07T14:30:05");
        Assert.Equal(new DateTime(2024, 3, 7, 14, 30, 5), result);
    }

    [Fact]
    public void ToMonthYearGroup_ReturnsMonthAndYear()
    {
        var result = DateTimeHelper.ToMonthYearGroup("2024-03-07");
        Assert.Equal("March 2024", result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a date")]
    public void ToMonthYearGroup_WithBadInput_DoesNotThrow(string input)
    {
        Assert.Equal("Unknown", DateTimeHelper.ToMonthYearGroup(input));
    }

    [Fact]
    public void TryParse_HandlesDateAndDateTimeStringsAndRejectsEmpty()
    {
        Assert.True(DateTimeHelper.TryParse("2024-03-07", out var d));
        Assert.Equal(new DateTime(2024, 3, 7), d);
        Assert.True(DateTimeHelper.TryParse("2024-03-07T14:30:05", out var dt));
        Assert.Equal(new DateTime(2024, 3, 7, 14, 30, 5), dt);
        Assert.False(DateTimeHelper.TryParse(null, out _));
        Assert.False(DateTimeHelper.TryParse("", out _));
    }

    [Theory]
    [InlineData("2024-03-07", 2024, 3, 7, 0, 0, 0)]
    [InlineData("2024-03-07T14:30:05", 2024, 3, 7, 14, 30, 5)]
    [InlineData("2024-03-07 14:30", 2024, 3, 7, 14, 30, 0)]
    [InlineData("2024-03-07T14:30", 2024, 3, 7, 14, 30, 0)]
    [InlineData("2024-03-07 14:30:05", 2024, 3, 7, 14, 30, 5)]
    [InlineData(" 2024-03-07 ", 2024, 3, 7, 0, 0, 0)]
    public void TryParseStored_AcceptsEveryStoredShape(string input, int y, int m, int d, int h, int mi, int s)
    {
        Assert.True(DateTimeHelper.TryParseStored(input, out var dt));
        Assert.Equal(new DateTime(y, m, d, h, mi, s), dt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a date")]
    [InlineData("07/03/2024 garbage")]
    public void TryParseStored_RejectsGarbage(string? input)
    {
        Assert.False(DateTimeHelper.TryParseStored(input, out _));
    }

    [Fact]
    public void TryParseStored_IsCultureIndependent()
    {
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            // de-DE uses '.' as the time separator and d.M.yyyy dates; stored strings must still parse.
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.True(DateTimeHelper.TryParseStored("2024-03-07T14:30:05", out var dt));
            Assert.Equal(new DateTime(2024, 3, 7, 14, 30, 5), dt);
            Assert.True(DateTimeHelper.TryParseTime("08:30", out var t));
            Assert.Equal(new TimeSpan(8, 30, 0), t);
            Assert.Equal("2024-03-07 14:30", DateTimeHelper.ToDateTimeMinuteString(dt));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData("08:00", 8, 0)]
    [InlineData("8:05", 8, 5)]
    [InlineData("23:59:30", 23, 59)]
    public void TryParseTime_ParsesStoredTimes(string input, int h, int m)
    {
        Assert.True(DateTimeHelper.TryParseTime(input, out var t));
        Assert.Equal(h, t.Hours);
        Assert.Equal(m, t.Minutes);
    }

    [Theory]
    [InlineData("bad")]
    [InlineData("")]
    [InlineData("25:00")]
    public void TryParseTime_RejectsInvalid(string input)
    {
        Assert.False(DateTimeHelper.TryParseTime(input, out _));
    }

    [Fact]
    public void NormalizeStartDate_ProducesMinuteFormatForAnyStoredShape()
    {
        Assert.Equal("2024-03-07 00:00", DateTimeHelper.NormalizeStartDate("2024-03-07"));
        Assert.Equal("2024-03-07 14:30", DateTimeHelper.NormalizeStartDate("2024-03-07T14:30:05"));
        Assert.Equal("2024-03-07 14:30", DateTimeHelper.NormalizeStartDate("2024-03-07 14:30"));
        Assert.Equal("junk", DateTimeHelper.NormalizeStartDate("junk"));
    }

    [Fact]
    public void DatePart_ReturnsFirstTenCharacters()
    {
        Assert.Equal("2024-03-07", DateTimeHelper.DatePart("2024-03-07T14:30:05"));
        Assert.Equal("2024-03-07", DateTimeHelper.DatePart("2024-03-07"));
        Assert.Equal("", DateTimeHelper.DatePart(null));
    }

    [Fact]
    public void ToTimeString_FormatsHHmm()
    {
        Assert.Equal("08:05", DateTimeHelper.ToTimeString(new TimeSpan(8, 5, 0)));
    }

    [Fact]
    public void RoundTrip_DateString_PreservesDate()
    {
        var original = new DateTime(2024, 11, 25);
        var str = DateTimeHelper.ToDateString(original);
        var parsed = DateTimeHelper.ParseDate(str);
        Assert.Equal(original, parsed.Date);
    }

    [Fact]
    public void RoundTrip_DateTimeString_PreservesDateTime()
    {
        var original = new DateTime(2024, 11, 25, 8, 45, 0);
        var str = DateTimeHelper.ToDateTimeString(original);
        var parsed = DateTimeHelper.ParseDateTime(str);
        Assert.Equal(original, parsed);
    }
}
