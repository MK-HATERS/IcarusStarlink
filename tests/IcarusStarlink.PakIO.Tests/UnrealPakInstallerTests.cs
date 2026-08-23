using System.IO.Compression;
using IcarusStarlink.PakIO.Pak;

namespace IcarusStarlink.PakIO.Tests;

public sealed class UnrealPakInstallerTests : IDisposable
{
    private readonly string _appBase = Path.Combine(Path.GetTempPath(), "IcarusStarlinkTests", $"UPak_{Guid.NewGuid():N}");

    public UnrealPakInstallerTests()
    {
        Directory.CreateDirectory(_appBase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_appBase))
        {
            Directory.Delete(_appBase, recursive: true);
        }
    }

    private UnrealPakInstaller CreateInstaller(FakeRunner? runner = null) => new(runner ?? new FakeRunner("usage: UnrealPak ..."), _appBase);

    /// <summary>Builds a Tools\UnrealPak.zip payload with Epic's own relative layout, the way packaging would.</summary>
    private void WritePayload(bool includeExe = true)
    {
        var staging = Path.Combine(_appBase, "payload_staging");
        var win64 = Path.Combine(staging, "Engine", "Binaries", "Win64");
        Directory.CreateDirectory(win64);
        if (includeExe)
        {
            File.WriteAllText(Path.Combine(win64, "UnrealPak.exe"), "exe bytes");
            File.WriteAllText(Path.Combine(win64, "UnrealPak-Core.dll"), "dll bytes");
            File.WriteAllText(Path.Combine(win64, "UnrealPak.version"), """{"MajorVersion":4,"MinorVersion":27,"PatchVersion":0}""");
        }

        Directory.CreateDirectory(Path.Combine(_appBase, "Tools"));
        ZipFile.CreateFromDirectory(staging, Path.Combine(_appBase, "Tools", "UnrealPak.zip"));
        Directory.Delete(staging, recursive: true);
    }

    [Fact]
    public async Task Verify_NoFileAtPath_ReportsMissing()
    {
        var result = await CreateInstaller().VerifyAsync(Path.Combine(_appBase, "nowhere", "UnrealPak.exe"));

        Assert.Equal(UnrealPakHealth.Missing, result.Health);
    }

    [Fact]
    public async Task Verify_RunnableExe_ReportsOkWithEngineVersion()
    {
        WritePayload();
        var installer = CreateInstaller();
        var exePath = await installer.InstallAsync();

        var result = await installer.VerifyAsync(exePath);

        Assert.Equal(UnrealPakHealth.Ok, result.Health);
        Assert.Equal("4.27.0", result.EngineVersion);
    }

    [Fact]
    public async Task Verify_ExeThatWontStart_ReportsBroken()
    {
        // The real corruption case: the exe file exists but a sibling DLL is gone, so launching it
        // fails — which File.Exists alone can never see.
        WritePayload();
        var installer = new UnrealPakInstaller(new FakeRunner(throwOnRun: true), _appBase);
        var exePath = await installer.InstallAsync();

        var result = await installer.VerifyAsync(exePath);

        Assert.Equal(UnrealPakHealth.Broken, result.Health);
        Assert.Equal("4.27.0", result.EngineVersion);
    }

    [Fact]
    public async Task Install_NoPayload_ThrowsWithGuidanceRatherThanFailingObscurely()
    {
        var installer = CreateInstaller();

        Assert.False(installer.PayloadAvailable);
        var ex = await Assert.ThrowsAsync<FileNotFoundException>(() => installer.InstallAsync());
        Assert.Contains("locate an existing UnrealPak.exe", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Install_ExtractsPayloadNextToApp()
    {
        WritePayload();
        var installer = CreateInstaller();

        var exePath = await installer.InstallAsync();

        Assert.True(installer.PayloadAvailable);
        Assert.Equal(installer.InstalledExePath, exePath);
        Assert.True(File.Exists(exePath));
        Assert.StartsWith(Path.Combine(_appBase, "Tools", "UnrealPak"), exePath);
    }

    [Fact]
    public async Task Reinstall_RemovesStaleFilesRatherThanExtractingOverThem()
    {
        // Reinstall's whole purpose is repair — a stray/corrupt extra file must not survive it.
        WritePayload();
        var installer = CreateInstaller();
        await installer.InstallAsync();
        var stalePath = Path.Combine(Path.GetDirectoryName(installer.InstalledExePath)!, "stale_corrupt.dll");
        File.WriteAllText(stalePath, "junk");

        await installer.InstallAsync();

        Assert.False(File.Exists(stalePath));
        Assert.True(File.Exists(installer.InstalledExePath));
    }

    [Fact]
    public async Task Install_MispackagedPayload_SaysSoClearly()
    {
        WritePayload(includeExe: false);
        var installer = CreateInstaller();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => installer.InstallAsync());
        Assert.Contains("mis-packaged", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FakeRunner(string output = "", bool throwOnRun = false) : IProcessRunner
    {
        public Task<ProcessRunResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default) =>
            throwOnRun
                ? throw new System.ComponentModel.Win32Exception("The specified module could not be found.")
                : Task.FromResult(new ProcessRunResult(1, output, ""));
    }
}
