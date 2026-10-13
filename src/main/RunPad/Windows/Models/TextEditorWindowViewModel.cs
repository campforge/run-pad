using System.ComponentModel;
using System.Reactive;
using System.Text;
using AvaloniaEdit.Document;
using ReactiveUI;
using RunPad.Services;
using RunPad.Services.TextFiles;

namespace RunPad.Windows.Models;

/// <summary>
/// The text editor window: one whole file, saved back
/// in the encoding it was read in (<see cref="TextFileCodec"/>). Dirty state follows the undo stack, so
/// undoing back to the saved text clears it. A failed save shows an error and keeps the text, the
/// file path and the dirty state exactly as they were.
/// </summary>
public sealed class TextEditorWindowViewModel : ReactiveObject
{
    private readonly IWindowService _windowService;
    private readonly Encoding _encoding;
    private readonly bool _hasBom;

    public TextEditorWindowViewModel(string filePath, TextFileContent content, IWindowService windowService)
    {
        _windowService = windowService;
        _encoding = content.Encoding;
        _hasBom = content.HasBom;
        FilePath = Path.GetFullPath(filePath);
        EncodingLabel = content.EncodingLabel;
        NewLineLabel = TextFileCodec.NewLineLabel(content.Text);

        Document = new TextDocument(content.Text) { FileName = FilePath };
        Document.UndoStack.MarkAsOriginalFile();
        Document.UndoStack.PropertyChanged += OnUndoStackChanged;

        SaveCommand = ReactiveCommand.CreateFromTask(SaveAsync);
        SaveAsCommand = ReactiveCommand.CreateFromTask(SaveAsAsync);
    }

    /// <summary>Reads <paramref name="filePath"/>; throws (IOException, ...) when it can't be read.</summary>
    public static async Task<TextEditorWindowViewModel> LoadAsync(string filePath, IWindowService windowService) =>
        new(filePath, await TextFileCodec.LoadAsync(filePath), windowService);

    public TextDocument Document { get; }

    public string FilePath
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(FileName));
            this.RaisePropertyChanged(nameof(Title));
        }
    } = "";

    public string FileName => Path.GetFileName(FilePath);

    public bool IsDirty
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(Title));
        }
    }

    /// <summary>"notes.txt — RunPad", with a "*" while there are unsaved changes.</summary>
    public string Title => $"{FileName}{(IsDirty ? "*" : "")} — RunPad";

    public string EncodingLabel { get; }

    /// <summary>The file's line breaks as last loaded/saved: CRLF, LF, CR, Mixed (empty: none).</summary>
    public string NewLineLabel
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public ReactiveCommand<Unit, bool> SaveCommand { get; }
    public ReactiveCommand<Unit, bool> SaveAsCommand { get; }

    public Task<bool> SaveAsync() => WriteAsync(FilePath);

    /// <summary>Asks for a new path; the window follows the file to it only once it's written.</summary>
    public async Task<bool> SaveAsAsync()
    {
        var path = await _windowService.ShowSaveTextFileDialogAsync(FileName, Path.GetDirectoryName(FilePath) ?? "");
        return path is not null && await WriteAsync(Path.GetFullPath(path));
    }

    /// <summary>
    /// Before the window closes: true to close. Unsaved changes ask Save / Don't Save / Cancel; a Save
    /// that fails keeps the window open (its text is not lost).
    /// </summary>
    public async Task<bool> ConfirmCloseAsync()
    {
        if (!IsDirty) return true;
        return await _windowService.ShowUnsavedChangesDialogAsync(FileName) switch
        {
            UnsavedChangesResult.Save => await SaveAsync(),
            UnsavedChangesResult.DontSave => true,
            _ => false,
        };
    }

    private async Task<bool> WriteAsync(string path)
    {
        var text = Document.Text;
        try
        {
            await TextFileCodec.SaveAsync(path, text, _encoding, _hasBom);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
                                       or EncoderFallbackException)
        {
            await _windowService.ShowErrorAsync("Save Failed", $"Could not save '{path}':\n{ex.Message}");
            return false;
        }

        FilePath = path;
        Document.FileName = path;
        NewLineLabel = TextFileCodec.NewLineLabel(text);
        Document.UndoStack.MarkAsOriginalFile();
        return true;
    }

    private void OnUndoStackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UndoStack.IsOriginalFile))
            IsDirty = !Document.UndoStack.IsOriginalFile;
    }
}
