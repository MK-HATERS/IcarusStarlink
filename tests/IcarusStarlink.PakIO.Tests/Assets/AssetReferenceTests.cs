using IcarusStarlink.PakIO.Assets;

namespace IcarusStarlink.PakIO.Tests.Assets;

/// <summary>
/// A mod's cooked files checked against the game's content, and a moved game file pointed at its
/// new place. The fixture is a real cooked Icarus blueprint (Wax's MIT-licensed sample,
/// Fixtures/WaxSample): it subclasses the game's /Game/BP/Utilities/WT/WT_CaveLight and uses two
/// of its components.
/// </summary>
public sealed class AssetReferenceTests : IDisposable
{
    private const string CaveLight = "/Game/BP/Utilities/WT/WT_CaveLight";
    private const string File = "BP/Wax/BP_WaxSampleCaveLight.uasset";

    private static readonly string FixtureFolder = Path.Combine(AppContext.BaseDirectory, "Fixtures", "WaxSample");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "AssetRefs_" + Guid.NewGuid().ToString("N"));

    /// <summary>What WT_CaveLight holds that the sample uses (place inside the package → class).</summary>
    private static Dictionary<string, string> CaveLightObjects() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["WT_CaveLight_C"] = "BlueprintGeneratedClass",
        ["Default__WT_CaveLight_C"] = "WT_CaveLight_C",
        ["WT_CaveLight_C.PointLight_GEN_VARIABLE"] = "PointLightComponent",
        ["WT_CaveLight_C.DefaultSceneRoot_GEN_VARIABLE"] = "SceneComponent",
        ["WT_CaveLight_C.UserConstructionScript"] = "Function",
    };

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static byte[] Header() => System.IO.File.ReadAllBytes(Path.Combine(FixtureFolder, "BP_WaxSampleCaveLight.uasset"));

    private static IReadOnlyList<MissingAssetReference>? Check(FakeGame game, IReadOnlySet<string>? elsewhere = null) =>
        AssetReferenceChecker.Check([File, "BP/Wax/BP_WaxSampleCaveLight.uexp"], f => f == File ? Header() : null, game, elsewhere);

    /// <summary>A copy of the fixture in a temp folder, for the rewriter to change.</summary>
    private string CopyFixture()
    {
        Directory.CreateDirectory(_root);
        foreach (var extension in new[] { ".uasset", ".uexp" })
        {
            System.IO.File.Copy(Path.Combine(FixtureFolder, "BP_WaxSampleCaveLight" + extension), Path.Combine(_root, "BP_WaxSampleCaveLight" + extension));
        }

        return Path.Combine(_root, "BP_WaxSampleCaveLight.uasset");
    }

    [Fact]
    public void Header_ReadsTheImportsOfARealCookedBlueprint()
    {
        var header = UnrealPackageHeader.TryRead(Header());

        Assert.NotNull(header);
        var packages = Enumerable.Range(0, header.Imports.Count).Select(header.ImportPackage).Distinct().ToList();
        Assert.Contains(CaveLight, packages);
        Assert.Contains("/Script/Engine", packages);
        var light = header.Imports.ToList().FindIndex(i => i.ObjectName == "PointLight_GEN_VARIABLE");
        Assert.Equal("WT_CaveLight_C.PointLight_GEN_VARIABLE", header.ImportPath(light));
        Assert.Equal(CaveLight, header.ImportPackage(light));
    }

    [Fact]
    public void Header_ReadsTheExports_AndTheyFillTheFile()
    {
        var header = UnrealPackageHeader.TryRead(Header())!;
        var exports = System.IO.File.ReadAllBytes(Path.Combine(FixtureFolder, "BP_WaxSampleCaveLight.uexp"));

        var objects = header.ExportedObjects();

        Assert.Equal("BlueprintGeneratedClass", objects["BP_WaxSampleCaveLight_C"]);
        Assert.True(header.ExportsFillPackage(Header().LongLength + exports.LongLength));
        Assert.False(header.ExportsFillPackage(Header().LongLength + exports.LongLength + 16));
    }

    [Fact]
    public void Header_IsNullForSomethingElse()
    {
        Assert.Null(UnrealPackageHeader.TryRead("not a package at all, just some text"u8));
        Assert.Null(UnrealPackageHeader.TryRead(Header().AsSpan(0, 40)));
    }

    [Fact]
    public void Check_NothingWhenTheGameHasEverything()
    {
        var game = new FakeGame { [CaveLight] = CaveLightObjects() };

        Assert.Empty(Check(game)!);
    }

    [Fact]
    public void Check_TheGameMovedIt_OneNewPlace()
    {
        var game = new FakeGame { ["/Game/BP/World/WT_CaveLight"] = CaveLightObjects() };

        var found = Assert.Single(Check(game)!);

        Assert.Equal(MissingReferenceKind.Moved, found.Kind);
        Assert.Equal(CaveLight, found.Package);
        Assert.Equal("/Game/BP/World/WT_CaveLight", found.NewPackage);
        Assert.Equal(File, found.AssetFile);
    }

    [Fact]
    public void Check_SeveralPlaces_SuggestsTheNearest()
    {
        var game = new FakeGame
        {
            ["/Game/Testing/Old/WT_CaveLight"] = CaveLightObjects(),
            ["/Game/BP/Utilities/World/WT_CaveLight"] = CaveLightObjects(),
        };

        var found = Assert.Single(Check(game)!);

        Assert.Equal(MissingReferenceKind.MovedMaybe, found.Kind);
        Assert.Equal("/Game/BP/Utilities/World/WT_CaveLight", found.NewPackage);
        Assert.Equal(2, found.Candidates.Count);
    }

    /// <summary>A file of the same name that lacks something the mod uses is another file, not where it moved.</summary>
    [Fact]
    public void Check_SameNameWithoutTheComponent_IsGone()
    {
        var objects = CaveLightObjects();
        objects.Remove("WT_CaveLight_C.PointLight_GEN_VARIABLE");
        var game = new FakeGame { ["/Game/BP/World/WT_CaveLight"] = objects };

        var found = Assert.Single(Check(game)!);

        Assert.Equal(MissingReferenceKind.Gone, found.Kind);
        Assert.Null(found.NewPackage);
    }

    [Fact]
    public void Check_SameNameAnotherClass_IsGone()
    {
        var objects = CaveLightObjects();
        objects["WT_CaveLight_C.PointLight_GEN_VARIABLE"] = "SpotLightComponent";
        var game = new FakeGame { ["/Game/BP/World/WT_CaveLight"] = objects };

        Assert.Equal(MissingReferenceKind.Gone, Assert.Single(Check(game)!).Kind);
    }

    [Fact]
    public void Check_TheGameTookAComponentOut_ListsOnlyThatPart()
    {
        var objects = CaveLightObjects();
        objects.Remove("WT_CaveLight_C.PointLight_GEN_VARIABLE");
        var game = new FakeGame { [CaveLight] = objects };

        var found = Assert.Single(Check(game)!);

        Assert.Equal(MissingReferenceKind.ObjectGone, found.Kind);
        Assert.Equal(["WT_CaveLight_C.PointLight_GEN_VARIABLE"], found.Objects.Keys);
    }

    [Fact]
    public void Check_AnotherLibraryModHasIt_IsADependency()
    {
        var game = new FakeGame();

        Assert.Empty(Check(game, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { CaveLight.ToUpperInvariant() })!);
    }

    [Fact]
    public void Check_CantSayAnythingWithoutTheGame()
    {
        Assert.Null(Check(new FakeGame { Readable = false }));
    }

    [Fact]
    public void PackagePathOf_IsTheGamePath()
    {
        Assert.Equal("/Game/ASS/DEP/SM_X", AssetReferenceChecker.PackagePathOf("ASS\\DEP\\SM_X.uexp"));
        Assert.Null(AssetReferenceChecker.PackagePathOf("Extracted Mods/X.EXMOD"));
    }

    [Fact]
    public void Rewrite_PointsTheFileAtTheNewPlace_AndNothingElseChanges()
    {
        var path = CopyFixture();
        var before = UnrealPackageHeader.TryRead(System.IO.File.ReadAllBytes(path))!;

        var rewritten = AssetReferenceRewriter.TryRewrite(path, new Dictionary<string, string> { [CaveLight] = "/Game/BP/World/WT_CaveLight" }, out var why);

        Assert.Null(why);
        Assert.NotNull(rewritten);
        AssetReferenceRewriter.Commit(rewritten);
        var after = UnrealPackageHeader.TryRead(System.IO.File.ReadAllBytes(path))!;
        Assert.Equal(before.Imports.Count, after.Imports.Count);
        for (var i = 0; i < before.Imports.Count; i++)
        {
            var expected = before.ImportPackage(i) == CaveLight ? "/Game/BP/World/WT_CaveLight" : before.ImportPackage(i);
            Assert.Equal(expected, after.ImportPackage(i));
            Assert.Equal(before.ImportPath(i), after.ImportPath(i));
        }

        Assert.Equal(before.ExportedObjects(), after.ExportedObjects());
        Assert.True(after.ExportsFillPackage(new FileInfo(path).Length + new FileInfo(Path.ChangeExtension(path, ".uexp")).Length));
    }

    /// <summary>Pointed away and back again, the file is byte for byte what it was: nothing but the name changed.</summary>
    [Fact]
    public void Rewrite_ThereAndBack_IsTheSameFile()
    {
        var path = CopyFixture();

        AssetReferenceRewriter.Commit(AssetReferenceRewriter.TryRewrite(path, new Dictionary<string, string> { [CaveLight] = "/Game/A/Much/Longer/Folder/Than/Before/WT_CaveLight" }, out _)!);
        AssetReferenceRewriter.Commit(AssetReferenceRewriter.TryRewrite(path, new Dictionary<string, string> { ["/Game/A/Much/Longer/Folder/Than/Before/WT_CaveLight"] = CaveLight }, out _)!);

        Assert.Equal(Header(), System.IO.File.ReadAllBytes(path));
        Assert.Equal(System.IO.File.ReadAllBytes(Path.Combine(FixtureFolder, "BP_WaxSampleCaveLight.uexp")), System.IO.File.ReadAllBytes(Path.ChangeExtension(path, ".uexp")));
    }

    [Fact]
    public void Rewrite_RefusedWhenTheFileDoesntNameIt()
    {
        var path = CopyFixture();

        Assert.Null(AssetReferenceRewriter.TryRewrite(path, new Dictionary<string, string> { ["/Game/Nothing/Here"] = "/Game/Else" }, out var why));
        Assert.NotNull(why);
    }

    /// <summary>Data after the objects would keep its old position in a file whose header grew: never rewritten.</summary>
    [Fact]
    public void Rewrite_RefusedWhenSomethingFollowsTheObjects()
    {
        var path = CopyFixture();
        var exports = Path.ChangeExtension(path, ".uexp");
        System.IO.File.WriteAllBytes(exports, [.. System.IO.File.ReadAllBytes(exports), .. new byte[16]]);

        Assert.Null(AssetReferenceRewriter.TryRewrite(path, new Dictionary<string, string> { [CaveLight] = "/Game/BP/World/WT_CaveLight" }, out var why));
        Assert.Contains("after its objects", why);
    }

    /// <summary>A game made of packages and what each holds.</summary>
    private sealed class FakeGame : Dictionary<string, Dictionary<string, string>>, IGameAssetCatalog
    {
        public FakeGame() : base(StringComparer.OrdinalIgnoreCase)
        {
        }

        public bool Readable { get; init; } = true;

        public bool? HasPackage(string packagePath) => Readable ? ContainsKey(packagePath) : null;

        public IReadOnlyDictionary<string, string>? Objects(string packagePath) => TryGetValue(packagePath, out var objects) ? objects : null;

        public IReadOnlyList<string> PackagesNamed(string assetName) =>
            [.. Keys.Where(k => string.Equals(AssetReferenceChecker.AssetName(k), assetName, StringComparison.OrdinalIgnoreCase))];
    }
}
