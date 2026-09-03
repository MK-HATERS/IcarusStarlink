using System.IO.Compression;
using System.Net;
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

    private UnrealPakInstaller CreateInstaller(FakeRunner? runner = null, HttpMessageHandler? httpHandler = null) =>
        new(runner ?? new FakeRunner("usage: UnrealPak ..."), new HttpClient(httpHandler ?? new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))), _appBase);

    /// <summary>Builds an UnrealPak payload zip with Epic's own relative layout, the way packaging would — as bytes, since both the local Tools\UnrealPak.zip path and the fake remote download response need the exact same shape.</summary>
    private static byte[] BuildPayloadZipBytes(bool includeExe = true)
    {
        var staging = Path.Combine(Path.GetTempPath(), "IcarusStarlinkTests", $"payload_staging_{Guid.NewGuid():N}");
        var win64 = Path.Combine(staging, "Engine", "Binaries", "Win64");
        Directory.CreateDirectory(win64);
        if (includeExe)
        {
            File.WriteAllText(Path.Combine(win64, "UnrealPak.exe"), "exe bytes");
            File.WriteAllText(Path.Combine(win64, "UnrealPak-Core.dll"), "dll bytes");
            File.WriteAllText(Path.Combine(win64, "UnrealPak.version"), """{"MajorVersion":4,"MinorVersion":27,"PatchVersion":0}""");
        }

        var zipPath = Path.Combine(Path.GetTempPath(), "IcarusStarlinkTests", $"payload_{Guid.NewGuid():N}.zip");
        ZipFile.CreateFromDirectory(staging, zipPath);
        Directory.Delete(staging, recursive: true);
        try
        {
            return File.ReadAllBytes(zipPath);
        }
        finally
        {
            File.Delete(zipPath);
        }
    }

    /// <summary>Writes a local Tools\UnrealPak.zip payload — the offline, bundled-in-the-release-zip path InstallAsync prefers when present.</summary>
    private void WritePayload(bool includeExe = true)
    {
        Directory.CreateDirectory(Path.Combine(_appBase, "Tools"));
        File.WriteAllBytes(Path.Combine(_appBase, "Tools", "UnrealPak.zip"), BuildPayloadZipBytes(includeExe));
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
        var installer = CreateInstaller(new FakeRunner(throwOnRun: true));
        var exePath = await installer.InstallAsync();

        var result = await installer.VerifyAsync(exePath);

        Assert.Equal(UnrealPakHealth.Broken, result.Health);
        Assert.Equal("4.27.0", result.EngineVersion);
    }

    [Fact]
    public async Task Install_ExtractsLocalPayloadNextToApp()
    {
        WritePayload();
        var installer = CreateInstaller();

        var exePath = await installer.InstallAsync();

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
    public async Task Install_MispackagedLocalPayload_SaysSoClearly()
    {
        WritePayload(includeExe: false);
        var installer = CreateInstaller();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => installer.InstallAsync());
        Assert.Contains("mis-packaged", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Install_NoLocalPayload_DownloadsFromRemoteAndExtracts()
    {
        // No WritePayload() call — nothing at Tools\UnrealPak.zip, so this exercises the fallback.
        var payloadBytes = BuildPayloadZipBytes();
        var handler = new FakeHttpMessageHandler(request =>
        {
            Assert.Equal("https://github.com/MK-HATERS/IcarusStarlink/releases/download/unrealpak-4.27/UnrealPak.zip", request.RequestUri!.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payloadBytes) };
        });
        var installer = CreateInstaller(httpHandler: handler);

        var exePath = await installer.InstallAsync();

        Assert.Equal(installer.InstalledExePath, exePath);
        Assert.True(File.Exists(exePath));
        // The temp download file must not linger after a successful install.
        Assert.Empty(Directory.GetFiles(Path.GetTempPath(), "UnrealPak-*.zip"));
    }

    [Fact]
    public async Task Install_NoLocalPayloadAndRemoteReturnsError_ThrowsWithGuidance()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var installer = CreateInstaller(httpHandler: handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => installer.InstallAsync());
        Assert.Contains("Couldn't download UnrealPak", ex.Message);
        Assert.Contains("404", ex.Message);
    }

    [Fact]
    public async Task Install_NoLocalPayloadAndNetworkUnreachable_ThrowsMentioningInternetConnection()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new HttpRequestException("No such host is known."));
        var installer = CreateInstaller(httpHandler: handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => installer.InstallAsync());
        Assert.Contains("internet connection", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Install_NoLocalPayloadAndMispackagedRemotePayload_SaysSoClearly()
    {
        var payloadBytes = BuildPayloadZipBytes(includeExe: false);
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payloadBytes) });
        var installer = CreateInstaller(httpHandler: handler);

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

    private sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
