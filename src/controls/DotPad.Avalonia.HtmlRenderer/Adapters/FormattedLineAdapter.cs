using Avalonia.HtmlRenderer.External.Adapters;
using Avalonia.HtmlRenderer.External.Adapters.Entities;
using Avalonia.Media.TextFormatting;

namespace Avalonia.HtmlRenderer.Adapters;

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