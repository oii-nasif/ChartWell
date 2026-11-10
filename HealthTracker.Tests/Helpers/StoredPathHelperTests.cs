using HealthTracker.Helpers;

namespace HealthTracker.Tests.Helpers;

public class StoredPathHelperTests
{
    private static readonly string Base = Path.Combine(Path.GetTempPath(), "HealthTrackerTests", "HealthApp");

    [Fact]
    public void ToStoredPath_MakesPathsUnderBaseRelativeWithForwardSlashes()
    {
        var absolute = Path.Combine(Base, "reports", "Lab_2024-01-01_abc12345.pdf");

        Assert.Equal("reports/Lab_2024-01-01_abc12345.pdf", StoredPathHelper.ToStoredPath(absolute, Base));
    }

    [Fact]
    public void ToStoredPath_LeavesRelativeValuesAloneButNormalisesSeparators()
    {
        Assert.Equal("reports/x.pdf", StoredPathHelper.ToStoredPath("reports/x.pdf", Base));
        Assert.Equal("reports/x.pdf", StoredPathHelper.ToStoredPath(@"reports\x.pdf", Base));
        Assert.Equal(string.Empty, StoredPathHelper.ToStoredPath(null, Base));
    }

    [Fact]
    public void ToStoredPath_RerootsLegacyAbsolutePathFromAnotherInstall()
    {
        var legacy = Path.Combine(Path.GetTempPath(), "OtherInstall", "files", "HealthApp", "scans", "Xray_2024-02-02_deadbeef.jpg");

        Assert.Equal("scans/Xray_2024-02-02_deadbeef.jpg", StoredPathHelper.ToStoredPath(legacy, Base));
    }

    [Fact]
    public void ResolvePath_RootsRelativeValuesOnTheBaseFolder()
    {
        var resolved = StoredPathHelper.ResolvePath("reports/x.pdf", Base, _ => false);

        Assert.Equal(Path.Combine(Base, "reports", "x.pdf"), resolved);
    }

    [Fact]
    public void ResolvePath_ReturnsExistingAbsolutePathUnchanged()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "Elsewhere", "HealthApp", "other", "f.png");

        Assert.Equal(absolute, StoredPathHelper.ResolvePath(absolute, Base, p => p == absolute));
    }

    [Fact]
    public void ResolvePath_RerootsMissingAbsolutePathOntoCurrentBase()
    {
        var legacy = Path.Combine(Path.GetTempPath(), "OldInstall", "HealthApp", "other", "f.png");
        var expected = Path.Combine(Base, "other", "f.png");

        Assert.Equal(expected, StoredPathHelper.ResolvePath(legacy, Base, p => p == expected));
        // When the re-rooted candidate does not exist either, the original is returned for the caller's File.Exists check
        Assert.Equal(legacy, StoredPathHelper.ResolvePath(legacy, Base, _ => false));
    }

    [Fact]
    public void RoundTrip_StoredToResolvedToStored_IsStable()
    {
        var absolute = Path.Combine(Base, "prescriptions", "Rx_2024-05-05_0badf00d.jpg");

        var stored = StoredPathHelper.ToStoredPath(absolute, Base);
        var resolved = StoredPathHelper.ResolvePath(stored, Base, _ => false);
        var storedAgain = StoredPathHelper.ToStoredPath(resolved, Base);

        Assert.Equal(absolute, resolved);
        Assert.Equal(stored, storedAgain);
    }

    [Theory]
    [InlineData("Lab", "Lab")]
    [InlineData("X-ray/CT", "X-rayCT")]
    [InlineData("a:b*c?d", "abcd")]
    [InlineData("   ", "file")]
    [InlineData(null, "file")]
    public void SanitizeFileNamePart_StripsInvalidCharacters(string? input, string expected)
    {
        Assert.Equal(expected, StoredPathHelper.SanitizeFileNamePart(input));
    }
}
