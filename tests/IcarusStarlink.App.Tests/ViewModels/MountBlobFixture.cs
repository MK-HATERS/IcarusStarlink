using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>Mount RecorderBlobs (Mounts.json) built in the test, as the game writes them.</summary>
internal static class MountBlobFixture
{
    /// <summary>A RecorderBlob of tagged properties: the mount's own name and its experience, then None and a 0. It has no Talents.</summary>
    public static JsonArray Blob(string name, int experience)
    {
        var bytes = new BlobBuilder().AddStr("MountName", name).AddInt("Experience", experience).Build();
        return new JsonArray([.. bytes.Select(b => (JsonNode)JsonValue.Create((int)b))]);
    }

    /// <summary>
    /// Builds a blob of tagged properties the way MountBlob reads them (UE4, before the UE5 tag
    /// layout): each tag is name, type, size, array index, the type's own header, then a 0 byte
    /// (no property GUID), then the value. An array of structs holds a count, one inner struct tag
    /// (with the size of all the elements), then each element's properties ending with "None".
    /// </summary>
    internal sealed class BlobBuilder
    {
        private readonly MemoryStream _stream = new();
        private readonly BinaryWriter _writer;

        public BlobBuilder() => _writer = new BinaryWriter(_stream);

        private static byte[] FString(string s) => [.. BitConverter.GetBytes(s.Length + 1), .. Encoding.ASCII.GetBytes(s), 0];

        private static byte[] Tag(string name, string type, int size, byte[] header) =>
            [.. FString(name), .. FString(type), .. BitConverter.GetBytes(size), .. BitConverter.GetBytes(0), .. header, 0];

        private static byte[] Str(string name, string value, string type = "StrProperty")
        {
            var bytes = FString(value);
            return [.. Tag(name, type, bytes.Length, []), .. bytes];
        }

        private static byte[] Int(string name, int value) => [.. Tag(name, "IntProperty", 4, []), .. BitConverter.GetBytes(value)];

        public BlobBuilder AddStr(string name, string value)
        {
            _writer.Write(Str(name, value));
            return this;
        }

        public BlobBuilder AddName(string name, string value)
        {
            _writer.Write(Str(name, value, "NameProperty"));
            return this;
        }

        public BlobBuilder AddInt(string name, int value)
        {
            _writer.Write(Int(name, value));
            return this;
        }

        /// <summary>An array of structs, each element one (StrProperty, IntProperty) pair.</summary>
        public BlobBuilder AddStructArray(string name, string structName, string keyName, string valueName, params (string Key, int Value)[] elements)
        {
            var body = elements.SelectMany(e => (byte[])[.. Str(keyName, e.Key), .. Int(valueName, e.Value), .. FString("None")]).ToArray();
            byte[] inner = [.. FString(name), .. FString("StructProperty"), .. BitConverter.GetBytes(body.Length), .. BitConverter.GetBytes(0),
                .. FString(structName), .. new byte[16], 0];
            byte[] value = [.. BitConverter.GetBytes(elements.Length), .. inner, .. body];
            _writer.Write(Tag(name, "ArrayProperty", value.Length, FString("StructProperty")));
            _writer.Write(value);
            return this;
        }

        public byte[] Build()
        {
            _writer.Write(FString("None"));
            _writer.Write(0);
            _writer.Flush();
            return _stream.ToArray();
        }
    }
}
