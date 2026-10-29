using Avalonia;
using Avalonia.Controls;
using Invoklet.UserControls;

namespace Invoklet.Windows;

internal sealed partial class BreakpointConditionDialog : Window
{
    private readonly BreakpointConditionEditor _editor;
    private readonly Window _owner;

    public BreakpointConditionDialog(BreakpointConditionEditor editor, Window owner)
    {
        InitializeComponent();
        BorderlessWindowChrome.Attach(this, DialogSurface);
        _editor = editor;
        _owner = owner;
        RequestedThemeVariant = owner.ActualThemeVariant;
        owner.PropertyChanged += OnOwnerPropertyChanged;
        EditorHost.Content = editor;
        _editor.Finished += OnFinished;
        Closed += (_, _) =>
        {
            _owner.PropertyChanged -= OnOwnerPropertyChanged;
            _editor.Finished -= OnFinished;
            _editor.Dispose();
        };
    }

    internal BreakpointConditionEditor ConditionEditor => _editor;

    private void OnFinished((string? Condition, string? HitCondition, bool Enabled)? result)
        => Close(result);

    private void OnOwnerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ThemeVariantScope.ActualThemeVariantProperty)
            RequestedThemeVariant = _owner.ActualThemeVariant;
    }
}
