using System.IO;
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

    private static string CreateTempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void ResolveUniqueFileName_NothingAtThatPathYet_ReturnsTheCandidateUnchanged()
    {
        var dir = CreateTempDirectory();

        var result = DownloadFileNameSanitizer.ResolveUniqueFileName(dir, "mod.zip", modId: 1, fileId: 1, _ => false);

        Assert.Equal("mod.zip", result);
    }

    /// <summary>
    /// Regression guard: DownloadsViewModel used to derive the local file name purely from the CDN's
    /// Content-Disposition header, with no per-(ModId, FileId) uniqueness at all — two completely
    /// unrelated mods whose CDN files happened to share a literal name (a generic "main.zip") would
    /// silently overwrite each other's already-downloaded file on disk while both got their own
    /// PendingDownloadEntry pointing at that same path.
    /// </summary>
    [Fact]
    public void ResolveUniqueFileName_FileExistsAndDoesNotBelongToThisDownload_DisambiguatesWithModAndFileId()
    {
        var dir = CreateTempDirectory();
        File.WriteAllText(Path.Combine(dir, "mod.zip"), "someone else's download");

        var result = DownloadFileNameSanitizer.ResolveUniqueFileName(dir, "mod.zip", modId: 42, fileId: 7, _ => false);

        Assert.Equal("mod_42-7.zip", result);
    }

    [Fact]
    public void ResolveUniqueFileName_FileExistsButBelongsToThisSamePairsOwnPriorDownload_ReturnsTheCandidateUnchanged()
    {
        var dir = CreateTempDirectory();
        File.WriteAllText(Path.Combine(dir, "mod.zip"), "an earlier download of this exact mod file");

        var result = DownloadFileNameSanitizer.ResolveUniqueFileName(dir, "mod.zip", modId: 42, fileId: 7, _ => true);

        Assert.Equal("mod.zip", result);
    }
}
