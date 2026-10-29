using System.IO;
using UeSaveGame;
using UeSaveGame.PropertyTypes;
using UeSaveGame.StructData;
using UeSaveGame.Util;

namespace IcarusStarlink.App.Services;

/// <param name="Row">D_Talents row ("Raptor_Health").</param>
public sealed record MountTalentRank(string Row, int Rank);

/// <param name="Name">D_GeneticValues row ("Vitality").</param>
/// <param name="Value">0 to 10; 3 is the middle (the game's genetics curves are 0 at 3).</param>
public sealed record MountGene(string Name, int Value);

/// <summary>
/// A saved mount's RecorderBlob (Mounts.json): Unreal tagged properties holding the mount's own
/// copy of its name, its experience, talents, genetics, lineage and look, read and written with
/// UeSaveGame (vendored, src/ThirdParty). Mount editor, plan step 4 (2026-10-23).
///
/// Only ever used on a blob that writes back byte for byte unchanged: TryRead checks that first
/// and refuses anything else, so a mount this code can't round-trip is never written (it stays
/// label-only on the page). Every mount on the user's save and its 12 backups (61) passed.
/// </summary>
public sealed class MountBlob
{
    private static readonly PackageVersion Version = new()
    {
        PackageVersionUE4 = EObjectUE4Version.VER_UE4_CORRECT_LICENSEE_FLAG,
        PackageVersionUE5 = EObjectUE5Version.INVALID,
    };

    private readonly byte[] _original;
    private readonly List<FPropertyTag> _properties;

    private MountBlob(byte[] original, List<FPropertyTag> properties)
    {
        _original = original;
        _properties = properties;
    }

    /// <summary>Null (with why) when the blob can't be read, or doesn't write back exactly as it was.</summary>
    public static MountBlob? TryRead(byte[] data, out string? why)
    {
        why = null;
        try
        {
            var properties = Parse(data);
            if (!Serialize(properties).AsSpan().SequenceEqual(data))
            {
                why = "its data doesn't write back exactly as it was read";
                return null;
            }

            return new MountBlob(data, properties);
        }
        catch (Exception ex)
        {
            why = $"its data couldn't be read ({ex.GetType().Name})";
            return null;
        }
    }

    private static List<FPropertyTag> Parse(byte[] data)
    {
        using var reader = new BinaryReader(new MemoryStream(data));
        return [.. PropertySerializationHelper.ReadProperties(reader, Version, true)];
    }

    private static byte[] Serialize(IEnumerable<FPropertyTag> properties)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream))
        {
            PropertySerializationHelper.WriteProperties(properties, writer, Version, true);
            writer.Flush();
        }

        return stream.ToArray();
    }

    /// <summary>The blob as it is now (the original bytes when nothing changed).</summary>
    public byte[] Write() => Serialize(_properties);

    private FPropertyTag? Tag(string name) => _properties.FirstOrDefault(p => p.Name.Value == name);

    private static FPropertyTag? Tag(IEnumerable<FPropertyTag> properties, string name) => properties.FirstOrDefault(p => p.Name.Value == name);

    /// <summary>The mount's own copy of its name (the list's copy is Mounts.json's MountName).</summary>
    public string? Name
    {
        get => (Tag("MountName")?.Property as StrProperty)?.Value?.Value;
        set
        {
            if (Tag("MountName")?.Property is StrProperty p && value is not null)
            {
                p.Value = new FString(value);
            }
        }
    }

    public int? Experience
    {
        get => (Tag("Experience")?.Property as IntProperty)?.Value;
        set
        {
            if (Tag("Experience")?.Property is IntProperty p && value is { } v)
            {
                p.Value = v;
            }
        }
    }

    /// <summary>D_GeneticLineages row ("Wild", "Alpha").</summary>
    public string? Lineage
    {
        get => (Tag("Lineage")?.Property as NameProperty)?.Value?.Value;
        set
        {
            if (Tag("Lineage")?.Property is NameProperty p && value is not null)
            {
                p.Value = new FString(value);
            }
        }
    }

    /// <summary>Which of its type's looks (D_Mounts Variations, 0-based).</summary>
    public int? Variation
    {
        get => (Tag("Variation")?.Property as IntProperty)?.Value;
        set
        {
            if (Tag("Variation")?.Property is IntProperty p && value is { } v)
            {
                p.Value = v;
            }
        }
    }

    /// <summary>The ID the game knows it by; Mounts.json's MountIconName (its portrait's file name) is the same number.</summary>
    public int? ActorGuid => (Tag("IcarusActorGUID")?.Property as IntProperty)?.Value;

    private IEnumerable<IList<FPropertyTag>> Elements(string array) =>
        (Tag(array)?.Property as ArrayProperty)?.Value?.OfType<StructProperty>().Select(s => s.Value).OfType<PropertiesStruct>().Select(p => p.Properties) ?? [];

    public IReadOnlyList<MountGene> Genetics =>
        [.. Elements("Genetics").Select(e => new MountGene(
            (Tag(e, "GeneticValueName")?.Property as StrProperty)?.Value?.Value ?? "?",
            (Tag(e, "Value")?.Property as IntProperty)?.Value ?? 0))];

    public void SetGene(string name, int value)
    {
        foreach (var element in Elements("Genetics"))
        {
            if ((Tag(element, "GeneticValueName")?.Property as StrProperty)?.Value?.Value == name && Tag(element, "Value")?.Property is IntProperty p)
            {
                p.Value = value;
            }
        }
    }

    public IReadOnlyList<MountTalentRank> Talents =>
        [.. Elements("Talents").Select(e => new MountTalentRank(
            (Tag(e, "TalentRowName")?.Property as StrProperty)?.Value?.Value ?? "?",
            (Tag(e, "TalentRank")?.Property as IntProperty)?.Value ?? 0))];

    /// <summary>
    /// Sets a talent's rank: 0 takes it out of the list (the game's own "not learned"). A talent the
    /// mount doesn't have yet is added as a copy of one of its existing entries (read fresh from the
    /// original bytes), so it's built exactly like the game's; false when there's none to copy.
    /// </summary>
    public bool SetTalentRank(string row, int rank)
    {
        if (Tag("Talents")?.Property is not ArrayProperty array || array.Value is not { } items)
        {
            return false;
        }

        var list = items.OfType<FProperty>().ToList();
        var existing = list.FirstOrDefault(i => i is StructProperty { Value: PropertiesStruct ps }
            && string.Equals((Tag(ps.Properties, "TalentRowName")?.Property as StrProperty)?.Value?.Value, row, StringComparison.OrdinalIgnoreCase));
        if (existing is StructProperty { Value: PropertiesStruct found })
        {
            if (rank <= 0)
            {
                list.Remove(existing);
            }
            else if (Tag(found.Properties, "TalentRank")?.Property is IntProperty r)
            {
                r.Value = rank;
            }
        }
        else if (rank > 0)
        {
            var fresh = (Tag(Parse(_original), "Talents")?.Property as ArrayProperty)?.Value?.OfType<StructProperty>().FirstOrDefault();
            if (fresh is not { Value: PropertiesStruct copy }
                || Tag(copy.Properties, "TalentRowName")?.Property is not StrProperty name
                || Tag(copy.Properties, "TalentRank")?.Property is not IntProperty value)
            {
                return false;
            }

            name.Value = new FString(row);
            value.Value = rank;
            list.Add(fresh);
        }
        else
        {
            return true;
        }

        var newItems = Array.CreateInstance(items.GetType().GetElementType()!, list.Count);
        for (var i = 0; i < list.Count; i++)
        {
            newItems.SetValue(list[i], i);
        }

        array.Value = newItems;
        return true;
    }
}
