using System.IO;
using System.IO.Compression;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>The programs question: asked once per download, and leaving them out works on read-only files.</summary>
public sealed partial class DownloadsViewModelTests
{
    /// <summary>
    /// Regression: a read-only .bat in the author's own build folder (the copy keeps the attribute)
    /// made Leave them out fail the whole import.
    /// </summary>
    [Fact]
    public async Task ImportOwnBuild_AReadOnlyProgram_LeaveThemOut_StillImportsTheModWithoutIt()
    {
        var dialogs = new FakeDialogService(confirmResult: true) { WordsAnswer = false };
        var harness = new Harness(_root) { Dialogs = dialogs };
        var vm = harness.Build();
        var build = Path.Combine(_root, "MyBuild");
        Directory.CreateDirectory(Path.Combine(build, "MyMod", "Scripts"));
        File.WriteAllText(Path.Combine(build, "MyMod", "Scripts", "main.lua"), "print('hi')");
        var bat = Path.Combine(build, "MyMod", "Install.bat");
        File.WriteAllText(bat, "@echo off");
        File.SetAttributes(bat, FileAttributes.ReadOnly);
        try
        {
            var status = await vm.ImportOwnBuildAsync(build);

            Assert.DoesNotContain("Couldn't", status);
            Assert.Single(dialogs.WordsQuestions);
            var staged = harness.Ue4ss.GetFolderPath("MyMod");
            Assert.True(File.Exists(Path.Combine(staged, "Scripts", "main.lua")));
            Assert.False(File.Exists(Path.Combine(staged, "Install.bat")));
            Assert.True(File.Exists(bat));
        }
        finally
        {
            File.SetAttributes(bat, FileAttributes.Normal);
        }
    }

    /// <summary>
    /// Programs both beside a nested zip and inside it: the player is asked once, and that answer
    /// is used for the nested zip too.
    /// </summary>
    [Fact]
    public async Task Activate_ProgramsOutsideAndInsideANestedZip_AsksOnce_AndLeavesThemAllOut()
    {
        var dialogs = new FakeDialogService(confirmResult: true) { WordsAnswer = false };
        var harness = new Harness(_root) { Dialogs = dialogs };
        var vm = harness.Build();
        var inner = new MemoryStream();
        using (var innerArchive = new ZipArchive(inner, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(innerArchive, "MyMod/Scripts/main.lua", "print('hi')");
            WriteEntry(innerArchive, "MyMod/Install.bat", "@echo off");
        }

        var item = harness.AddDownload("MyMod 1.0.zip", archive =>
        {
            WriteEntry(archive, "Setup.exe", "MZ");
            using var stream = archive.CreateEntry("MyMod.zip").Open();
            stream.Write(inner.ToArray());
        });

        await vm.ActivatePendingDownloadCommand.ExecuteAsync(item);

        Assert.Single(dialogs.WordsQuestions);
        var staged = harness.Ue4ss.GetFolderPath("MyMod");
        Assert.True(File.Exists(Path.Combine(staged, "Scripts", "main.lua")));
        Assert.False(File.Exists(Path.Combine(staged, "Install.bat")));
    }
}
