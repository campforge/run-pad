using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Invoklet.Services.Preferences;
using Invoklet.UserControls.Models;
using ReactiveUI;

namespace Invoklet.Shared;

// Layout belongs to the workspace, while script contents and execution state belong to tabs.
public sealed class EditorLayoutState : ReactiveObject
{
    private static readonly ConditionalWeakTable<IPreferencesService, EditorLayoutState> Instances = new();
    internal static EditorLayoutState For(IPreferencesService prefs) => Instances.GetValue(prefs, p => new(p));

    public ScriptPreviewMode PreviewMode { get; set => this.RaiseAndSetIfChanged(ref field, value); }
    public GridLength EditorWidth { get; set => this.RaiseAndSetIfChanged(ref field, value); } = GridLength.Star;
    public GridLength PreviewWidth { get; set => this.RaiseAndSetIfChanged(ref field, value); } = GridLength.Star;
    public GridLength StackWidth { get; set => this.RaiseAndSetIfChanged(ref field, value); } = new(2, GridUnitType.Star);
    public GridLength VariablesWidth { get; set => this.RaiseAndSetIfChanged(ref field, value); } = new(3, GridUnitType.Star);
    public GridLength ThreadsHeight { get; set => this.RaiseAndSetIfChanged(ref field, value); } = GridLength.Star;
    public GridLength CallStackHeight { get; set => this.RaiseAndSetIfChanged(ref field, value); } = new(2, GridUnitType.Star);
    public CollapsiblePanelState OutputPanel { get; }

    private EditorLayoutState(IPreferencesService prefs)
    {
        OutputPanel = new(prefs.Layout.DebugPanelCollapsed, prefs.Layout.DebugPanelHeight, 220,
            (collapsed, size) =>
            {
                prefs.Layout.DebugPanelCollapsed = collapsed;
                if (size > 0) prefs.Layout.DebugPanelHeight = size;
                prefs.Save();
            });
    }
}
