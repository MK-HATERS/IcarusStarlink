namespace IcarusStarlink.PakIO.Assets;

/// <summary>
/// The result of attempting to decode a real Unreal USoundWave into playable audio. Unlike
/// IUassetTextureDecoder/IUassetStaticMeshDecoder/IUassetSkeletalMeshDecoder's plain "null means
/// not this asset type, or couldn't decode" contract, a sound asset can be positively identified as
/// a real USoundWave while still being stored in a compressed format this app has no safe way to
/// hand to WPF's MediaElement (see CueUassetSoundDecoder's own doc comment on SoundDecoder.Decode's
/// real format outputs) — so a non-null result here distinguishes "here's a playable WAV" from
/// "this really is a sound, just not one previewable in-app today", instead of collapsing both into
/// the same null the other decoders use for "not this asset type at all".
/// </summary>
public sealed record UassetSoundAudio(byte[]? WavBytes, string? UnsupportedFormatReason)
{
    /// <summary>WavBytes is a complete, directly-playable RIFF/WAVE file — never headerless raw PCM (see CueUassetSoundDecoder's own doc comment on why SoundDecoder.Decode never hands back the latter for the one format this treats as playable).</summary>
    public static UassetSoundAudio Decoded(byte[] wavBytes) => new(wavBytes, null);

    public static UassetSoundAudio Unsupported(string reason) => new(null, reason);
}
