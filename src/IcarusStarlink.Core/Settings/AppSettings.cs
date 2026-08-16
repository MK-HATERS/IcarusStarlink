namespace IcarusStarlink.Core.Settings;

public sealed class AppSettings
{
    public string? IcarusContentPath { get; set; }
    public string? UnrealPakExePath { get; set; }
    public string ThemeName { get; set; } = "Icarus";
    public bool PerformanceTrackingEnabled { get; set; }
}
