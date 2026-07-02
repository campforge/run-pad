#nullable disable
using DotPad.Avalonia.HtmlRenderer.External.Adapters.Entities;

namespace DotPad.Avalonia.HtmlRenderer.External.Adapters;

public abstract class RFormattedLine
{
    public abstract int Length { get; }

    public abstract RSize Size { get; }
}