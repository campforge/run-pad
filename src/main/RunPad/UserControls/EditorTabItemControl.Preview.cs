using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Styling;
using MarkView.Avalonia;
using MarkView.Avalonia.SyntaxHighlighting;
using ReactiveUI;
using RunPad.Avalonia.Themes;
using RunPad.Format.RPad;
using RunPad.UserControls.Models;
using TextMateSharp.Grammars;

namespace RunPad.UserControls;

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
    }

    private void DisposePreview()
    {
        State.Editor.TextChanged -= OnEditorTextChangedForPreview;
        _previewModeSub?.Dispose();
        _previewUpdateCts?.Cancel();
        _previewUpdateCts?.Dispose();
    }

    private bool PreviewShown => _vm.PreviewMode != ScriptPreviewMode.Off;

    private void ApplyPreviewMode(ScriptPreviewMode mode)
    {
        var shown = mode != ScriptPreviewMode.Off;
        if (shown && _preview is null)
        {
            _preview = new MarkdownViewer { Padding = new(16, 8) };
            ApplyPreviewTheme();
            ApplyPreviewCodeFont();
            PreviewHost.Content = _preview;
        }

        // Each mode starts from an even split; a dragged splitter only lasts until the next switch.
        var columns = EditorArea.ColumnDefinitions;
        columns[0].Width = mode == ScriptPreviewMode.Full ? new GridLength(0) : GridLength.Star;
        columns[2].Width = shown ? GridLength.Star : new GridLength(0);
        EditorHost.IsVisible = mode != ScriptPreviewMode.Full;
        PreviewSplitter.IsVisible = mode == ScriptPreviewMode.Side;
        PreviewHost.IsVisible = shown;

        if (shown) UpdatePreviewNow();
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
    private void UpdatePreviewNow()
    {
        _previewUpdateCts?.Cancel();
        if (_preview is null || !PreviewShown) return;
        _preview.BaseUri = PreviewBaseUri(_vm.FilePath);
        _preview.Markdown = PreviewMarkdown();
    }

    // A file that isn't a valid .rpad has no RpadDocument and is saved back verbatim; it is shown
    // as the Markdown it is.
    internal string PreviewMarkdown()
        => State.RpadDocument is { } rpad ? RpadPreview.ToMarkdown(rpad, State.Editor.Text) : State.Editor.Text;

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
        _preview.Markdown = null;
        UpdatePreviewNow();
    }

    // Code in the preview is in the editor's font, not the viewer's own list of code fonts, which may
    // all be missing (and the editor font follows the user's setting).
    private void ApplyPreviewCodeFont()
    {
        if (_preview is null) return;
        var font = State.Editor.FontFamily;
        _preview.Styles.Clear();
        _preview.Styles.Add(new Style(x => x.OfType<Border>().Class("markdown-code-block").Child().OfType<TextBlock>())
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
        _preview!.Background = _themeService.GetBrush("RunPad.Editor.BackgroundBrush");
        _preview.Extensions.Clear();
        _preview.UseTextMateHighlighting(darkTheme: _previewTheme, lightTheme: _previewTheme);
    }
}
