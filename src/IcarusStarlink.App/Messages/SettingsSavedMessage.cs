namespace IcarusStarlink.App.Messages;

/// <summary>
/// Broadcast when Settings' own explicit Save runs — the one place IcarusContentPath/
/// UnrealPakExePath change. Deliberately NOT sent by the various silent auto-saves scattered
/// elsewhere (column preferences, window bounds, theme) — those never change anything another
/// page derives state from, and sending on every one of them would just be noise. First consumer:
/// MergeInstallViewModel re-checking whether this app's pak is installed under the (possibly just
/// changed) Content path, so its Install/"Update install" label doesn't go stale until restart.
/// </summary>
public sealed record SettingsSavedMessage;
