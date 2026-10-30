using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Invoklet.Services.QuickOpen;
using Invoklet.Windows.Healpers;

namespace Invoklet.Windows;

/// <summary>Quick Open: a box that narrows the list of scripts as the user types; closes with the chosen path, or null.</summary>
public partial class QuickOpenDialog : Window
{
    private readonly QuickOpenSession _session;

    public QuickOpenDialog(IReadOnlyList<QuickOpenEntry> entries)
    {
        InitializeComponent();
        BorderlessWindowChrome.Attach(this, DialogSurface);
        _session = new QuickOpenSession(entries);
        QueryBox.TextChanged += (_, _) =>
        {
            _session.SetQuery(QueryBox.Text ?? "");
            ShowResults();
        };
        QueryBox.AddHandler(KeyDownEvent, OnQueryKeyDown, RoutingStrategies.Tunnel);
        ResultList.DoubleTapped += (_, _) => Accept();
        Opened += (_, _) => QueryBox.Focus();
        ShowResults();
    }

    private void ShowResults()
    {
        ResultList.ItemsSource = _session.Results;
        ResultList.SelectedIndex = _session.SelectedIndex;
        if (_session.SelectedIndex >= 0) ResultList.ScrollIntoView(_session.SelectedIndex);
        EmptyText.IsVisible = _session.Results.Count == 0;
    }

    // The arrows and Enter act on the list while the text box keeps the focus, so typing never has to stop.
    private void OnQueryKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                _session.Move(1);
                break;
            case Key.Up:
                _session.Move(-1);
                break;
            case Key.PageDown:
                _session.Move(8);
                break;
            case Key.PageUp:
                _session.Move(-8);
                break;
            case Key.Enter:
                Accept();
                e.Handled = true;
                return;
            case Key.Escape:
                Close(null);
                e.Handled = true;
                return;
            default:
                return;
        }

        ResultList.SelectedIndex = _session.SelectedIndex;
        if (_session.SelectedIndex >= 0) ResultList.ScrollIntoView(_session.SelectedIndex);
        e.Handled = true;
    }

    private void Accept()
    {
        _session.Select(ResultList.SelectedIndex);
        if (_session.Selected is { } entry) Close(entry.Path);
    }
}
