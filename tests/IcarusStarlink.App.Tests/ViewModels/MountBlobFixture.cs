using System.Text.Json.Nodes;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>Mount RecorderBlobs (Mounts.json) built in the test, as the game writes them.</summary>
internal static class MountBlobFixture
{
    /// <summary>A RecorderBlob of tagged properties: the mount's own name and its experience, then None and a 0. It has no Talents.</summary>
    public static JsonArray Blob(string name, int experience)
    {
        using var stream = new System.IO.MemoryStream();
        using var writer = new System.IO.BinaryWriter(stream);
        void FString(string s)
        {
            writer.Write(s.Length + 1);
            writer.Write(System.Text.Encoding.ASCII.GetBytes(s));
            writer.Write((byte)0);
        }

        FString("MountName");
        FString("StrProperty");
        writer.Write(4 + name.Length + 1);
        writer.Write(0);
        writer.Write((byte)0);
        FString(name);

        FString("Experience");
        FString("IntProperty");
        writer.Write(4);
        writer.Write(0);
        writer.Write((byte)0);
        writer.Write(experience);

        FString("None");
        writer.Write(0);
        writer.Flush();
        return new JsonArray([.. stream.ToArray().Select(b => (JsonNode)JsonValue.Create((int)b))]);
    }
}
