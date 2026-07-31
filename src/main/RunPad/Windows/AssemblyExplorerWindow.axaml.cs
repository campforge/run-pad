using Avalonia;
using Avalonia.Controls;
using RunPad.Avalonia.CodeTextEditor;
using RunPad.Languages.DotnetCommon;
using RunPad.Services;
using SyntaxTree = ICSharpCode.Decompiler.CSharp.Syntax.SyntaxTree;

namespace RunPad.Windows;

/// <summary>
/// ILSpy/dnSpy-style assembly explorer (TODO.md #62, user request 2026-07-19; merged with the
/// previously separate <c>DecompiledViewWindow</c> per user request 2026-07-20 — dnSpy/ILSpy don't
/// pop a new window per decompiled type either, they update one code pane in place) — a tree of
/// namespaces/types on the left, a read-only decompiled source pane on the right that updates in
/// place on double-click. Serves two entry points:
///  - Go to Definition (F12) from the user's own script, for a symbol outside it — opened with the
///    resolved member already decompiled AND its assembly loaded into the tree, so the user can keep
///    browsing right from there (<see cref="UserControls.EditorsTabItemControl.ShowGoToDefinitionAsync"/>).
///  - "Edit → Assembly Explorer..." — an arbitrary DLL the user picks via <see cref="BrowseAsync"/>,
///    nothing decompiled until a type is picked from the tree.
/// F12 inside the source pane keeps working recursively either way (<see cref="NavigateAsync"/>,
/// pass 96) — it resolves against the currently-shown <see cref="SyntaxTree"/>, not
/// against whatever's loaded in the tree, so it also works for entry points where nothing was
/// browsed via the tree at all.
/// </summary>
public partial class AssemblyExplorerWindow : Window
{
    private readonly IWindowService _windowService;
    private readonly string _startFolder = "";
    private string? _currentAssemblyPath;
    private SyntaxTree? _currentTree;
    private readonly DecompiledMemberResult? _pendingInitialResult;

    /// <summary>Test-only observability — same rationale as the old DecompiledViewWindow.Editor.</summary>
    internal CodeTextEditor Editor => (CodeTextEditor)EditorHost.Content!;

    internal TreeView Tree => TypeTree;
    internal TextBlock AssemblyPathText => AssemblyPathBlock;

    /// <summary>"Edit → Assembly Explorer...", or Browse from within an already-open instance — empty
    /// until the user picks a DLL.</summary>
    public AssemblyExplorerWindow(
        IThemeService themeService,
        IWindowService windowService,
        string startFolder)
        : this(themeService, windowService)
    {
        _startFolder = startFolder;
    }

    /// <summary>Go to Definition (F12) landing outside the user's own script — opens straight to
    /// <paramref name="initialResult"/> decompiled, with <paramref name="assemblyPath"/>'s tree loaded
    /// alongside it (null only for the "type not found" fallback result, which has no real assembly
    /// path behind it worth browsing).</summary>
    public AssemblyExplorerWindow(
        IThemeService themeService,
        IWindowService windowService,
        DecompiledMemberResult initialResult,
        string? assemblyPath)
        : this(themeService, windowService)
    {
        _pendingInitialResult = initialResult;
        if (assemblyPath is null)
            return;
        _startFolder = Path.GetDirectoryName(assemblyPath) ?? "";
        _ = LoadAssemblyAsync(assemblyPath);
    }

    private AssemblyExplorerWindow(
        IThemeService themeService,
        IWindowService windowService)
    {
        InitializeComponent();
        _windowService = windowService;

        BrowseButton.Click += async (_, _) => await BrowseAsync();

        var editor = new CodeTextEditor
        {
            IsReadOnly = true,
            ShowLineNumberText = true,
            ShowBreakpoints = false,
            Background = themeService.GetBrush("RunPad.Editor.BackgroundBrush")
        };

        editor.GoToDefinitionRequested += (_, _) => _ = NavigateAsync();
        EditorHost.Content = editor;

        // Same reasoning as EditorTabItemControl's own onAttached dance: SetLanguage calls Redraw()
        // internally, which needs the editor already in the visual tree.
        EventHandler<VisualTreeAttachmentEventArgs>? onAttached = null;
        onAttached = (_, _) =>
        {
            editor.AttachedToVisualTree -= onAttached;
            editor.SetLanguage(themeService.CurrentTheme.Editor.TextMateName, ".cs");
            if (_pendingInitialResult is { } initial) ApplyResult(initial);
        };
        editor.AttachedToVisualTree += onAttached;
    }

    // XAML previewer only — mirrors the old DecompiledViewWindow's own empty ctor.
    public AssemblyExplorerWindow()
    {
        InitializeComponent();
        _windowService = null!;
    }

    internal async Task BrowseAsync()
    {
        var path = await _windowService.ShowOpenAssemblyFileDialogAsync(_startFolder);
        if (path is null) return;
        await LoadAssemblyAsync(path);
    }

    internal async Task LoadAssemblyAsync(string assemblyPath)
    {
        IReadOnlyList<AssemblyTypeEntry> types;
        try
        {
            types = await Task.Run(() => CSharpDecompilerService.EnumerateTypes(assemblyPath));
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException)
        {
            await _windowService.ShowErrorAsync(
                "Browse Assembly Failed", $"Could not load '{Path.GetFileName(assemblyPath)}':\n{ex.Message}");
            return;
        }

        _currentAssemblyPath = assemblyPath;
        AssemblyPathBlock.Text = assemblyPath;
        BuildTree(types);
    }

    private void BuildTree(IReadOnlyList<AssemblyTypeEntry> types)
    {
        TypeTree.Items.Clear();
        foreach (var group in types.GroupBy(t => t.Namespace).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var nsItem = new TreeViewItem
            {
                Header = string.IsNullOrEmpty(group.Key) ? "(global)" : group.Key,
            };
            foreach (var entry in group.OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                var typeItem = new TreeViewItem { Header = entry.Name, Tag = entry };
                typeItem.DoubleTapped += (_, _) => _ = OpenTypeAsync(entry);
                nsItem.Items.Add(typeItem);
            }

            TypeTree.Items.Add(nsItem);
        }
    }

    // Double-click in the tree — decompiles in place, same pane recursive F12 uses (no new window;
    // that's the whole point of the merge — dnSpy/ILSpy don't pop one per type either).
    internal async Task OpenTypeAsync(AssemblyTypeEntry entry)
    {
        if (_currentAssemblyPath is not { } assemblyPath) return;

        DecompiledMemberResult result;
        try
        {
            result = await Task.Run(() => CSharpDecompilerService.DecompileType(
                assemblyPath, entry.FullTypeName, entry.Name));
        }
        catch (Exception ex)
        {
            result = new DecompiledMemberResult($"// Failed to decompile: {ex.Message}", 1, entry.Name);
        }

        ApplyResult(result);
    }

    // internal (not private): lets tests drive a navigation directly without simulating a real F12
    // keypress + caret placement — same rationale as every other "internal test hook" in this session
    // (headless Avalonia has no reliable way to fake caret-dependent keyboard interaction either).
    internal async Task NavigateAsync()
    {
        if (_currentTree is not { } tree) return;

        var caretLine = Editor.TextArea.Caret.Line;
        var caretColumn = Editor.TextArea.Caret.Column;

        var reference = await Task.Run(() =>
            CSharpDecompilerService.ResolveReferenceAtPosition(tree, caretLine, caretColumn));
        if (reference is null) return;

        DecompiledMemberResult next;
        try
        {
            next = await Task.Run(() => CSharpDecompilerService.Decompile(reference));
        }
        catch (Exception ex)
        {
            next = new DecompiledMemberResult($"// Failed to decompile: {ex.Message}", 1, reference.DisplayName);
        }

        // Back on the UI thread already — Avalonia's own SynchronizationContext resumes the
        // continuation here, same as EditorTabItemControl.ShowGoToDefinitionAsync's identical
        // await Task.Run(...) pattern (see pass 96 for why an explicit
        // Dispatcher.Post here was a real, caught-by-test bug, not just unnecessary).
        ApplyResult(next);
    }

    private void ApplyResult(DecompiledMemberResult result)
    {
        Title = result.Title;
        _currentTree = result.SyntaxTree;

        Editor.Text = result.Text;
        Editor.TextArea.Caret.Line = result.Line;
        Editor.TextArea.Caret.Column = 1;
        Editor.ScrollToLine(result.Line);
    }
}
