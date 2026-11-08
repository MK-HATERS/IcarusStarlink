using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using IcarusStarlink.App.Services;

namespace IcarusStarlink.App.Tests.Services;

/// <summary>
/// The single-instance pipe that hands a second launch's nxm:// link to the running Starlink.
/// Every test uses its own unique mutex and pipe names, never the real ones, so a Starlink the
/// user has open never sees these messages.
/// </summary>
public sealed class SingleInstanceServiceTests
{
    private static string UniqueName() => $"IcarusStarlink-Test-{Guid.NewGuid():N}";

    [Fact]
    public async Task AClientThatConnectsButNeverWrites_DoesNotBlockTheNextLink()
    {
        // The listener allows one connection at a time: a second launch paused right after
        // connecting must not hold it, or every later link would be lost.
        var name = UniqueName();
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = new SingleInstanceService(name, name) { ReadTimeout = TimeSpan.FromMilliseconds(300) };
        Assert.True(service.IsFirstInstance);
        service.StartListening(m => received.TrySetResult(m));

        using var silent = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.CurrentUserOnly);
        await silent.ConnectAsync(5000);

        await Task.Run(() => SingleInstanceService.SendToRunningInstance(name, "nxm://icarus/mods/1/files/2", connectTimeoutMs: 5000));

        var winner = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(6)));
        Assert.True(winner == received.Task, "The link sent after a silent client never reached the running Starlink.");
        Assert.Equal("nxm://icarus/mods/1/files/2", await received.Task);
    }

    [Fact]
    public async Task ALinkSentToTheRunningInstance_Arrives()
    {
        var name = UniqueName();
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = new SingleInstanceService(name, name);
        service.StartListening(m => received.TrySetResult(m));

        await Task.Run(() => SingleInstanceService.SendToRunningInstance(name, SingleInstanceService.ActivateMessage, connectTimeoutMs: 5000));

        var winner = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(6)));
        Assert.True(winner == received.Task, "The message never arrived.");
        Assert.Equal(SingleInstanceService.ActivateMessage, await received.Task);
    }

    [Fact]
    public async Task TheListenerPipe_LetsOnlyThisWindowsAccountIn()
    {
        // Regression: the pipe kept Windows' default access (everyone may read), so it wasn't tied
        // to the account that runs Starlink. nxm:// links carry a download key.
        var name = UniqueName();
        using var service = new SingleInstanceService(name, name);
        service.StartListening(_ => { });

        using var client = new NamedPipeClientStream(".", name,
            PipeAccessRights.ReadPermissions | PipeAccessRights.WriteData,
            PipeOptions.None, TokenImpersonationLevel.None, HandleInheritability.None);
        await client.ConnectAsync(5000);

        var me = WindowsIdentity.GetCurrent().User;
        var who = client.GetAccessControl()
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .Select(r => r.IdentityReference.Value)
            .Distinct()
            .ToList();
        Assert.Equal([me!.Value], who);
    }

    [Fact]
    public async Task Dispose_StopsTheListener_SoThePipeIsFreeAgain()
    {
        var name = UniqueName();
        var service = new SingleInstanceService(name, name);
        service.StartListening(_ => { });
        await WaitUntilAsync(() => PipeExists(name));

        service.Dispose();

        // The listener allows one pipe instance: while it still waits, no other server can open the name.
        var freed = await WaitUntilAsync(() =>
        {
            try
            {
                using var probe = new NamedPipeServerStream(name, PipeDirection.In, maxNumberOfServerInstances: 1);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
        });
        Assert.True(freed, "The listener kept waiting for a connection after Dispose.");
    }

    private static bool PipeExists(string name) => File.Exists($@"\\.\pipe\{name}");

    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(50);
        }

        return condition();
    }

    [Fact]
    public void ASecondServiceWithTheSameName_IsNotTheFirstInstance()
    {
        var name = UniqueName();
        using var first = new SingleInstanceService(name, name);
        using var second = new SingleInstanceService(name, name);

        Assert.True(first.IsFirstInstance);
        Assert.False(second.IsFirstInstance);
    }
}
