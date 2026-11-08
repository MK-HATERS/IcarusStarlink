using System.Diagnostics;
using System.IO;
using IcarusStarlink.App.Services;

namespace IcarusStarlink.App.Tests.Services;

/// <summary>How a running server's exe path is matched to a profile's install folder.</summary>
public sealed class DedicatedServerProcessServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            // The junction goes first, on its own: deleting it removes only the link.
            var link = Path.Combine(_root, "Link");
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }

            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void IsUnderInstall_MatchesAnyOfTheRoots_AndNotAFolderThatOnlyStartsTheSame()
    {
        Assert.True(DedicatedServerProcessService.IsUnderInstall(
            @"D:\Real\IcarusServer\Icarus\Binaries\Win64\IcarusServer-Win64-Shipping.exe", [@"C:\Link\IcarusServer\", @"D:\Real\IcarusServer\"]));
        Assert.False(DedicatedServerProcessService.IsUnderInstall(@"D:\Real\IcarusServer2\x.exe", [@"D:\Real\IcarusServer\"]));
    }

    /// <summary>
    /// Regression: Windows reports a running exe by its final path (junctions resolved), and the
    /// install folder was compared as typed, so a server adopted through a junction never counted
    /// as running: Start said it "didn't appear" and Stop stayed greyed out.
    /// </summary>
    [Fact]
    public void InstallRoots_OfAFolderReachedThroughAJunction_IncludeTheRealFolder()
    {
        var real = Path.Combine(_root, "Real", "IcarusServer");
        var exeFolder = Path.Combine(real, "Icarus", "Binaries", "Win64");
        Directory.CreateDirectory(exeFolder);
        var link = Path.Combine(_root, "Link");
        if (!MakeJunction(link, Path.Combine(_root, "Real")))
        {
            return; // mklink isn't available here; IsUnderInstall's own test still runs
        }

        var roots = DedicatedServerProcessService.InstallRoots(Path.Combine(link, "IcarusServer"));

        // The exe as Windows reports it: by its final path, which is also the long form when TEMP
        // is an 8.3 short path (C:\Users\JAMES~1\...).
        var reportedExe = Path.Combine(DedicatedServerProcessService.ResolveFinalFolder(exeFolder)!, "IcarusServer-Win64-Shipping.exe");
        Assert.True(DedicatedServerProcessService.IsUnderInstall(reportedExe, roots));
    }

    [Fact]
    public void ResolveFinalFolder_OfAPlainFolder_IsThatFolder_AndNullWhenMissing()
    {
        Directory.CreateDirectory(_root);

        // Compared by the folder's own name and existence, not the whole path: the final path is
        // the long form, while TEMP can be an 8.3 short path (C:\Users\JAMES~1\...).
        var final = DedicatedServerProcessService.ResolveFinalFolder(_root);
        Assert.NotNull(final);
        Assert.True(Directory.Exists(final));
        Assert.Equal(Path.GetFileName(_root), Path.GetFileName(final.TrimEnd('\\')), ignoreCase: true);
        Assert.Null(DedicatedServerProcessService.ResolveFinalFolder(Path.Combine(_root, "missing")));
    }

    private static bool MakeJunction(string link, string target)
    {
        try
        {
            using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (mklink is null)
            {
                return false;
            }

            mklink.StandardOutput.ReadToEnd();
            mklink.StandardError.ReadToEnd();
            mklink.WaitForExit();
            return mklink.ExitCode == 0 && Directory.Exists(link);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
