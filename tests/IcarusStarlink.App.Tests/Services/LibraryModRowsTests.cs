using System.IO;
using IcarusStarlink.App.Services;
using IcarusStarlink.Core.Library;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;
using IcarusStarlink.Storage.Library;
using Microsoft.Extensions.Logging.Abstractions;

namespace IcarusStarlink.App.Tests.Services;

/// <summary>The rows each Library mod changes, for Weekly Changes' "Only rows my mods change".</summary>
public sealed class LibraryModRowsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "ModRows_" + Guid.NewGuid().ToString("N"));
    private readonly FolderLibraryRepository _library;

    public LibraryModRowsTests()
    {
        _library = new FolderLibraryRepository(Path.Combine(_root, "Extracted_Mods"), Path.Combine(_root, "Library_Meta"), Path.Combine(_root, "Backups"), NullLogger<FolderLibraryRepository>.Instance);
    }

    public void Dispose()
    {
        _library.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void Mod(string name, string rowsJson)
    {
        var folder = _library.CreateBlankMod(name, "Me", ModTemplate.Blank).FolderName;
        ExmodFolder.Write(_library.GetFolderPath(folder), new ExmodPackageContents(ExmodJson.Parse($$"""
            { "name": "{{name}}", "author": "Me", "version": "1", "description": "D", "fileName": "{{name.Replace(' ', '_')}}", "Rows": [{{rowsJson}}] }
            """), []));
    }

    [Fact]
    public void Read_ListsEveryRowEachModChanges()
    {
        Mod("Better Canteens", """
            {"CurrentFile": "Items-D_ItemsStatic.json", "File_Items": [{"Name": "Canteen", "Durability": 150}, {"Name": "Bread", "Weight": 5}]}
            """);
        Mod("Fuel Mod", """
            {"CurrentFile": "Crafting-D_Fuel.json", "File_Items": [{"Name": "Wood", "BurnTime": 99}]}
            """);

        var rows = LibraryModRows.Read(_library);

        Assert.Equal(
            ["Better Canteens|Items-D_ItemsStatic.json|Bread", "Better Canteens|Items-D_ItemsStatic.json|Canteen", "Fuel Mod|Crafting-D_Fuel.json|Wood"],
            rows.Select(r => $"{r.ModName}|{r.CurrentFile}|{r.RowName}").Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("Items-D_ItemsStatic.json", "Items/D_ItemsStatic.json")]
    [InlineData("D_ItemsStatic.json", "Items/D_ItemsStatic.json")]
    [InlineData("items/d_itemsstatic.json", "Items/D_ItemsStatic.json")]
    public void TableKey_AModsFileNameAndAReportsPath_Match(string modFile, string reportPath) =>
        Assert.Equal(LibraryModRows.TableKey(modFile), LibraryModRows.TableKey(reportPath));
}
