using System.Runtime.Versioning;
using IcarusStarlink.Storage.Secrets;

namespace IcarusStarlink.Storage.Tests.Secrets;

/// <summary>
/// Exercises the REAL Windows Credential Manager on whatever machine runs this test (P/Invoke
/// wrappers are exactly the kind of code a mock can't meaningfully verify — a subtle struct-layout
/// mistake would still "pass" against a fake). Every test uses its own uniquely-named target and
/// cleans up in a finally block, so a failed run doesn't leave real entries behind in the actual
/// Windows Credential Manager this test suite's own machine uses.
/// </summary>
[SupportedOSPlatform("windows")]
public class WindowsCredentialStoreTests
{
    private static string UniqueTarget([System.Runtime.CompilerServices.CallerMemberName] string testName = "") =>
        $"IcarusStarlink:Test:{testName}:{Guid.NewGuid():N}";

    [Fact]
    public void SaveThenRead_RoundTripsExactly()
    {
        var store = new WindowsCredentialStore();
        var target = UniqueTarget();
        try
        {
            store.Save(target, "sk-test-1234567890abcdef");

            Assert.Equal("sk-test-1234567890abcdef", store.Read(target));
        }
        finally
        {
            store.Delete(target);
        }
    }

    [Fact]
    public void Read_NothingSaved_ReturnsNull()
    {
        var store = new WindowsCredentialStore();

        Assert.Null(store.Read(UniqueTarget()));
    }

    [Fact]
    public void Save_CalledTwice_OverwritesRatherThanErroring()
    {
        var store = new WindowsCredentialStore();
        var target = UniqueTarget();
        try
        {
            store.Save(target, "first-value");
            store.Save(target, "second-value");

            Assert.Equal("second-value", store.Read(target));
        }
        finally
        {
            store.Delete(target);
        }
    }

    [Fact]
    public void Delete_RemovesTheSecret()
    {
        var store = new WindowsCredentialStore();
        var target = UniqueTarget();
        store.Save(target, "to-be-deleted");

        store.Delete(target);

        Assert.Null(store.Read(target));
    }

    [Fact]
    public void Delete_NothingSaved_DoesNotThrow()
    {
        var store = new WindowsCredentialStore();

        var exception = Record.Exception(() => store.Delete(UniqueTarget()));

        Assert.Null(exception);
    }
}
