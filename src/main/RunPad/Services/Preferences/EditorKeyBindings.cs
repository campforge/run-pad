using Avalonia.Input;
using RunPad.Avalonia.CodeTextEditor;

namespace RunPad.Services.Preferences;

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
        editor.CommandModifier = keyboard.CommandModifier;
    }
}
