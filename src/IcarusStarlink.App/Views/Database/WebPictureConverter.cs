using System.Globalization;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace IcarusStarlink.App.Converters;

/// <summary>
/// A picture's web address (a Nexus card's picture, an IMM Database mod's image) as an image
/// decoded at DecodePixelWidth. Binding the address straight to Image.Source decodes the whole
/// picture: a Nexus header is often 1920 px wide (about 8 MB in memory) for a card 150 px tall, and
/// a catalog's image link can name a picture of any size. WPF still downloads it in the background.
/// Anything but an http(s) address, or one WPF can't start loading, gives no image, so the
/// placeholder behind it shows.
/// </summary>
public sealed class WebPictureConverter : IValueConverter
{
    /// <summary>The width the picture is decoded at; its height follows its shape.</summary>
    public int DecodePixelWidth { get; set; } = 640;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string text
            || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || uri.IsUnc)
        {
            return null;
        }

        try
        {
            // Only the width is set, so the picture keeps its shape. Not frozen: that throws while it downloads.
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = uri;
            bitmap.DecodePixelWidth = DecodePixelWidth;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            return bitmap;
        }
        catch (Exception)
        {
            return null; // only for looking at: the placeholder shows instead
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
