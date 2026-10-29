using System.Text;
using IcarusStarlink.Core.Ue4ss;

namespace IcarusStarlink.Core.Tests.Ue4ss;

/// <summary>
/// Config files keep their own encoding and BOM (report 2026-10-28: a config.lua saved as ANSI in
/// Notepad with "é" in a comment came back as "�" after a Save or a mod update, and some paths
/// dropped a leading UTF-8 BOM). Every path that reads and writes a config goes through
/// <see cref="ConfigTextFile"/>; an unchanged file written back is the same bytes.
/// </summary>
public sealed class ConfigTextFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "ConfigTextFile_" + Guid.NewGuid().ToString("N"));

    public ConfigTextFileTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    internal const string LuaText = "-- Réglages: vitesse de la pêche\r\nreturn {\r\n    Speed = 5,\r\n    Name = \"Café\",\r\n}\r\n";

    internal const string IniText = "[General]\r\n; Paramètre très utile\r\nSpeed = 5 ; plus élevé = plus rapide\r\n";

    /// <summary>The four encodings a config file comes in, as the bytes a text editor writes.</summary>
    public static TheoryData<ConfigTextEncoding> Encodings => new()
    {
        ConfigTextEncoding.Utf8, ConfigTextEncoding.Utf8Bom, ConfigTextEncoding.Utf16LeBom, ConfigTextEncoding.Windows1252,
    };

    /// <summary>The bytes Notepad (or any editor) writes for <paramref name="text"/> in <paramref name="encoding"/>.</summary>
    internal static byte[] Bytes(string text, ConfigTextEncoding encoding) => encoding switch
    {
        ConfigTextEncoding.Utf8 => Encoding.UTF8.GetBytes(text),
        ConfigTextEncoding.Utf8Bom => [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(text)],
        ConfigTextEncoding.Utf16LeBom => [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(text)],
        ConfigTextEncoding.Utf16BeBom => [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes(text)],
        ConfigTextEncoding.Windows1252 => CodePagesEncodingProvider.Instance.GetEncoding(1252)!.GetBytes(text),
        _ => Encoding.Latin1.GetBytes(text),
    };

    [Theory]
    [MemberData(nameof(Encodings))]
    [InlineData(ConfigTextEncoding.Utf16BeBom)]
    public void Decode_FindsTheEncoding_AndTheTextHasNoBom(ConfigTextEncoding encoding)
    {
        var config = ConfigTextFile.Decode(Bytes(LuaText, encoding));

        Assert.Equal(encoding, config.Encoding);
        Assert.Equal(LuaText, config.Text);
    }

    [Theory]
    [MemberData(nameof(Encodings))]
    [InlineData(ConfigTextEncoding.Utf16BeBom)]
    public void ReadThenWrite_AnUnchangedFile_IsTheSameBytes(ConfigTextEncoding encoding)
    {
        var path = Path.Combine(_root, "config.lua");
        var bytes = Bytes(LuaText, encoding);
        File.WriteAllBytes(path, bytes);

        Assert.Equal(encoding, ConfigTextFile.Write(path, ConfigTextFile.Read(path)));

        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public void Ansi_EAcuteIsOneByte_AndStaysOneByte()
    {
        var bytes = Bytes("-- é\n", ConfigTextEncoding.Windows1252);
        Assert.Equal(0xE9, bytes[3]);

        var config = ConfigTextFile.Decode(bytes);

        Assert.Equal("-- é\n", config.Text);
        Assert.Equal(bytes, ConfigTextFile.Encode(config.Text, config.Encoding, out _));
    }

    [Fact]
    public void AnsiFileWithBytes1252LeavesUndefined_StillComesBackByteForByte()
    {
        // 0x81, 0x8D, 0x8F, 0x90, 0x9D have no 1252 character: whatever is used, the bytes come back.
        byte[] bytes = [.. Encoding.ASCII.GetBytes("-- "), 0x81, 0x8D, 0x8F, 0x90, 0x9D, 0xE9, (byte)'\n'];

        var config = ConfigTextFile.Decode(bytes);

        Assert.True(config.Encoding is ConfigTextEncoding.Windows1252 or ConfigTextEncoding.Latin1);
        Assert.Equal(bytes, ConfigTextFile.Encode(config.Text, config.Encoding, out _));
    }

    [Fact]
    public void AnsiFile_GivenTextItCantHold_IsWrittenAsUtf8_NotQuestionMarks()
    {
        var bytes = ConfigTextFile.Encode("-- é → 🐻\n", ConfigTextEncoding.Windows1252, out var written);

        Assert.Equal(ConfigTextEncoding.Utf8, written);
        Assert.Equal("-- é → 🐻\n", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void Read_OverTheCap_IsRefused()
    {
        var path = Path.Combine(_root, "config.lua");
        File.WriteAllBytes(path, new byte[2048]);

        Assert.Throws<IOException>(() => ConfigTextFile.Read(path, maxBytes: 1024));
        Assert.Equal(2048, ConfigTextFile.Read(path).Text.Length);
    }

    [Fact]
    public void EmptyFile_IsUtf8()
    {
        var config = ConfigTextFile.Decode([]);

        Assert.Equal(new ConfigText("", ConfigTextEncoding.Utf8), config);
    }

    // UE4SS-settings.ini through Settings › UE4SS config: Load + Save.
    [Theory]
    [MemberData(nameof(Encodings))]
    public void SettingsDocument_LoadSave_KeepsTheEncoding_UnchangedIsTheSameBytes(ConfigTextEncoding encoding)
    {
        var path = Path.Combine(_root, "UE4SS-settings.ini");
        var bytes = Bytes(IniText, encoding);
        File.WriteAllBytes(path, bytes);

        var document = Ue4ssSettingsDocument.Load(path, keepCommentLines: true);
        Assert.Equal(encoding, document.Encoding);
        Assert.Contains("Paramètre très utile", document.Settings.Single().Description);
        document.Save(path);
        Assert.Equal(bytes, File.ReadAllBytes(path));

        document.SetValue("General", "Speed", "7");
        document.Save(path);
        Assert.Equal(Bytes(IniText.Replace("Speed = 5", "Speed = 7"), encoding), File.ReadAllBytes(path));
    }

    // A mod update (Ue4ssModConfigCarryOver.KeepInstalledConfigs): the player's file and the new
    // version's are the same, in each encoding: the file after the update is the same bytes.
    [Theory]
    [MemberData(nameof(Encodings))]
    public void CarryOver_UnchangedFile_IsTheSameBytes(ConfigTextEncoding encoding)
    {
        var bytes = Bytes(LuaText, encoding);
        var previous = Folder("old", ("config.lua", bytes));
        var updated = Folder("new", ("config.lua", bytes));

        Ue4ssModConfigCarryOver.KeepInstalledConfigs(previous, updated);

        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(updated, "config.lua")));
    }

    [Theory]
    [MemberData(nameof(Encodings))]
    public void CarryOver_ThePlayersValueGoesIn_InThePlayersEncoding(ConfigTextEncoding encoding)
    {
        var previous = Folder("old", ("config.lua", Bytes(LuaText.Replace("Speed = 5", "Speed = 9"), encoding)));
        // The new version ships UTF-8 with one new setting.
        var shipped = LuaText.Replace("    Speed = 5,\r\n", "    Speed = 5,\r\n    Range = 2,\r\n");
        var updated = Folder("new", ("config.lua", Bytes(shipped, ConfigTextEncoding.Utf8)));

        Ue4ssModConfigCarryOver.KeepInstalledConfigs(previous, updated);

        Assert.Equal(Bytes(shipped.Replace("Speed = 5", "Speed = 9"), encoding), File.ReadAllBytes(Path.Combine(updated, "config.lua")));
    }

    // A shared patch or a server's settings written into a copy (ApplyValues): the copy keeps its encoding.
    [Theory]
    [MemberData(nameof(Encodings))]
    public void ApplyValues_KeepsTheCopysEncoding(ConfigTextEncoding encoding)
    {
        var folder = Folder("copy", ("config.lua", Bytes(LuaText, encoding)));

        Ue4ssModConfigCarryOver.ApplyValues(folder, "config.lua", LuaText.Replace("Speed = 5", "Speed = 6"), Path.Combine(_root, "Backups"));

        Assert.Equal(Bytes(LuaText.Replace("Speed = 5", "Speed = 6"), encoding), File.ReadAllBytes(Path.Combine(folder, "config.lua")));
    }

    private string Folder(string side, params (string Path, byte[] Bytes)[] files)
    {
        var folder = Path.Combine(_root, side, "MkHatersTools");
        Directory.CreateDirectory(folder);
        foreach (var (path, bytes) in files)
        {
            File.WriteAllBytes(Path.Combine(folder, path), bytes);
        }

        return folder;
    }
}
