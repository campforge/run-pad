using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Invoklet.Services.QuickOpen;
using Invoklet.Windows.Healpers;

namespace Invoklet.Windows;

/// <summary>
/// Quick Open: a box that narrows the scripts, the samples and, on request, the other files as the user types, with the folder and the
/// last write time of each. It can also look in the text of the scripts. Closes with the chosen entry, or null.
/// </summary>
public partial class QuickOpenDialog : Window
{
    private readonly QuickOpenSession _session;
    private readonly QuickOpenContent _content;
    private CancellationTokenSource? _contentSearch;
    private bool _otherFilesLoaded;

    public QuickOpenDialog(QuickOpenContent content)
    {
        InitializeComponent();
        BorderlessWindowChrome.Attach(this, DialogSurface);
        _content = content;
        _session = new QuickOpenSession(content.Entries);
        TitleText.Text = content.KeyText.Length == 0 ? "Quick Open" : $"Quick Open ({content.KeyText})";

        QueryBox.TextChanged += (_, _) => OnQueryChanged();
        QueryBox.AddHandler(KeyDownEvent, OnQueryKeyDown, RoutingStrategies.Tunnel);
        ScriptsCheck.IsCheckedChanged += (_, _) => Filter(() => _session.ShowScripts = ScriptsCheck.IsChecked == true);
        SamplesCheck.IsCheckedChanged += (_, _) => Filter(() => _session.ShowSamples = SamplesCheck.IsChecked == true);
        SearchInsideCheck.IsCheckedChanged += (_, _) =>
        {
            Filter(() => _session.SearchInside = SearchInsideCheck.IsChecked == true);
            StartContentSearch();
        };
        OtherFilesCheck.IsCheckedChanged += async (_, _) => await OnOtherFilesChangedAsync();
        ResultList.DoubleTapped += (_, _) => Accept();
        Opened += (_, _) => QueryBox.Focus();
        Closed += (_, _) => _contentSearch?.Cancel();
        ShowResults();
    }

    private void OnQueryChanged()
    {
        _session.SetQuery(QueryBox.Text ?? "");
        ClearButton.IsVisible = !string.IsNullOrEmpty(QueryBox.Text);
        ShowResults();
        StartContentSearch();
    }

    private void Filter(Action change)
    {
        change();
        ShowResults();
    }

    private async Task OnOtherFilesChangedAsync()
    {
        var include = OtherFilesCheck.IsChecked == true;
        if (include && !_otherFilesLoaded)
        {
            _otherFilesLoaded = true;
            try
            {
                _session.AddEntries(await _content.LoadOtherFilesAsync(CancellationToken.None));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The folder could not be read: the box goes on with what it has.
            }
        }

        _session.IncludeOtherFiles = include;
        ShowResults();
        StartContentSearch();
    }

    // What the text of the scripts holds is looked up after a short pause, off the UI thread, and replaced by the next text typed.
    private async void StartContentSearch()
    {
        _contentSearch?.Cancel();
        var query = QueryBox.Text ?? "";
        if (!_session.SearchInside || query.Trim().Length == 0) return;

        var source = _contentSearch = new CancellationTokenSource();
        try
        {
            await Task.Delay(200, source.Token);
            var found = await Task.Run(() => QuickOpenContentSearch.FindAsync(
                _session.ContentCandidates, query, _content.ReadTextAsync, source.Token), source.Token);
            _session.SetContentMatches(query, found);
            ShowResults();
        }
        catch (OperationCanceledException)
        {
            // A newer text, or the box closed.
        }
    }

    private void ShowResults()
    {
        ResultList.ItemsSource = _session.Results;
        ResultList.SelectedIndex = _session.SelectedIndex;
        if (_session.SelectedIndex >= 0) ResultList.ScrollIntoView(_session.SelectedIndex);
        EmptyText.IsVisible = _session.Results.Count == 0;
        OpenButton.IsEnabled = _session.Results.Count > 0;
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
        if (_session.Selected is { } entry) Close(entry);
    }

    private void OpenButton_Click(object? sender, RoutedEventArgs e) => Accept();

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close(null);

    private void ClearButton_Click(object? sender, RoutedEventArgs e)
    {
        QueryBox.Text = "";
        QueryBox.Focus();
    }
}
