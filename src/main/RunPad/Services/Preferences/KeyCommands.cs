namespace RunPad.Services.Preferences;

/// <summary>
/// A command that can be bound to a key. <see cref="DefaultKey"/> applies on Windows and Linux,
/// <see cref="MacKey"/> on macOS (null there means the same as <see cref="DefaultKey"/>); an empty
/// string means "no key by default". The user's own bindings, if any, are in
/// <see cref="KeyboardSettings.Bindings"/>.
/// </summary>
public sealed record KeyCommand(string Id, string Category, string Title, string DefaultKey, string? MacKey = null)
{
    public string DefaultKeyFor(bool mac) => mac ? MacKey ?? DefaultKey : DefaultKey;
}

/// <summary>
/// Every key binding in RunPad, in the order the Keyboard page of Preferences lists them. The
/// defaults follow LINQPad (and Visual Studio, whose debugging keys LINQPad shares); on macOS, Ctrl
/// becomes Cmd where that is the system convention. Keys a text box handles itself (Undo, Copy…)
/// follow the platform and are not listed.
/// </summary>
public static class KeyCommands
{
    public const string General = "General";
    public const string Run = "Run";
    public const string Editor = "Editor";
    public const string FileTree = "File tree";
    public const string AssemblyExplorer = "Assembly Explorer";

    public static readonly KeyCommand NewFile = new("file.new", General, "New File", "Ctrl+N", "Cmd+N");
    public static readonly KeyCommand OpenFile = new("file.open", General, "Open Script", "Ctrl+O", "Cmd+O");
    public static readonly KeyCommand Save = new("file.save", General, "Save", "Ctrl+S", "Cmd+S");
    public static readonly KeyCommand SaveAs = new("file.saveAs", General, "Save As", "Ctrl+Shift+S", "Cmd+Shift+S");
    public static readonly KeyCommand RevertFile = new("file.revert", General, "Revert to Saved", "");
    public static readonly KeyCommand CloseTab = new("file.closeTab", General, "Close Tab", "Ctrl+W", "Cmd+W");
    // Windows and Linux close the window with the system's Alt+F4; macOS has no such key, Cmd+Q is the convention.
    public static readonly KeyCommand Exit = new("file.exit", General, "Exit", "", "Cmd+Q");
    public static readonly KeyCommand OpenDependencies = new("tools.dependencies", General, "Dependencies", "F4");
    public static readonly KeyCommand OpenAssemblyExplorer = new("tools.assemblyExplorer", General, "Assembly Explorer", "");
    public static readonly KeyCommand OpenPreferences = new("tools.preferences", General, "Preferences", "", "Cmd+,");
    public static readonly KeyCommand ToggleFileTree = new("view.toggleFileTree", General, "Toggle File Tree", "");
    public static readonly KeyCommand ToggleOutputPanel = new("view.toggleOutputPanel", General, "Toggle Output Panel", "Ctrl+R", "Cmd+R");
    public static readonly KeyCommand ZoomIn = new("view.zoomIn", General, "Zoom In", "");
    public static readonly KeyCommand ZoomOut = new("view.zoomOut", General, "Zoom Out", "");
    public static readonly KeyCommand ResetZoom = new("view.resetZoom", General, "Reset Zoom", "");
    // VS Code's keys; its Ctrl+K V for the side preview is a two-key chord, which key bindings here
    // can't express, so that one takes Alt instead of Shift.
    public static readonly KeyCommand TogglePreview = new("view.togglePreview", General, "Toggle Markdown Preview", "Ctrl+Shift+V", "Cmd+Shift+V");
    public static readonly KeyCommand TogglePreviewToSide = new("view.togglePreviewToSide", General, "Toggle Markdown Preview to the Side", "Ctrl+Alt+V", "Cmd+Alt+V");

    public static readonly KeyCommand RunOrContinue = new("run.run", Run, "Run / Continue", "F5");
    public static readonly KeyCommand Stop = new("run.stop", Run, "Stop", "Shift+F5");
    public static readonly KeyCommand ToggleDebugMode = new("run.toggleDebugMode", Run, "Debug Mode", "");
    public static readonly KeyCommand StepOver = new("run.stepOver", Run, "Step Over", "F10");
    public static readonly KeyCommand StepInto = new("run.stepInto", Run, "Step Into", "F11");
    public static readonly KeyCommand StepOut = new("run.stepOut", Run, "Step Out", "Shift+F11");
    public static readonly KeyCommand Pause = new("run.pause", Run, "Pause", "");
    public static readonly KeyCommand RunToCursor = new("run.runToCursor", Run, "Run to Cursor", "Ctrl+F10", "Cmd+F10");

    public static readonly KeyCommand TriggerCompletion = new("editor.completion", Editor, "Show Completion", "Ctrl+Space");
    public static readonly KeyCommand SignatureHelp = new("editor.signatureHelp", Editor, "Signature Help", "Ctrl+Shift+Space", "Cmd+Shift+Space");
    public static readonly KeyCommand GoToDefinition = new("editor.goToDefinition", Editor, "Go to Definition", "F12");
    public static readonly KeyCommand FindReferences = new("editor.findReferences", Editor, "Find References", "Shift+F12");
    public static readonly KeyCommand RenameSymbol = new("editor.rename", Editor, "Rename Symbol", "F2");
    public static readonly KeyCommand JumpToBracket = new("editor.jumpToBracket", Editor, "Jump to Matching Bracket", "Ctrl+]");
    public static readonly KeyCommand Format = new("editor.format", Editor, "Format Document", "Shift+Alt+F");
    public static readonly KeyCommand GoToLine = new("editor.goToLine", Editor, "Go to Line", "Ctrl+G");
    public static readonly KeyCommand ResolveAllUsings = new("editor.resolveAllUsings", Editor, "Resolve All Usings", "Ctrl+Alt+U", "Cmd+Alt+U");

    public static readonly KeyCommand RenameFile = new("fileTree.rename", FileTree, "Rename", "F2", "Enter");
    public static readonly KeyCommand DeleteFile = new("fileTree.delete", FileTree, "Delete", "Delete", "Cmd+Backspace");

    public static readonly KeyCommand ExplorerBack = new("explorer.back", AssemblyExplorer, "Back", "Alt+Left", "Cmd+[");
    public static readonly KeyCommand ExplorerForward = new("explorer.forward", AssemblyExplorer, "Forward", "Alt+Right", "Cmd+]");
    public static readonly KeyCommand ExplorerSearch = new("explorer.search", AssemblyExplorer, "Search Types and Members", "Ctrl+Shift+F", "Cmd+Shift+F");
    public static readonly KeyCommand ExplorerAnalyze = new("explorer.analyze", AssemblyExplorer, "Analyze", "Ctrl+R", "Cmd+R");
    public static readonly KeyCommand ExplorerRemoveAssembly = new("explorer.removeAssembly", AssemblyExplorer, "Remove Assembly", "Delete", "Cmd+Backspace");
    public static readonly KeyCommand ExplorerNextTab = new("explorer.nextTab", AssemblyExplorer, "Next Tab", "Ctrl+Tab");
    public static readonly KeyCommand ExplorerPreviousTab = new("explorer.previousTab", AssemblyExplorer, "Previous Tab", "Ctrl+Shift+Tab");

    public static IReadOnlyList<KeyCommand> All { get; } =
    [
        NewFile, OpenFile, Save, SaveAs, RevertFile, CloseTab, Exit, OpenDependencies, OpenAssemblyExplorer, OpenPreferences,
        ToggleFileTree, ToggleOutputPanel, TogglePreview, TogglePreviewToSide, ZoomIn, ZoomOut, ResetZoom,
        RunOrContinue, Stop, ToggleDebugMode, StepOver, StepInto, StepOut, Pause, RunToCursor,
        TriggerCompletion, SignatureHelp, GoToDefinition, FindReferences, RenameSymbol, JumpToBracket,
        Format, GoToLine, ResolveAllUsings,
        RenameFile, DeleteFile,
        ExplorerBack, ExplorerForward, ExplorerSearch, ExplorerAnalyze, ExplorerRemoveAssembly, ExplorerNextTab,
        ExplorerPreviousTab,
    ];

    public static KeyCommand? Find(string id) => All.FirstOrDefault(c => c.Id == id);

    // ── Where each key is live ────────────────────────────────────────────────

    public const string MainWindow = "Main window";
    public const string TextEditorWindow = "Text editor window";
    public const string AssemblyExplorerWindow = "Assembly Explorer window";

    private const string CodeEditor = "code editor";
    private const string Tree = "tree";

    // The keys the code editor control handles itself (EditorKeyBindings.ApplyTo), in every window
    // that has one.
    private static readonly HashSet<KeyCommand> CodeEditorCommands =
        [TriggerCompletion, SignatureHelp, JumpToBracket, RenameSymbol, GoToDefinition, FindReferences];

    /// <summary>Where <paramref name="command"/>'s key is handled: the main window dispatches General
    /// and Run keys (and Format, Go to Line, Resolve All Usings) wherever its focus is; the code
    /// editor and the file tree only see keys while they have focus; the text editor and Assembly
    /// Explorer windows share a few keys with the main window.</summary>
    public static IReadOnlyList<KeyScope> ScopesOf(KeyCommand command)
    {
        List<KeyScope> scopes = [];
        if (CodeEditorCommands.Contains(command))
        {
            scopes.Add(new(MainWindow, CodeEditor));
            scopes.Add(new(TextEditorWindow, CodeEditor));
            scopes.Add(new(AssemblyExplorerWindow, CodeEditor));
        }
        else if (command.Category == FileTree)
        {
            scopes.Add(new(MainWindow, Tree));
        }
        else if (command == ExplorerRemoveAssembly)
        {
            scopes.Add(new(AssemblyExplorerWindow, Tree));
        }
        else if (command.Category == AssemblyExplorer)
        {
            scopes.Add(new(AssemblyExplorerWindow));
        }
        else
        {
            scopes.Add(new(MainWindow));
        }

        if (command == Save || command == SaveAs) scopes.Add(new(TextEditorWindow));
        if (command == Save || command == CloseTab) scopes.Add(new(AssemblyExplorerWindow));
        // Exit quits from every window (ApplicationExit).
        if (command == Exit)
        {
            scopes.Add(new(TextEditorWindow));
            scopes.Add(new(AssemblyExplorerWindow));
        }
        return scopes;
    }
}

/// <summary>A place where a key is handled: a whole window (<paramref name="Area"/> null), or one
/// part of it that only sees the key while it has focus.</summary>
public readonly record struct KeyScope(string Window, string? Area = null)
{
    /// <summary>Whether one keypress can reach both scopes: the same window, and either one of them
    /// covers the whole window or both are the same part.</summary>
    public bool Overlaps(KeyScope other)
        => Window == other.Window && (Area is null || other.Area is null || Area == other.Area);
}
