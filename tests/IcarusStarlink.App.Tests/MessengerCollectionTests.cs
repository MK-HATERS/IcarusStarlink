using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;

namespace IcarusStarlink.App.Tests;

/// <summary>
/// Every test class whose code sends through or registers on WeakReferenceMessenger.Default, or sets
/// UiScale, is in the MessengerCollection (see there for why). The test files are read for those
/// uses (comments left out); the class's [Collection] is read from the built type, so it may sit on
/// any of its partial files.
/// </summary>
public sealed class MessengerCollectionTests
{
    private static readonly Regex SharedStateUse = new(@"WeakReferenceMessenger\.Default|UiScale\.(Initialize|Set)\(", RegexOptions.Compiled);

    private static readonly Regex TestClass = new(@"\bclass\s+(\w+Tests)\b", RegexOptions.Compiled);

    [Fact]
    public void EveryTestClassUsingTheAppWideMessenger_IsInTheMessengerCollection()
    {
        var testFolder = Path.Combine(RepoRoot(), "tests", "IcarusStarlink.App.Tests");
        var testTypes = typeof(MessengerCollectionTests).Assembly.GetTypes().Where(type => !type.IsNested).ToList();
        var missing = new List<string>();
        var checkedClasses = 0;
        foreach (var path in Directory.EnumerateFiles(testFolder, "*.cs", SearchOption.AllDirectories))
        {
            // This file names the uses it looks for.
            if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                || Path.GetFileName(path) == $"{nameof(MessengerCollectionTests)}.cs")
            {
                continue;
            }

            var code = string.Join('\n', File.ReadLines(path).Select(WithoutComment));
            if (!SharedStateUse.IsMatch(code) || TestClass.Match(code) is not { Success: true } match)
            {
                continue;
            }

            var name = match.Groups[1].Value;
            var type = testTypes.SingleOrDefault(t => t.Name == name);
            Assert.True(type is not null, $"{name} (in {Path.GetFileName(path)}) wasn't found in the test assembly.");
            checkedClasses++;
            if (!IsInMessengerCollection(type!))
            {
                missing.Add($"{name} ({Path.GetFileName(path)})");
            }
        }

        Assert.True(checkedClasses > 0, "No test class using WeakReferenceMessenger.Default was found: is the test folder where this test looks?");
        Assert.Empty(missing);
    }

    private static string WithoutComment(string line)
    {
        var comment = line.IndexOf("//", StringComparison.Ordinal);
        return comment < 0 ? line : line[..comment];
    }

    private static bool IsInMessengerCollection(Type type) =>
        type.GetCustomAttributesData().Any(attribute =>
            attribute.AttributeType == typeof(CollectionAttribute)
            && attribute.ConstructorArguments.Count > 0
            && Equals(attribute.ConstructorArguments[0].Value, MessengerCollection.Name));

    private static string RepoRoot()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "IcarusStarlink.slnx")))
        {
            folder = folder.Parent;
        }

        return folder?.FullName ?? throw new InvalidOperationException("IcarusStarlink.slnx not found above the test folder.");
    }
}
