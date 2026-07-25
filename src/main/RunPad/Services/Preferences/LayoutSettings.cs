namespace DotPad.Services.Preferences;

public sealed class LayoutSettings
{
    public double SidebarWidth { get; set; } = 300;
    public bool SidebarCollapsed { get; set; }
    public double DebugPanelHeight { get; set; } = 220;
    public bool DebugPanelCollapsed { get; set; }
}
