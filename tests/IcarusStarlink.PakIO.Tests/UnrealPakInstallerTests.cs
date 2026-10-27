using IcarusStarlink.PakIO.Pak;

namespace IcarusStarlink.PakIO.Tests;

public sealed class UnrealPakInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "UnrealPakTrustTests", Guid.NewGuid().ToString("N"));

    public UnrealPakInstallerTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Install_RefusesWithoutNetworkExecutionOrChanges_EvenWithLegacyPayload(bool legacyPayload)
    {
        var runner = new RecordingRunner();
        var installer = new UnrealPakInstaller(runner, _root);
        Directory.CreateDirectory(Path.GetDirectoryName(installer.InstalledExePath)!);
        await File.WriteAllTextAsync(installer.InstalledExePath, "existing tool");
        if (legacyPayload)
            await File.WriteAllTextAsync(Path.Combine(_root, "Tools", "UnrealPak.zip"), "untrusted payload");

        Assert.False(installer.PayloadAvailable);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => installer.InstallAsync());

        Assert.Contains("Epic-sourced or locally built", error.Message);
        Assert.Equal(0, runner.Calls);
        Assert.Equal("existing tool", await File.ReadAllTextAsync(installer.InstalledExePath));
        Assert.False(Directory.EnumerateDirectories(_root, "*_partial_*", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task Verify_MissingFile_DoesNotExecute()
    {
        var runner = new RecordingRunner();
        var result = await new UnrealPakInstaller(runner, _root).VerifyAsync(Path.Combine(_root, "missing.exe"));
        Assert.Equal(UnrealPakHealth.Missing, result.Health);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task Verify_SelectedFile_RunsOperationalCheckAndReadsVersion()
    {
        var runner = new RecordingRunner();
        var path = Path.Combine(_root, "UnrealPak.exe");
        await File.WriteAllTextAsync(path, "selected tool");
        await File.WriteAllTextAsync(Path.Combine(_root, "UnrealPak.version"), """{"MajorVersion":4,"MinorVersion":27,"PatchVersion":2}""");
        var result = await new UnrealPakInstaller(runner, _root).VerifyAsync(path);
        Assert.Equal(UnrealPakHealth.Ok, result.Health);
        Assert.Equal("4.27.2", result.EngineVersion);
        Assert.Equal(1, runner.Calls);
    }

    private sealed class RecordingRunner : IProcessRunner
    {
        public int Calls;
        public Task<ProcessRunResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ProcessRunResult(1, "usage", ""));
        }
    }

}
