using Avalonia.Controls;
using Avalonia.Input;
using RunPad.Contracts.IntelliSense;

namespace RunPad.UserControls;

public partial class BreakpointConditionEditor : UserControl, IDisposable
{
    private readonly Func<string, CancellationToken, Task<BreakpointConditionAnalysis?>> _analyze;
    private CancellationTokenSource? _pending;
    private bool _disposed;
    private bool _valid;
    private readonly string? _hitCondition;
    public event Action<(string? Condition, string? HitCondition, bool Enabled)?>? Finished;

    public BreakpointConditionEditor() : this(null, null, true, (_, _) => Task.FromResult<BreakpointConditionAnalysis?>(null)) { }

    public BreakpointConditionEditor(string? condition, string? hitCount, bool enabled,
        Func<string, CancellationToken, Task<BreakpointConditionAnalysis?>> analyze)
    {
        InitializeComponent();
        _analyze = analyze;
        ConditionBox.Text = condition ?? "";
        _hitCondition = hitCount;
        EnabledBox.IsChecked = enabled;
        ConditionBox.TextChanged += (_, _) => _ = ValidateAsync();
        DoneButton.Click += (_, _) => Apply();
        Suggestions.DoubleTapped += (_, _) => AcceptSuggestion();
        AddHandler(KeyDownEvent, OnKeyDown, global::Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Loaded += (_, _) =>
        {
            ConditionBox.Focus();
            ConditionBox.SelectAll();
            _ = ValidateAsync();
        };
    }

    private async Task ValidateAsync()
    {
        _pending?.Cancel();
        _pending?.Dispose();
        var pending = _pending = new CancellationTokenSource();
        Suggestions.ItemsSource = null;
        Suggestions.IsVisible = false;
        _valid = false;
        DoneButton.IsEnabled = false;
        try
        {
            await Task.Delay(150, pending.Token);
            var result = await _analyze(ConditionBox.Text ?? "", pending.Token);
            if (_disposed || pending.IsCancellationRequested) return;
            var error = result?.Error;
            ErrorText.Text = error ?? (result is null ? "Validation is unavailable for this language." : "");
            ErrorText.IsVisible = !string.IsNullOrEmpty(ErrorText.Text);
            Suggestions.ItemsSource = result?.Suggestions;
            Suggestions.IsVisible = result?.Suggestions.Count > 0 && !string.IsNullOrWhiteSpace(ConditionBox.Text);
            _valid = error is null;
            DoneButton.IsEnabled = _valid;
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (_disposed || pending.IsCancellationRequested) return;
            ErrorText.Text = "Could not validate the condition. Try editing it again.";
            ErrorText.IsVisible = true;
        }
    }

    private void Apply()
    {
        if (_valid && !_disposed)
            Finished?.Invoke((ConditionBox.Text, _hitCondition, EnabledBox.IsChecked ?? true));
    }

    private void AcceptSuggestion()
    {
        if (Suggestions.SelectedItem is not string name) return;
        var text = ConditionBox.Text ?? "";
        var start = text.Length;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_')) start--;
        ConditionBox.Text = text[..start] + name;
        ConditionBox.CaretIndex = ConditionBox.Text.Length;
        ConditionBox.Focus();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Finished?.Invoke(null); e.Handled = true; }
        else if (e.Key == Key.Down && Suggestions.IsVisible)
        {
            Suggestions.SelectedIndex = Math.Min(Suggestions.SelectedIndex + 1, Suggestions.ItemCount - 1);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            if (Suggestions.SelectedItem is not null) AcceptSuggestion();
            else Apply();
            e.Handled = true;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _pending?.Cancel();
        _pending?.Dispose();
        _pending = null;
    }
}
