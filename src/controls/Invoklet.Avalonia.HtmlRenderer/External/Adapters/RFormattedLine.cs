#nullable disable
using Invoklet.Avalonia.HtmlRenderer.External.Adapters.Entities;

namespace Invoklet.Avalonia.HtmlRenderer.External.Adapters;

public abstract class RFormattedLine
{
    public abstract int Length { get; }

    public abstract RSize Size { get; }
}