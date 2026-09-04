using CUE4Parse.UE4.Assets.Exports.Sound;
using CUE4Parse_Conversion.Sounds;

namespace IcarusStarlink.PakIO.Assets;

/// <summary>
/// Real UE4.27 sound parsing via CUE4Parse-Conversion's own SoundDecoder.Decode(this USoundWave,
/// bool shouldDecompress, out string audioFormat, out byte[]? data) — confirmed by reading its
/// actual source (CUE4Parse-Conversion/Sounds/SoundDecoder.cs, package version 1.2.2.202608, not
/// guessed): the audioFormat string it hands back is genuinely one of several real values, and only
/// one of them is safely playable here —
///   - "WAV": the underlying platform-format data SoundDecoder read was already a complete real
///     RIFF/WAVE container storing uncompressed PCM — SoundDecoder's own Decompress step confirms
///     this by actually parsing a WAVE "fmt " chunk out of the bytes (via ADPCMDecoder.GetAudioFormat)
///     before it ever renames audioFormat to "WAV". The bytes it hands back are that same complete
///     container verbatim — nothing here (or in SoundDecoder itself) wraps raw/headerless PCM in a
///     synthesized WAV header, because SoundDecoder never actually hands back raw headerless PCM in
///     the first place.
///   - "PCM"/"ADPCM": also a real RIFF/WAVE container (same code path as "WAV" above), but its own
///     fmt-chunk format code came back ADPCM rather than PCM (or the format code was something
///     Decompress itself couldn't even recognize, in which case it returns null data entirely).
///     Plausibly still playable by Windows Media Foundation's own built-in ADPCM codec, but that
///     was never actually verified against a real decoded asset, so treated as unsupported here
///     rather than guessed.
///   - "OGG"/"WEM"/"BINKA"/"RADA"/"OPUS"/"AT9": a real compressed audio stream in its own native
///     container (Ogg Vorbis / Wwise / Bink Audio / Radical / Opus / Sony ATRAC9) — none of these
///     are a WAV file, and Windows has no universally-guaranteed built-in decoder for most of them
///     (Ogg Vorbis in particular commonly needs a separate codec pack most machines don't have) —
///     always treated as unsupported here rather than risking a silent, soundless MediaElement with
///     no clear reason why. Most of Icarus's own real sound content is very likely "OGG" (the usual
///     UE4 desktop-platform default for a USoundWave that isn't using Wwise), which is exactly why
///     this scope limitation is worth a human's real live test against real extracted sound assets
///     — see this project's own top-level report on that.
/// </summary>
public sealed class CueUassetSoundDecoder : IUassetSoundDecoder
{
    public UassetSoundAudio? TryDecodeAudio(string modFolderPath, string relativeAssetPath)
    {
        if (!string.Equals(Path.GetExtension(relativeAssetPath), ".uasset", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            var soundWave = CueAssetProviderLocator.TryLoadExport<USoundWave>(modFolderPath, relativeAssetPath);
            if (soundWave is null)
            {
                return null;
            }

            soundWave.Decode(shouldDecompress: true, out var audioFormat, out var data);
            if (data is null)
            {
                return UassetSoundAudio.Unsupported("this sound's own audio data couldn't be read");
            }

            return string.Equals(audioFormat, "WAV", StringComparison.OrdinalIgnoreCase)
                ? UassetSoundAudio.Decoded(data)
                : UassetSoundAudio.Unsupported(
                    $"this sound is stored as {audioFormat} — only uncompressed WAV/PCM sound data can be previewed in-app right now");
        }
        catch (Exception)
        {
            // A texture, mesh, blueprint, material, or a genuinely corrupt/unsupported asset all
            // land here — same "no preview available" fallback every other decoder here uses.
            return null;
        }
    }
}
