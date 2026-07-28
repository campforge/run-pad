#nullable disable
using RunPad.Avalonia.HtmlRenderer.External.Adapters.Entities;

namespace RunPad.Avalonia.HtmlRenderer.External.Adapters;

public abstract class RFormattedLine
{
    public abstract int Length { get; }

    public abstract RSize Size { get; }
}