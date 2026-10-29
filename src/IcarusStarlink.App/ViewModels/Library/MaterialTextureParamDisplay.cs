using System.Windows.Media.Imaging;

namespace IcarusStarlink.App.ViewModels;

/// <summary>One texture-valued material parameter, ready for the Files tab's own Image control — the
/// WPF counterpart of PakIO's own MaterialTextureParam, whose PngBytes this decodes exactly once
/// (via LibraryItemViewModel's own TryDecodeImage) rather than the XAML layer needing a byte[]-to-
/// BitmapImage converter of its own.</summary>
public sealed record MaterialTextureParamDisplay(string Name, BitmapImage Thumbnail);
