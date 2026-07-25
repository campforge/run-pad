using Avalonia.Media.TextFormatting;
using DotPad.Avalonia.HtmlRenderer.External.Adapters;
using DotPad.Avalonia.HtmlRenderer.External.Adapters.Entities;

namespace DotPad.Avalonia.HtmlRenderer.Adapters;

public class FormattedLineAdapter : RFormattedLine
{
    public TextLine TextLine { get; }

    public FormattedLineAdapter(TextLine textLine)
    {
        TextLine = textLine;
    }

    public override int Length => TextLine.Length;
    public override RSize Size => new RSize(TextLine.WidthIncludingTrailingWhitespace, TextLine.Height);
}
