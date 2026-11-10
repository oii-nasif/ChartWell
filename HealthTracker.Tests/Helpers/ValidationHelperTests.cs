using HealthTracker.Constants;
using HealthTracker.Helpers;

namespace HealthTracker.Tests.Helpers;

public class ValidationHelperTests
{
    // ── Weight ──────────────────────────────────────────────────────────────
    [Theory]
    [InlineData(70.0)]
    [InlineData(1.0)]
    [InlineData(500.0)]
    public void Weight_ValidRange_ReturnsTrue(double value)
    {
        var result = ValidationHelper.IsMetricValueValid(MetricTypes.Weight, value, null, out var error);
        Assert.True(result);
        Assert.Empty(error);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(501.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Weight_OutOfRangeOrNotANumber_ReturnsFalse(double value)
    {
        var result = ValidationHelper.IsMetricValueValid(MetricTypes.Weight, value, null, out var error);
        Assert.False(result);
        Assert.NotEmpty(error);
    }

    // ── Blood Pressure ───────────────────────────────────────────────────────
    [Fact]
    public void BloodPressure_ValidBothValues_ReturnsTrue()
    {
        var result = ValidationHelper.IsMetricValueValid(MetricTypes.BloodPressure, 120, 80, out var error);
        Assert.True(result);
        Assert.Empty(error);
    }

    [Fact]
    public void BloodPressure_MissingDiastolic_ReturnsFalse()
    {
        var result = ValidationHelper.IsMetricValueValid(MetricTypes.BloodPressure, 120, null, out var error);
        Assert.False(result);
        Assert.Contains("Diastolic", error);
    }

    [Fact]
    public void BloodPressure_SystolicOutOfRange_ReturnsFalse()
    {
        var result = ValidationHelper.IsMetricValueValid(MetricTypes.BloodPressure, 400, 80, out var error);
        Assert.False(result);
        Assert.Contains("Systolic", error);
    }

    [Fact]
    public void BloodPressure_DiastolicOutOfRange_ReturnsFalse()
    {
        var result = ValidationHelper.IsMetricValueValid(MetricTypes.BloodPressure, 120, 250, out var error);
        Assert.False(result);
        Assert.Contains("Diastolic", error);
    }

    [Fact]
    public void BloodPressure_NaNDiastolic_ReturnsFalse()
    {
        var result = ValidationHelper.IsMetricValueValid(MetricTypes.BloodPressure, 120, double.NaN, out _);
        Assert.False(result);
    }

    // ── TryParseDouble ───────────────────────────────────────────────────────
    [Theory]
    [InlineData("72.5", 72.5)]
    [InlineData("72,5", 72.5)]
    [InlineData(" 10 ", 10)]
    [InlineData("0", 0)]
    public void TryParseDouble_AcceptsDotAndCommaDecimals(string text, double expected)
    {
        Assert.True(ValidationHelper.TryParseDouble(text, out var value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData(null)]
    public void TryParseDouble_RejectsNonNumbers(string? text)
    {
        Assert.False(ValidationHelper.TryParseDouble(text, out _));
    }

    // ── File type ────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("pdf")]
    [InlineData("jpg")]
    [InlineData("jpeg")]
    [InlineData("png")]
    [InlineData("PDF")]
    [InlineData(".pdf")]
    public void FileType_ValidExtension_ReturnsTrue(string ext)
    {
        var result = ValidationHelper.IsValidFileType(ext, out var error);
        Assert.True(result);
        Assert.Empty(error);
    }

    [Theory]
    [InlineData("exe")]
    [InlineData("docx")]
    [InlineData("txt")]
    public void FileType_InvalidExtension_ReturnsFalse(string ext)
    {
        var result = ValidationHelper.IsValidFileType(ext, out var error);
        Assert.False(result);
        Assert.NotEmpty(error);
    }

    // ── PIN ──────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("1234")]
    [InlineData("123456")]
    [InlineData("9999")]
    public void Pin_ValidPin_ReturnsTrue(string pin)
    {
        var result = ValidationHelper.IsValidPin(pin, out var error);
        Assert.True(result);
        Assert.Empty(error);
    }

    [Fact]
    public void Pin_TooShort_ReturnsFalse()
    {
        var result = ValidationHelper.IsValidPin("123", out var error);
        Assert.False(result);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void Pin_TooLong_ReturnsFalse()
    {
        var result = ValidationHelper.IsValidPin("1234567", out var error);
        Assert.False(result);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void Pin_NonDigitChars_ReturnsFalse()
    {
        var result = ValidationHelper.IsValidPin("12ab", out var error);
        Assert.False(result);
        Assert.Contains("digit", error);
    }
}
