using Avalonia.Controls;
using Avalonia.Input;
using RunPad.Avalonia.CodeTextEditor;
using RunPad.Services;
using RunPad.Services.Preferences;
using RunPad.Windows.Models;

namespace RunPad.Windows;

/// <summary>
/// A plain text editor for configs and text documents —
/// one whole file per window, opened from File → "Open Text
/// File..." (<see cref="IWindowService.ShowTextEditorAsync"/>). TextMate highlighting when the
/// extension has a grammar, plain text otherwise. Closing with unsaved changes asks Save / Don't Save /
/// Cancel; a failed save keeps the window (and its text) open.
/// </summary>
public partial class TextEditorWindow : Window
{
    // set once the close was confirmed, so the real Close() below isn't asked about again
    private bool _closeConfirmed;

    private readonly IPreferencesService? _preferences;

    public TextEditorWindow(TextEditorWindowViewModel viewModel, IThemeService themeService, IPreferencesService? preferences)
    {
        InitializeComponent();
        DataContext = ViewModel = viewModel;

        var editor = new CodeTextEditor
        {
            Document = viewModel.Document,
            ShowLineNumberText = true,
            ShowBreakpoints = false,
            Background = themeService.GetBrush("RunPad.Editor.BackgroundBrush"),
        };
        EditorHost.Content = editor;
        Editor = editor;

        // SetLanguage needs the editor in the visual tree (same as every other CodeTextEditor host)
        EventHandler<global::Avalonia.VisualTreeAttachmentEventArgs>? onAttached = null;
        onAttached = (_, _) =>
        {
            editor.AttachedToVisualTree -= onAttached;
            editor.TrySetLanguage(themeService.CurrentTheme.Editor.TextMateName, Path.GetExtension(viewModel.FilePath));
        };
        editor.AttachedToVisualTree += onAttached;

        UpdateCaret();
        editor.TextArea.Caret.PositionChanged += (_, _) => UpdateCaret();

        // the user's own keys, as in the main window, and again whenever Preferences change them
        _preferences = preferences;
        ApplyKeyBindings();
        if (preferences is not null)
        {
            preferences.Changed += ApplyKeyBindings;
            Closed += (_, _) => preferences.Changed -= ApplyKeyBindings;
        }

        Closing += async (_, e) =>
        {
            if (_closeConfirmed) return;
            e.Cancel = true; // Closing can't await the question: cancel, ask, then close for real
            if (await ViewModel.ConfirmCloseAsync())
            {
                _closeConfirmed = true;
                Close();
            }
        };
    }

    // XAML previewer only.
    public TextEditorWindow()
    {
        InitializeComponent();
        ViewModel = null!;
        Editor = null!;
    }

    public TextEditorWindowViewModel ViewModel { get; }

    internal CodeTextEditor Editor { get; }

    /// <summary>Test hook: what a window close does (ask about unsaved changes, then close or not).</summary>
    internal async Task<bool> TryCloseAsync()
    {
        if (!await ViewModel.ConfirmCloseAsync()) return false;
        _closeConfirmed = true;
        Close();
        return true;
    }

    private void UpdateCaret() =>
        CaretText.Text = $"Ln {Editor.TextArea.Caret.Line}, Col {Editor.TextArea.Caret.Column}";

    private readonly List<KeyBinding> _saveBindings = [];

    private void ApplyKeyBindings()
    {
        if (_preferences?.Keyboard is not { } keyboard) return;
        keyboard.ApplyTo(Editor);

        foreach (var binding in _saveBindings) KeyBindings.Remove(binding);
        _saveBindings.Clear();
        AddGesture(keyboard.GestureFor(KeyCommands.Save), ViewModel.SaveCommand);
        AddGesture(keyboard.GestureFor(KeyCommands.SaveAs), ViewModel.SaveAsCommand);
    }

    // An unbound or unparsable key: the toolbar buttons still work.
    private void AddGesture(KeyGesture? gesture, System.Windows.Input.ICommand command)
    {
        if (gesture is null) return;
        var binding = new KeyBinding { Gesture = gesture, Command = command };
        _saveBindings.Add(binding);
        KeyBindings.Add(binding);
    }
}
