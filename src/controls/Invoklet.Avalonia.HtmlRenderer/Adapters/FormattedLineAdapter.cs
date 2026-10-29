using Avalonia.Media.TextFormatting;
using Invoklet.Avalonia.HtmlRenderer.External.Adapters;
using Invoklet.Avalonia.HtmlRenderer.External.Adapters.Entities;

namespace Invoklet.Avalonia.HtmlRenderer.Adapters;

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
