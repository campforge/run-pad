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
    public static readonly KeyCommand Save = new("file.save", General, "Save", "Ctrl+S", "Cmd+S");
    public static readonly KeyCommand SaveAs = new("file.saveAs", General, "Save As", "Ctrl+Shift+S", "Cmd+Shift+S");
    public static readonly KeyCommand CloseTab = new("file.closeTab", General, "Close Tab", "Ctrl+W", "Cmd+W");
    public static readonly KeyCommand OpenDependencies = new("tools.dependencies", General, "Dependencies", "F4");
    public static readonly KeyCommand OpenAssemblyExplorer = new("tools.assemblyExplorer", General, "Assembly Explorer", "");
    public static readonly KeyCommand OpenPreferences = new("tools.preferences", General, "Preferences", "", "Cmd+,");
    public static readonly KeyCommand ToggleFileTree = new("view.toggleFileTree", General, "Toggle File Tree", "");
    public static readonly KeyCommand ToggleOutputPanel = new("view.toggleOutputPanel", General, "Toggle Output Panel", "Ctrl+R", "Cmd+R");
    public static readonly KeyCommand ZoomIn = new("view.zoomIn", General, "Zoom In", "");
    public static readonly KeyCommand ZoomOut = new("view.zoomOut", General, "Zoom Out", "");
    public static readonly KeyCommand ResetZoom = new("view.resetZoom", General, "Reset Zoom", "");

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
        NewFile, Save, SaveAs, CloseTab, OpenDependencies, OpenAssemblyExplorer, OpenPreferences,
        ToggleFileTree, ToggleOutputPanel, ZoomIn, ZoomOut, ResetZoom,
        RunOrContinue, Stop, ToggleDebugMode, StepOver, StepInto, StepOut, Pause, RunToCursor,
        TriggerCompletion, SignatureHelp, GoToDefinition, FindReferences, RenameSymbol, JumpToBracket,
        Format, GoToLine, ResolveAllUsings,
        RenameFile, DeleteFile,
        ExplorerBack, ExplorerForward, ExplorerSearch, ExplorerAnalyze, ExplorerRemoveAssembly, ExplorerNextTab,
        ExplorerPreviousTab,
    ];

    public static KeyCommand? Find(string id) => All.FirstOrDefault(c => c.Id == id);
}
