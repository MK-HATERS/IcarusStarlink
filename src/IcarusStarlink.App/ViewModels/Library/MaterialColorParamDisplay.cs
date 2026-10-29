using System.Windows.Media;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// One color material parameter, ready for a small Border/Rectangle swatch plus a readable text
/// value. SwatchColor is a straightforward linear-to-byte mapping (each channel clamped to 0..1,
/// then scaled to 0..255) — not a true sRGB gamma correction, since this is a plain parameter-list
/// swatch, not meant to be colorimetrically exact the way an in-engine material preview would be.
/// </summary>
public sealed record MaterialColorParamDisplay(string Name, Color SwatchColor, string DisplayText);
