namespace IcarusStarlink.App.Utilities;

/// <summary>The Version column's text: "v1.2.0", or "" when there's no version — a bare "v" on its own read as a glitch in a real screenshot. A version that isn't a number ("Beta") is shown as written, not as "vBeta".</summary>
public static class VersionLabel
{
    public static string For(string? version)
    {
        var trimmed = version?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return "";
        }

        // Some mods already write "v1.0" themselves — don't make it "vv1.0".
        return char.IsDigit(trimmed[0]) ? $"v{trimmed}" : trimmed;
    }
}
