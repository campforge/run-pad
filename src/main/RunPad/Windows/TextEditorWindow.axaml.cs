using Avalonia.Controls;
using Avalonia.Input;
using RunPad.Avalonia.CodeTextEditor;
using RunPad.Avalonia.Themes;
using RunPad.Services;
using RunPad.Services.Preferences;
using RunPad.Services.Scaling;
using RunPad.UserControls;
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

        // The window is always shown while open: a theme change recolours it at once.
        void OnThemeChanged(ActiveTheme theme)
        {
            editor.SetTheme(theme.Editor.TextMateName);
            editor.Background = themeService.GetBrush("RunPad.Editor.BackgroundBrush");
        }
        themeService.ThemeChanged += OnThemeChanged;
        Closed += (_, _) => themeService.ThemeChanged -= OnThemeChanged;

        UpdateCaret();
        editor.TextArea.Caret.PositionChanged += (_, _) => UpdateCaret();

        // the user's own keys, as in the main window, and again whenever Preferences change them
        _preferences = preferences;
        ApplyKeyBindings();
        if (preferences is not null)
        {
            preferences.Changed += ApplyKeyBindings;
            Closed += (_, _) => preferences.Changed -= ApplyKeyBindings;

            // Code here looks like code in the main window: the same font, size and shared code zoom.
            var viewScale = ViewScale.For(preferences);
            void ApplyCodeFont()
            {
                if (preferences.Editor is not { } settings) return;
                editor.FontFamily = MonospaceFont.Resolve(settings.FontFamily);
                editor.FontSize = EditorsTabItemControl.CodeFontSize(settings, viewScale.CodeZoom);
            }
            void OnZoomRequested(int direction)
            {
                if (direction > 0) viewScale.ZoomIn();
                else viewScale.ZoomOut();
            }
            ApplyCodeFont();
            preferences.Changed += ApplyCodeFont;
            viewScale.Changed += ApplyCodeFont;
            editor.ZoomRequested += OnZoomRequested;
            Closed += (_, _) =>
            {
                preferences.Changed -= ApplyCodeFont;
                viewScale.Changed -= ApplyCodeFont;
                editor.ZoomRequested -= OnZoomRequested;
            };
        }

        Closing += async (_, e) =>
        {
            if (_closeConfirmed) return;
            // The main window owns this one and, when it closes (Exit, Cmd+Q, quitting the app), asks
            // about this window's unsaved changes itself, together with its own tabs, so that one
            // answer per document is enough and the app does not stop halfway.
            if (Owner is not null && e.CloseReason is WindowCloseReason.OwnerWindowClosing
                    or WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown)
                return;
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
