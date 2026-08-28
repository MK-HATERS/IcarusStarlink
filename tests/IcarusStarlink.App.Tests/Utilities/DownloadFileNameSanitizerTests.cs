using IcarusStarlink.App.Utilities;

namespace IcarusStarlink.App.Tests.Utilities;

public class DownloadFileNameSanitizerTests
{
    [Fact]
    public void Sanitize_OrdinaryFileName_IsUnchanged()
    {
        Assert.Equal("zenProgression-290-1-0-1.zip", DownloadFileNameSanitizer.Sanitize("zenProgression-290-1-0-1.zip"));
    }

    [Fact]
    public void Sanitize_RelativeTraversal_StripsDownToTheBareFileName()
    {
        // A CDN's own Content-Disposition header (or URL path) is untrusted — this is the
        // exact shape that would otherwise let Path.Combine write outside the intended folder.
        Assert.Equal("evil.exe", DownloadFileNameSanitizer.Sanitize("../../../evil.exe"));
    }

    [Fact]
    public void Sanitize_WindowsRelativeTraversal_StripsDownToTheBareFileName()
    {
        Assert.Equal("evil.exe", DownloadFileNameSanitizer.Sanitize(@"..\..\evil.exe"));
    }

    [Fact]
    public void Sanitize_RootedAbsolutePath_StripsDownToTheBareFileName()
    {
        Assert.Equal("evil.exe", DownloadFileNameSanitizer.Sanitize(@"C:\Windows\System32\evil.exe"));
    }

    [Fact]
    public void Sanitize_EmbeddedColon_IsReplacedNotLeftAsAnAlternateDataStreamMarker()
    {
        var result = DownloadFileNameSanitizer.Sanitize("readme.txt:hidden.exe");

        Assert.DoesNotContain(':', result);
    }

    [Fact]
    public void Sanitize_TrailingDotsAndSpaces_AreTrimmed()
    {
        Assert.Equal("mod.zip", DownloadFileNameSanitizer.Sanitize("mod.zip. "));
    }

    [Fact]
    public void Sanitize_ReservedWindowsDeviceName_IsNotReturnedAsIs()
    {
        var result = DownloadFileNameSanitizer.Sanitize("CON");

        Assert.NotEqual("CON", result);
    }

    [Fact]
    public void Sanitize_ReservedWindowsDeviceNameWithExtension_IsNotReturnedAsIs()
    {
        var result = DownloadFileNameSanitizer.Sanitize("con.txt");

        Assert.NotEqual("con.txt", result);
    }

    [Fact]
    public void Sanitize_EmptyAfterSanitizing_ProducesANonEmptyFallbackName()
    {
        var result = DownloadFileNameSanitizer.Sanitize("../../");

        Assert.False(string.IsNullOrWhiteSpace(result));
    }
}
