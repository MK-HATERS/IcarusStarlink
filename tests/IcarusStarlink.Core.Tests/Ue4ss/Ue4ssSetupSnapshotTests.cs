using IcarusStarlink.Core.Ue4ss;
using Xunit;

namespace IcarusStarlink.Core.Tests.Ue4ss;

public sealed class Ue4ssSetupSnapshotTests : IDisposable
{
    private readonly string _ue4ss = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"), "ue4ss");

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_ue4ss)!, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_ue4ss, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private void SeedMod(string name, bool on, string config = "Speed = 1")
    {
        Write(Path.Combine("Mods", name, "Scripts", "main.lua"), "-- " + name);
        Write(Path.Combine("Mods", name, "config.lua"), config);
        if (on)
        {
            Write(Path.Combine("Mods", name, "enabled.txt"), "");
        }
    }

    private void SeedSetup()
    {
        Write("UE4SS.dll", "old loader");
        Write("UE4SS-settings.ini", "[General]\nMine = 1\n");
        Write(Path.Combine("Mods", "shared", "UEHelpers.lua"), "-- old library");
        Write(Path.Combine("Mods", "BPModLoaderMod", "Scripts", "main.lua"), "-- built in");
        SeedMod("StorageLink", on: true);
        SeedMod("NearbyCrafting", on: true);
        SeedMod("Wildlife", on: false);
    }

    [Fact]
    public void TryTake_NoUe4ssFolder_IsNull()
    {
        Assert.Null(Ue4ssSetupSnapshot.TryTake(_ue4ss));
    }

    [Fact]
    public void CheckAgainst_ANewLoaderAndLibrary_SaysSettingsAndModsAreAsTheyWere()
    {
        SeedSetup();
        var before = Ue4ssSetupSnapshot.TryTake(_ue4ss)!;

        // What an install replaces: the loader, UE4SS's own library, and its settings template.
        Write("UE4SS.dll", "new loader");
        Write("UE4SS-settings.default.ini", "[General]\n");
        Write(Path.Combine("Mods", "shared", "UEHelpers.lua"), "-- new library");
        Write(Path.Combine("Mods", "BPModLoaderMod", "Scripts", "main.lua"), "-- built in, newer");

        var check = before.CheckAgainst(_ue4ss);

        Assert.Empty(check.Problems);
        Assert.Equal(" Checked: your UE4SS settings and your 3 mods are as they were (2 on).", check.Summary);
    }

    [Fact]
    public void CheckAgainst_NamesEverythingThatIsNotAsItWas()
    {
        SeedSetup();
        var before = Ue4ssSetupSnapshot.TryTake(_ue4ss)!;

        Write("UE4SS-settings.ini", "[General]\nMine = 0\n");
        File.Delete(Path.Combine(_ue4ss, "Mods", "StorageLink", "enabled.txt"));
        Write(Path.Combine("Mods", "NearbyCrafting", "config.lua"), "Speed = 9");
        Directory.Delete(Path.Combine(_ue4ss, "Mods", "Wildlife"), recursive: true);

        var check = before.CheckAgainst(_ue4ss);

        Assert.Equal(
            ["UE4SS-settings.ini changed", "Wildlife is missing", "StorageLink was on and is now off", "NearbyCrafting's files changed"],
            check.Problems);
        Assert.Equal(
            " Check these: UE4SS-settings.ini changed; Wildlife is missing; StorageLink was on and is now off; NearbyCrafting's files changed. The rest is as it was.",
            check.Summary);
    }

    [Fact]
    public void CheckAgainst_OnlySettings_OrOneMod_OrNothingOfThePlayers()
    {
        Write("UE4SS.dll", "loader");
        Write(Path.Combine("Mods", "shared", "UEHelpers.lua"), "-- library");
        Assert.Equal("", Ue4ssSetupSnapshot.TryTake(_ue4ss)!.CheckAgainst(_ue4ss).Summary);

        Write("UE4SS-settings.ini", "[General]\n");
        Assert.Equal(" Checked: your UE4SS settings are as they were.", Ue4ssSetupSnapshot.TryTake(_ue4ss)!.CheckAgainst(_ue4ss).Summary);

        SeedMod("StorageLink", on: true);
        Assert.Equal(" Checked: your UE4SS settings and your mod are as they were (it's on).", Ue4ssSetupSnapshot.TryTake(_ue4ss)!.CheckAgainst(_ue4ss).Summary);

        File.Delete(Path.Combine(_ue4ss, "UE4SS-settings.ini"));
        File.Delete(Path.Combine(_ue4ss, "Mods", "StorageLink", "enabled.txt"));
        Assert.Equal(" Checked: your mod is as it was (it's off).", Ue4ssSetupSnapshot.TryTake(_ue4ss)!.CheckAgainst(_ue4ss).Summary);
    }

    /// <summary>mods.txt switching a mod on counts the same as enabled.txt, and losing that line is reported.</summary>
    [Fact]
    public void CheckAgainst_AModSwitchedOnByModsTxt_IsOn_AndLosingItsLineIsNamed()
    {
        Write("UE4SS.dll", "loader");
        Write(Path.Combine("Mods", "StorageLink", "Scripts", "main.lua"), "-- mod");
        Write(Path.Combine("Mods", "mods.txt"), "StorageLink : 1\n");
        var before = Ue4ssSetupSnapshot.TryTake(_ue4ss)!;
        Assert.Equal(" Checked: your mod is as it was (it's on).", before.CheckAgainst(_ue4ss).Summary);

        Write(Path.Combine("Mods", "mods.txt"), "StorageLink : 0\n");

        Assert.Equal(["StorageLink was on and is now off"], before.CheckAgainst(_ue4ss).Problems);
    }
}
