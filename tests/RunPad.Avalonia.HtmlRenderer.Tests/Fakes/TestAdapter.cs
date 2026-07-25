using DotPad.Avalonia.HtmlRenderer.External.Adapters;
using DotPad.Avalonia.HtmlRenderer.External.Adapters.Entities;

namespace DotPad.Avalonia.HtmlRenderer.Tests.Fakes;

internal sealed class TestAdapter : RAdapter
{
    protected override RColor GetColorInt(string colorName) => colorName.ToLowerInvariant() switch
    {
        "black" => RColor.Black,
        "white" => RColor.White,
        "transparent" => RColor.Transparent,
        "red" => RColor.FromArgb(255, 0, 0),
        "green" => RColor.FromArgb(0, 128, 0),
        "blue" => RColor.FromArgb(0, 0, 255),
        _ => RColor.Empty
    };

    protected override RPen CreatePen(RColor color) => throw new NotSupportedException();
    protected override RBrush CreateSolidBrush(RColor color) => throw new NotSupportedException();
    protected override RBrush CreateLinearGradientBrush(RRect rect, RColor color1, RColor color2, double angle) => throw new NotSupportedException();
    protected override RImage ConvertImageInt(object image) => throw new NotSupportedException();
    protected override RImage ImageFromStreamInt(Stream memoryStream) => throw new NotSupportedException();
    protected override RFont CreateFontInt(string family, double size, RFontStyle style) => throw new NotSupportedException();
    protected override RFont CreateFontInt(RFontFamily family, double size, RFontStyle style) => throw new NotSupportedException();
}
