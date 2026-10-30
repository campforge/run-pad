using AvaloniaEdit;
using AvaloniaEdit.Search;
using Invoklet.Avalonia.CodeTextEditor;

namespace Invoklet.Services.Preferences;

public static class EditorKeyBindings
{
    /// <summary>Gives <paramref name="editor"/> the user's keys for its own commands (completion,
    /// F12…) and the platform's command modifier for Ctrl/Cmd+wheel zoom.</summary>
    public static void ApplyTo(this KeyboardSettings keyboard, CodeTextEditor editor)
    {
        editor.CompletionGesture = keyboard.GestureFor(KeyCommands.TriggerCompletion);
        editor.SignatureHelpGesture = keyboard.GestureFor(KeyCommands.SignatureHelp);
        editor.JumpToBracketGesture = keyboard.GestureFor(KeyCommands.JumpToBracket);
        editor.RenameSymbolGesture = keyboard.GestureFor(KeyCommands.RenameSymbol);
        editor.GoToDefinitionGesture = keyboard.GestureFor(KeyCommands.GoToDefinition);
        editor.FindReferencesGesture = keyboard.GestureFor(KeyCommands.FindReferences);
        editor.ToggleLineCommentGesture = keyboard.GestureFor(KeyCommands.ToggleLineComment);
        editor.CommandModifier = keyboard.CommandModifier;
        Configure(ApplicationCommands.Find, KeyCommands.FindText);
        Configure(ApplicationCommands.Replace, KeyCommands.ReplaceText);
        Configure(SearchCommands.FindNext, KeyCommands.FindNext);
        Configure(SearchCommands.FindPrevious, KeyCommands.FindPrevious);
        Configure(SearchCommands.ReplaceNext, KeyCommands.ReplaceNext);
        Configure(SearchCommands.ReplaceAll, KeyCommands.ReplaceAll);

        void Configure(RoutedCommand routed, KeyCommand command)
        {
            var key = keyboard.KeyFor(command);
            var hint = string.IsNullOrEmpty(key) ? command.Title : $"{command.Title} ({key})";
            editor.ConfigureSearchShortcut(routed, keyboard.GestureFor(command), hint);
        }
    }
}
