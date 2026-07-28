using Avalonia.Media.TextFormatting;
using RunPad.Avalonia.HtmlRenderer.External.Adapters;
using RunPad.Avalonia.HtmlRenderer.External.Adapters.Entities;

namespace RunPad.Avalonia.HtmlRenderer.Adapters;

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
