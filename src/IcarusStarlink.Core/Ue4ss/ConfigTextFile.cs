using System.Text;
using IcarusStarlink.Core.IO;

namespace IcarusStarlink.Core.Ue4ss;

/// <summary>How a config file's bytes are written: the encoding and whether it starts with a byte order mark.</summary>
public enum ConfigTextEncoding
{
    /// <summary>UTF-8 with no byte order mark (also what a new file gets).</summary>
    Utf8,
    Utf8Bom,
    Utf16LeBom,
    Utf16BeBom,

    /// <summary>Windows-1252 ("ANSI" in Notepad): bytes that aren't valid UTF-8.</summary>
    Windows1252,

    /// <summary>ISO-8859-1: the fallback for an "ANSI" file whose bytes 1252 can't give back exactly.</summary>
    Latin1,
}

/// <summary>A config file's text and the encoding to write it back in.</summary>
public readonly record struct ConfigText(string Text, ConfigTextEncoding Encoding);

/// <summary>
/// Reads and writes UE4SS config files (a mod's config.lua / config.ini, UE4SS-settings.ini) in the
/// encoding they came in. They used to be read as UTF-8 and written back as UTF-8 with no BOM, so
/// a file saved as ANSI in Notepad came back with "é" turned into "�" after a Save or a mod
/// update, and a leading BOM was dropped on some paths. Detection, in order: UTF-8 BOM, UTF-16 LE
/// or BE BOM, valid UTF-8, else Windows-1252. An unchanged file written back is the same bytes.
/// The files come from downloads: nothing here trusts their content beyond decoding it.
/// </summary>
public static class ConfigTextFile
{
    private static readonly byte[] Utf8Preamble = [0xEF, 0xBB, 0xBF];

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // Not registered app-wide: the provider is asked directly, so nothing else changes how it decodes.
    private static readonly Lazy<Encoding> Windows1252Encoding =
        new(() => CodePagesEncodingProvider.Instance.GetEncoding(1252) ?? Encoding.Latin1);

    /// <summary>Reads <paramref name="path"/>; refuses one bigger than <paramref name="maxBytes"/> when given.</summary>
    /// <exception cref="IOException">The file is over <paramref name="maxBytes"/>, or can't be read.</exception>
    public static ConfigText Read(string path, long? maxBytes = null)
    {
        if (maxBytes is { } max && new FileInfo(path).Length > max)
        {
            throw new IOException($"it's over {max / 1024 / 1024} MB, too big for a config");
        }

        return Decode(File.ReadAllBytes(path));
    }

    /// <summary>The text of a config file's bytes (any byte order mark is not part of it) and how they were encoded.</summary>
    public static ConfigText Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(Utf8Preamble))
        {
            return new ConfigText(Encoding.UTF8.GetString(bytes[3..]), ConfigTextEncoding.Utf8Bom);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return new ConfigText(Encoding.Unicode.GetString(bytes[2..]), ConfigTextEncoding.Utf16LeBom);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return new ConfigText(Encoding.BigEndianUnicode.GetString(bytes[2..]), ConfigTextEncoding.Utf16BeBom);
        }

        try
        {
            return new ConfigText(StrictUtf8.GetString(bytes), ConfigTextEncoding.Utf8);
        }
        catch (DecoderFallbackException)
        {
            // Not UTF-8: an "ANSI" file. 1252 leaves five bytes undefined (0x81, 0x8D, 0x8F, 0x90,
            // 0x9D); when it can't give these bytes back exactly, Latin-1 (every byte is one char) does.
            var ansi = Windows1252Encoding.Value;
            var text = ansi.GetString(bytes);
            return ansi.GetBytes(text).AsSpan().SequenceEqual(bytes)
                ? new ConfigText(text, ConfigTextEncoding.Windows1252)
                : new ConfigText(Encoding.Latin1.GetString(bytes), ConfigTextEncoding.Latin1);
        }
    }

    /// <summary>
    /// The bytes of <paramref name="text"/> in <paramref name="encoding"/>, its byte order mark first.
    /// Text an "ANSI" file can't hold (typed in the editor: "→", an emoji) makes the file UTF-8
    /// instead of turning those characters into "?"; <paramref name="written"/> says which it was.
    /// </summary>
    public static byte[] Encode(string text, ConfigTextEncoding encoding, out ConfigTextEncoding written)
    {
        written = encoding;
        switch (encoding)
        {
            case ConfigTextEncoding.Utf8Bom:
                return [.. Utf8Preamble, .. Encoding.UTF8.GetBytes(text)];
            case ConfigTextEncoding.Utf16LeBom:
                return [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(text)];
            case ConfigTextEncoding.Utf16BeBom:
                return [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes(text)];
            case ConfigTextEncoding.Windows1252 or ConfigTextEncoding.Latin1:
            {
                // A clone, so its fallback can be set: throw on a character it has no byte for.
                var strict = (Encoding)(encoding == ConfigTextEncoding.Latin1 ? Encoding.Latin1 : Windows1252Encoding.Value).Clone();
                strict.EncoderFallback = EncoderFallback.ExceptionFallback;
                try
                {
                    return strict.GetBytes(text);
                }
                catch (EncoderFallbackException)
                {
                    written = ConfigTextEncoding.Utf8;
                    return Encoding.UTF8.GetBytes(text);
                }
            }

            default:
                return Encoding.UTF8.GetBytes(text);
        }
    }

    /// <summary>Writes <paramref name="text"/> in <paramref name="encoding"/> through a temp file (<see cref="AtomicFile"/>). Returns the encoding written.</summary>
    public static ConfigTextEncoding Write(string path, string text, ConfigTextEncoding encoding)
    {
        AtomicFile.WriteAllBytes(path, Encode(text, encoding, out var written));
        return written;
    }

    /// <summary>Writes <paramref name="config"/>'s text back in the encoding it was read in.</summary>
    public static ConfigTextEncoding Write(string path, ConfigText config) => Write(path, config.Text, config.Encoding);
}
