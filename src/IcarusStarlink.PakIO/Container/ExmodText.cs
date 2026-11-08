using System.Text;

namespace IcarusStarlink.PakIO.Container;

/// <summary>
/// Turns an .EXMOD's bytes into its JSON text. Most are UTF-8 (some with a BOM); one saved as
/// "ANSI" in Notepad is Windows-1252, where smart quotes are the single bytes 0x93/0x94. Read as
/// UTF-8 those became U+FFFD, and saving the mod wrote the replacement characters back for good,
/// so bytes that aren't valid UTF-8 are read as Windows-1252 instead.
/// </summary>
internal static class ExmodText
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly Lazy<Encoding> Windows1252 =
        new(() => CodePagesEncodingProvider.Instance.GetEncoding(1252) ?? Encoding.Latin1);

    public static string Decode(byte[] bytes)
    {
        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            text = Windows1252.Value.GetString(bytes);
        }

        // A leading UTF-8 BOM some tools write.
        return text.TrimStart('﻿');
    }
}
