using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Styling;
using Invoklet.Avalonia.Themes;
using Invoklet.Format.MdRun;
using Invoklet.UserControls.Models;
using MarkView.Avalonia;
using ReactiveUI;
using TextMateSharp.Grammars;

namespace Invoklet.UserControls;

// The Markdown preview of the script: the document as it would be saved — the front matter as a
// table, the Markdown around the run block, the code highlighted — beside the editor or in its
// place (EditorTabItemViewModel.PreviewMode), following the editor's text as it changes.
public partial class EditorsTabItemControl
{
    // Long enough that typing doesn't re-render the document on every key, short enough to feel live.
    internal static readonly TimeSpan PreviewUpdateDelay = TimeSpan.FromMilliseconds(300);

    private MarkdownViewer? _preview;
    private ThemeName _previewTheme;
    private CancellationTokenSource? _previewUpdateCts;
    private IDisposable? _previewModeSub;
    private ScriptPreviewMode _lastPreviewMode;

    // Test-only (InternalsVisibleTo): the viewer, once the preview was first shown.
    internal MarkdownViewer? PreviewForTests => _preview;

    private void SetupPreview()
    {
        _previewTheme = _themeService.CurrentTheme.Editor.TextMateName;
        State.Editor.TextChanged += OnEditorTextChangedForPreview;
        _previewModeSub = _vm.WhenAnyValue(x => x.PreviewMode).Subscribe(ApplyPreviewMode);
        SetupPreviewScrollSync();
    }

    private void DisposePreview()
    {
        State.Editor.TextChanged -= OnEditorTextChangedForPreview;
        DisposePreviewScrollSync();
        _previewModeSub?.Dispose();
        _previewUpdateCts?.Cancel();
        _previewUpdateCts?.Dispose();
    }

    private bool PreviewShown => _vm.PreviewMode != ScriptPreviewMode.Off;

    private void ApplyPreviewMode(ScriptPreviewMode mode)
    {
        var shown = mode != ScriptPreviewMode.Off;
        // Out of the preview alone, the editor picks up where the preview was read to.
        var previewLine = _lastPreviewMode == ScriptPreviewMode.Full && mode != ScriptPreviewMode.Full
            ? _preview?.GetSourceLineAtTop()
            : null;
        if (shown && _preview is null)
        {
            // A script is mostly one code block, often long: it is highlighted as it scrolls into view.
            _preview = new MarkdownViewer { Padding = new(16, 8), LazyCodeBlocks = true };
            _preview.ScrollChanged += OnPreviewScrolled;
            ApplyPreviewTheme();
            ApplyPreviewCodeFont();
            PreviewHost.Content = _preview;
        }

        // Definitions retain the workspace split even while one pane is hidden.
        var columns = EditorArea.ColumnDefinitions;
        columns[0].MaxWidth = mode == ScriptPreviewMode.Full ? 0 : double.PositiveInfinity;
        columns[2].MaxWidth = shown ? double.PositiveInfinity : 0;
        EditorHost.IsVisible = mode != ScriptPreviewMode.Full;
        PreviewSplitter.IsVisible = mode == ScriptPreviewMode.Side;
        PreviewHost.IsVisible = shown;

        if (previewLine is { } line)
        {
            // The editor was hidden: it needs its size before it can scroll.
            State.Editor.UpdateLayout();
            ScrollEditorToLine(EditorLineFor(line));
        }
        if (shown) UpdatePreviewNow(previewLine);
        // Back from the preview alone, the editor was hidden and so lost the focus.
        else if (_lastPreviewMode == ScriptPreviewMode.Full) State.Editor.TextArea.Focus();
        _lastPreviewMode = mode;
    }

    private void OnEditorTextChangedForPreview(object? sender, EventArgs e)
    {
        if (!PreviewShown) return;
        _previewUpdateCts?.Cancel();
        _previewUpdateCts?.Dispose();
        var cts = _previewUpdateCts = new CancellationTokenSource();
        _ = DelayedPreviewUpdateAsync(cts.Token);
    }

    private async Task DelayedPreviewUpdateAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(PreviewUpdateDelay, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        // This control's own Dispatcher, as for the other debounces (see the constructor's note).
        Dispatcher.Post(() =>
        {
            if (!ct.IsCancellationRequested && !_disposed) UpdatePreviewNow();
        });
    }

    // Also after the front matter changed (kind, Dependencies), which the editor text doesn't show.
    // A new document opens at the top, so the preview is scrolled back to where it was read: the
    // editor's top line, or in the preview alone its own (previewLine, when the caller knows it).
    private void UpdatePreviewNow(double? previewLine = null)
    {
        _previewUpdateCts?.Cancel();
        if (_preview is null || !PreviewShown) return;
        if (previewLine is null && _vm.PreviewMode == ScriptPreviewMode.Full && _lastPreviewMode == ScriptPreviewMode.Full)
            previewLine = _preview.GetSourceLineAtTop();
        _preview.BaseUri = PreviewBaseUri(_vm.FilePath);
        _preview.Markdown = PreviewMarkdown(out _previewCodeLine);
        _preview.ScrollToSourceLine(previewLine ?? PreviewLineFor(EditorTopLine()));
    }

    internal string PreviewMarkdown() => PreviewMarkdown(out _);

    // A file that isn't a valid .inv has no MdRunDocument and is saved back verbatim; it is shown
    // as the Markdown it is, line for line.
    private string PreviewMarkdown(out int codeLine)
    {
        codeLine = 0;
        return State.MdRunDocument is { } rpad ? MdRunPreview.ToMarkdown(rpad, State.Editor.Text, out codeLine) : State.Editor.Text;
    }

    // Relative image links resolve against the script's folder; an untitled tab has none.
    private static Uri? PreviewBaseUri(string? filePath)
        => string.IsNullOrEmpty(filePath) || Path.GetDirectoryName(Path.GetFullPath(filePath)) is not { } folder
            ? null
            : new Uri(Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar);

    private void ApplyPreviewThemeNow(ActiveTheme theme)
    {
        _previewTheme = theme.Editor.TextMateName;
        if (_preview is null) return;
        ApplyPreviewTheme();
        if (!PreviewShown) return;
        // Extensions are read when the document renders; a new Markdown value renders it again.
        var line = _vm.PreviewMode == ScriptPreviewMode.Full ? _preview.GetSourceLineAtTop() : null;
        _preview.Markdown = null;
        UpdatePreviewNow(line);
    }

    // Code in the preview is in the editor's font, not the viewer's own list of code fonts, which may
    // all be missing (and the editor font follows the user's setting).
    private void ApplyPreviewCodeFont()
    {
        if (_preview is null) return;
        var font = State.Editor.FontFamily;
        _preview.Styles.Clear();
        _preview.Styles.Add(new Style(x => x.OfType<Border>().Class("markdown-code-block").Descendant().OfType<TextBlock>())
        {
            Setters = { new Setter(TextBlock.FontFamilyProperty, font) },
        });
        _preview.Styles.Add(new Style(x => x.OfType<Run>().Class("markdown-code-inline"))
        {
            Setters = { new Setter(TextElement.FontFamilyProperty, font) },
        });
    }

    // The code blocks use the editor's TextMate theme, in light and dark variants alike, and the
    // page the editor's background.
    private void ApplyPreviewTheme()
    {
        _preview!.Background = _themeService.GetBrush("Invoklet.Editor.BackgroundBrush");
        _preview.Extensions.Clear();
        _preview.UseTextMateHighlighting(darkTheme: _previewTheme, lightTheme: _previewTheme);
    }
}
