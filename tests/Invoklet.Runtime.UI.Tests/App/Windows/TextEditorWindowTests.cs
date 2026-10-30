using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Invoklet.Avalonia.Themes;
using Invoklet.Services;
using Invoklet.Services.Preferences;
using Invoklet.Windows;
using Invoklet.Windows.Models;
using Moq;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

// The text editor window:
// one whole file per window; Save / Save As keep its encoding and line breaks; unsaved changes are
// shown, and closing asks Save / Don't Save / Cancel; a failed write keeps the window and its text.
public sealed class TextEditorWindowTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("Invoklet-text-editor").FullName;
    private readonly Mock<IWindowService> _windowService = new();

    public TextEditorWindowTests()
    {
        _windowService.Setup(s => s.ShowErrorAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);
    }

    public void Dispose()
    {
        foreach (var file in Directory.GetFiles(_folder)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_folder, recursive: true);
    }

    private static Mock<IThemeService> Theme()
    {
        var theme = new Mock<IThemeService>();
        theme.Setup(t => t.CurrentTheme).Returns(new ActiveTheme(
            new InterfaceTheme("Test", ThemeVariant.Dark, true),
            new EditorTheme("Test", TextMateSharp.Grammars.ThemeName.DarkPlus, true)));
        return theme;
    }

    private static Mock<IPreferencesService> Prefs(KeyboardSettings? keyboard = null)
    {
        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Keyboard).Returns(keyboard ?? new KeyboardSettings());
        return prefs;
    }

    private string WriteFile(string name, byte[] bytes)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private async Task<TextEditorWindow> OpenAsync(string path, Mock<IPreferencesService>? prefs = null)
    {
        var viewModel = await TextEditorWindowViewModel.LoadAsync(path, _windowService.Object);
        var window = new TextEditorWindow(viewModel, Theme().Object, (prefs ?? Prefs()).Object);
        window.Show();
        return window;
    }

    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    [AvaloniaFact]
    public async Task Opening_Should_ShowTheWholeFile_ItsEncodingAndLineBreaks_AndAClean_Title()
    {
        var path = WriteFile("settings.json", [.. Bom, .. "{\r\n  \"a\": 1\r\n}"u8.ToArray()]);

        var window = await OpenAsync(path);

        Assert.Equal("{\r\n  \"a\": 1\r\n}", window.Editor.Text);
        Assert.Equal("settings.json — Invoklet", window.Title);
        Assert.Equal("UTF-8 with BOM", window.ViewModel.EncodingLabel);
        Assert.Equal("CRLF", window.ViewModel.NewLineLabel);
        Assert.False(window.ViewModel.IsDirty);
    }

    [AvaloniaFact]
    public async Task Editing_Should_MarkTheWindowDirty_AndUndoingBack_Should_ClearIt()
    {
        var window = await OpenAsync(WriteFile("notes.txt", "hello"u8.ToArray()));

        window.Editor.Document.Insert(5, " world");
        Assert.True(window.ViewModel.IsDirty);
        Assert.Equal("notes.txt* — Invoklet", window.Title);

        window.Editor.Document.UndoStack.Undo();
        Assert.False(window.ViewModel.IsDirty);
        Assert.Equal("notes.txt — Invoklet", window.Title);
    }

    [AvaloniaFact]
    public async Task Save_Should_WriteTheText_KeepingTheBomAndLineBreaks()
    {
        var path = WriteFile("app.config", [.. Bom, .. "<a>\r\n</a>"u8.ToArray()]);
        var window = await OpenAsync(path);
        window.Editor.Document.Insert(3, "\r\n  <b/>");

        Assert.True(await window.ViewModel.SaveCommand.Execute());

        byte[] expected = [.. Bom, .. "<a>\r\n  <b/>\r\n</a>"u8.ToArray()];
        Assert.Equal(expected, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.False(window.ViewModel.IsDirty);
    }

    [AvaloniaFact]
    public async Task AFailedSave_Should_ShowAnError_AndKeepTheTextAndTheDirtyState()
    {
        var path = WriteFile("locked.txt", "original"u8.ToArray());
        var window = await OpenAsync(path);
        window.Editor.Document.Insert(0, "changed ");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            Assert.False(await window.ViewModel.SaveAsync());
        }
        finally
        {
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        _windowService.Verify(s => s.ShowErrorAsync("Save Failed", It.IsAny<string>()), Times.Once);
        Assert.Equal("changed original", window.Editor.Text);
        Assert.True(window.ViewModel.IsDirty);
        Assert.Equal("original", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [AvaloniaFact]
    public async Task SaveAs_Should_WriteTheNewFile_AndFollowIt_LeavingTheOriginal()
    {
        var path = WriteFile("a.yaml", "k: v\n"u8.ToArray());
        var newPath = Path.Combine(_folder, "b.yaml");
        _windowService.Setup(s => s.ShowSaveTextFileDialogAsync("a.yaml", _folder)).ReturnsAsync(newPath);
        var window = await OpenAsync(path);
        window.Editor.Document.Insert(5, "x: y\n");

        Assert.True(await window.ViewModel.SaveAsCommand.Execute());

        Assert.Equal("k: v\nx: y\n", await File.ReadAllTextAsync(newPath, TestContext.Current.CancellationToken));
        Assert.Equal("k: v\n", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal(newPath, window.ViewModel.FilePath);
        Assert.Equal("b.yaml — Invoklet", window.Title);
        Assert.False(window.ViewModel.IsDirty);
    }

    [AvaloniaTheory]
    [InlineData(UnsavedChangesResult.Cancel, false, "old")]
    [InlineData(UnsavedChangesResult.DontSave, true, "old")]
    [InlineData(UnsavedChangesResult.Save, true, "new old")]
    public async Task ClosingWithUnsavedChanges_Should_AskSaveDontSaveCancel(
        UnsavedChangesResult answer, bool closes, string fileAfterwards)
    {
        var path = WriteFile("x.txt", "old"u8.ToArray());
        _windowService.Setup(s => s.ShowUnsavedChangesDialogAsync("x.txt")).ReturnsAsync(answer);
        var window = await OpenAsync(path);
        window.Editor.Document.Insert(0, "new ");

        Assert.Equal(closes, await window.TryCloseAsync());

        Assert.Equal(!closes, window.IsVisible);
        Assert.Equal(fileAfterwards, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [AvaloniaFact]
    public async Task ClosingWithoutChanges_Should_NotAsk()
    {
        var window = await OpenAsync(WriteFile("x.txt", "same"u8.ToArray()));

        Assert.True(await window.TryCloseAsync());

        _windowService.Verify(s => s.ShowUnsavedChangesDialogAsync(It.IsAny<string>()), Times.Never);
    }

    [AvaloniaFact]
    public async Task AnswerSave_WhenTheSaveFails_Should_KeepTheWindowOpen()
    {
        var path = WriteFile("locked.txt", "old"u8.ToArray());
        _windowService.Setup(s => s.ShowUnsavedChangesDialogAsync("locked.txt")).ReturnsAsync(UnsavedChangesResult.Save);
        var window = await OpenAsync(path);
        window.Editor.Document.Insert(0, "new ");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            Assert.False(await window.TryCloseAsync());
        }
        finally
        {
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Assert.True(window.IsVisible);
        Assert.Equal("new old", window.Editor.Text);
    }

    // The platform's default Save key — Ctrl+S on Windows and Linux, Cmd+S on macOS — with the
    // focus in the editor, where the key is typed.
    [AvaloniaTheory]
    [InlineData(false, RawInputModifiers.Control)]
    [InlineData(true, RawInputModifiers.Meta)]
    public async Task TheDefaultSaveKey_Should_Save_FromTheFocusedEditor(bool mac, RawInputModifiers command)
    {
        var path = WriteFile("k.ini", "a=1"u8.ToArray());
        var window = await OpenAsync(path, Prefs(new KeyboardSettings { IsMac = mac }));
        window.Editor.Document.Insert(3, "\nb=2");
        Assert.True(window.Editor.TextArea.Focus());

        window.KeyPressQwerty(PhysicalKey.S, command);
        await TestWait.WhileAsync(() => window.ViewModel.IsDirty);

        Assert.Equal("a=1\nb=2", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.True(window.Editor.TextArea.IsFocused);
    }

    // The other platform's key is not Save: Ctrl+S on macOS leaves the file as it is.
    [AvaloniaFact]
    public async Task OnMacOS_CtrlS_Should_NotSave()
    {
        var path = WriteFile("k.ini", "a=1"u8.ToArray());
        var window = await OpenAsync(path, Prefs(new KeyboardSettings { IsMac = true }));
        window.Editor.Document.Insert(3, "\nb=2");
        Assert.True(window.Editor.TextArea.Focus());

        window.KeyPressQwerty(PhysicalKey.S, RawInputModifiers.Control);
        await Task.Delay(50);

        Assert.True(window.ViewModel.IsDirty);
        Assert.Equal("a=1", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    // Rebinding Save in Preferences while the window is open: the old key stops saving, the new one
    // saves, and the editor's own keys follow too.
    [AvaloniaFact]
    public async Task RebindingSave_WhileTheWindowIsOpen_Should_TakeEffectAtOnce()
    {
        var path = WriteFile("k.ini", "a=1"u8.ToArray());
        var keyboard = new KeyboardSettings { IsMac = false };
        var prefs = Prefs(keyboard);
        var window = await OpenAsync(path, prefs);
        window.Editor.Document.Insert(3, "\nb=2");
        Assert.True(window.Editor.TextArea.Focus());

        keyboard.SetKey(KeyCommands.Save, "Ctrl+Alt+S");
        keyboard.SetKey(KeyCommands.TriggerCompletion, "Ctrl+J");
        prefs.Raise(p => p.Changed += null);

        window.KeyPressQwerty(PhysicalKey.S, RawInputModifiers.Control);
        await Task.Delay(50);
        Assert.True(window.ViewModel.IsDirty);

        window.KeyPressQwerty(PhysicalKey.S, RawInputModifiers.Control | RawInputModifiers.Alt);
        await TestWait.WhileAsync(() => window.ViewModel.IsDirty);
        Assert.Equal("a=1\nb=2", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal(new KeyGesture(Key.J, KeyModifiers.Control), window.Editor.CompletionGesture);

        // closed: no longer follows Preferences
        Assert.True(await window.TryCloseAsync());
        keyboard.SetKey(KeyCommands.TriggerCompletion, "Ctrl+K");
        prefs.Raise(p => p.Changed += null);
        Assert.Equal(new KeyGesture(Key.J, KeyModifiers.Control), window.Editor.CompletionGesture);
    }

    [AvaloniaTheory]
    [InlineData("data.json", true)]
    [InlineData("readme.unknownext", false)] // plain text, no crash
    [InlineData("Makefile", false)]
    public void TrySetLanguage_Should_HighlightKnownExtensions_AndLeaveOthersPlain(string name, bool highlighted)
    {
        var editor = new Invoklet.Avalonia.CodeTextEditor.CodeTextEditor();
        new Window { Content = editor }.Show();

        Assert.Equal(highlighted, editor.TrySetLanguage(TextMateSharp.Grammars.ThemeName.DarkPlus, Path.GetExtension(name)));
    }

    // ── WindowService: one window per file ──

    private WindowService NewWindowService() => new(
        dependenciesWindowFactory: () => throw new InvalidOperationException("not expected here"),
        preferencesWindowFactory: () => throw new InvalidOperationException("not expected here"),
        themeService: Theme().Object,
        preferences: Prefs().Object);

    [AvaloniaFact]
    public async Task ShowTextEditorAsync_Should_OpenAFileOnce_AndBringItsWindowBack_TheSecondTime()
    {
        var service = NewWindowService();
        var path = WriteFile("once.txt", "x"u8.ToArray());

        await service.ShowTextEditorAsync(path);
        await service.ShowTextEditorAsync(Path.Combine(_folder, ".", "once.txt"));

        var window = Assert.Single(service.TextEditors);
        Assert.Equal(Path.GetFullPath(path), window.ViewModel.FilePath);

        window.Close();
        Assert.Empty(service.TextEditors);
    }

    [AvaloniaFact]
    public async Task ShowTextEditorAsync_OnAMissingFile_Should_OpenNoWindow()
    {
        var service = NewWindowService();

        await service.ShowTextEditorAsync(Path.Combine(_folder, "missing.txt"));

        Assert.Empty(service.TextEditors);
    }
}
