using System.IO;
using System.IO.Compression;
using IcarusStarlink.App.Services;
using IcarusStarlink.Catalog.AppUpdate;

namespace IcarusStarlink.App.Tests.Services;

/// <summary>
/// The file work of the in-app update. Every call passes a busyNow that stops it right before the
/// updater would start: the test bin holds a real IcarusStarlink.Updater.exe.
/// </summary>
public sealed class AppUpdateInstallerTests : IDisposable
{
    private const string StopBeforeTheUpdater = "test: stop before the updater starts";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));

    public AppUpdateInstallerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string Zip(params string[] entries)
    {
        var zipPath = Path.Combine(_root, "update.zip");
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var entry in entries)
        {
            using var writer = new StreamWriter(archive.CreateEntry(entry).Open());
            writer.Write("bytes");
        }

        return zipPath;
    }

    private static readonly AppUpdateRelease Release = new("9.9.9", "", "");

    /// <summary>
    /// The updater copies every file of the new build over the install: a release zip made from a
    /// folder the app had run in would replace each user's settings.json with the one in the zip.
    /// </summary>
    [Fact]
    public async Task InstallFromZip_ABuildCarryingSettingsJson_IsRefusedBeforeAnythingIsStaged()
    {
        var zip = Zip("IcarusStarlink/IcarusStarlink.exe", "IcarusStarlink/settings.json");

        var problem = await AppUpdateInstaller.InstallFromZipAsync(Release, zip, _root, _ => { }, busyNow: () => StopBeforeTheUpdater);

        Assert.NotNull(problem);
        Assert.Contains("settings.json", problem);
        Assert.False(Directory.Exists(Path.Combine(_root, "updater")));
    }

    [Fact]
    public async Task InstallFromZip_ABuildCarryingALogsFolder_IsRefused()
    {
        var zip = Zip("IcarusStarlink/IcarusStarlink.exe", "IcarusStarlink/Logs/icarusstarlink-20261101.log");

        var problem = await AppUpdateInstaller.InstallFromZipAsync(Release, zip, _root, _ => { }, busyNow: () => StopBeforeTheUpdater);

        Assert.NotNull(problem);
        Assert.Contains("Logs", problem);
        Assert.False(Directory.Exists(Path.Combine(_root, "updater")));
    }

    /// <summary>
    /// Cancel (or Esc, or the window's close box) while the update unpacked was ignored: the
    /// unpacking took no token, the updater was started and Starlink closed anyway.
    /// </summary>
    [Fact]
    public async Task InstallFromZip_CancelledWhileUnpacking_ThrowsAndStartsNothing()
    {
        // No IcarusStarlink.exe: even code that ignores the cancel returns before staging anything.
        var zip = Zip("IcarusStarlink/readme.txt");
        using var cancel = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AppUpdateInstaller.InstallFromZipAsync(Release, zip, _root, step =>
        {
            if (step == AppUpdateStep.Unpack)
            {
                cancel.Cancel();
            }
        }, busyNow: () => StopBeforeTheUpdater, skipOldExe: false, cancel.Token));

        Assert.False(Directory.Exists(Path.Combine(_root, "updater")));
    }

    [Fact]
    public async Task InstallFromZip_ANormalBuild_PassesTheDataCheck()
    {
        var zip = Zip("IcarusStarlink/IcarusStarlink.exe", "IcarusStarlink/Uninstall.bat", "IcarusStarlink/e_sqlite3.dll", "IcarusStarlink/runtimes/win-x64/native/x.dll");

        var problem = await AppUpdateInstaller.InstallFromZipAsync(Release, zip, _root, _ => { }, busyNow: () => StopBeforeTheUpdater);

        Assert.NotNull(problem);
        Assert.DoesNotContain("own data", problem);
    }
}
