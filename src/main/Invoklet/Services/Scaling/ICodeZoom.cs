namespace Invoklet.Services.Scaling;

/// <summary>The one code zoom every editor shares.</summary>
internal interface ICodeZoom
{
    void ZoomIn();

    void ZoomOut();

    void ResetCodeZoom();
}
