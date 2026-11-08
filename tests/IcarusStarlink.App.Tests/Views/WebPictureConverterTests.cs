using System.Globalization;
using System.Windows.Media;
using IcarusStarlink.App.Converters;

namespace IcarusStarlink.App.Tests.Views;

/// <summary>Picture links from Nexus or a catalog are data: only a web address becomes an image (no https case: it would start a real download).</summary>
public class WebPictureConverterTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("readme.md")]
    [InlineData(@"C:\Windows\x.png")]
    [InlineData(@"\\evil-host\share\x.png")]
    [InlineData("file:///C:/x.png")]
    [InlineData("ftp://example.com/x.png")]
    public void NotAWebPicture_GivesNoImage(string? value) =>
        Assert.Null(new WebPictureConverter().Convert(value, typeof(ImageSource), null, CultureInfo.InvariantCulture));
}
