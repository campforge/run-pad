using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using ICSharpCode.Decompiler.CSharp;
using RunPad.Avalonia.CodeTextEditor;
using RunPad.Contracts.IntelliSense;
using RunPad.Languages.DotnetCommon;
using RunPad.Services;
using RunPad.Windows.Models;
using SyntaxTree = ICSharpCode.Decompiler.CSharp.Syntax.SyntaxTree;

namespace RunPad.Windows;

/// <summary>
/// ILSpy/dnSpy-style assembly explorer (merged with the
/// previously separate <c>DecompiledViewWindow</c> — dnSpy/ILSpy don't
/// pop a new window per decompiled type either, they update one code pane in place) — a tree of
/// namespaces/types on the left, a read-only decompiled source pane on the right that updates in
/// place on selection. The tree follows ILSpy: types expand lazily into
/// Base Types / Derived Types / nested types / members, every node has a kind icon, non-public API is
/// greyed, and selecting a member decompiles just that member. Like ILSpy's assembly list, the tree
/// holds any number of assemblies: Browse adds one, a References row opens
/// the assembly it names, and F12 into another assembly adds that assembly too. Serves two entry points:
///  - Go to Definition (F12) from the user's own script, for a symbol outside it — opened with the
///    resolved member already decompiled AND its assembly loaded into the tree, so the user can keep
///    browsing right from there (<see cref="UserControls.EditorsTabItemControl.ShowGoToDefinitionAsync"/>).
///  - "Edit → Assembly Explorer..." — an arbitrary DLL the user picks via <see cref="BrowseAsync"/>,
///    nothing decompiled until a type is picked from the tree.
/// F12 inside the source pane keeps working recursively either way (<see cref="NavigateAsync"/>) — it resolves
/// against the currently-shown <see cref="SyntaxTree"/>, not
/// against whatever's loaded in the tree, so it also works for entry points where nothing was
/// browsed via the tree at all.
/// </summary>
public partial class AssemblyExplorerWindow : Window
{
    private readonly IWindowService _windowService;
    private readonly string _startFolder = "";
    private SyntaxTree? _currentTree;
    private readonly DecompiledMemberResult? _pendingInitialResult;
    private readonly Dictionary<TreeViewItem, Task> _expansions = [];

    // Bumped by every selection/navigation — a decompile that finishes after a newer one started
    // (fast clicking through the tree) is dropped instead of overwriting the newer result.
    private int _requestVersion;

    // The tree's assemblies, in the order they were added (ILSpy's assembly list).
    private readonly List<LoadedAssembly> _assemblies = [];
    private int _assemblySetVersion;
    private bool _suppressSelection;
    private DecompilerView _view = DecompilerView.Default;
    private bool _syncingViewBoxes;

    // Search (ILSpy's search pane): an index per loaded assembly, built on first use.
    internal static TimeSpan SearchDelay { get; set; } = TimeSpan.FromMilliseconds(250);
    internal const int SearchLimit = 200;
    private readonly Dictionary<string, Task<IReadOnlyList<AssemblySearchEntry>>> _searchIndexes =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private CancellationTokenSource? _searchCts;
    private string _lastQuery = "";
    private CancellationTokenSource? _analysisCts;
    private (AssemblyTreeNode Node, AnalysisKind Kind)? _lastAnalysis;
    private string? _lastSaveFolder;

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
    /// path behind it worth browsing). With <paramref name="definition"/> (what F12 resolved to), the
    /// tree also reveals and selects that member's row — or its type's, when the tree doesn't list the
    /// member — and the page becomes re-renderable (C#/IL).</summary>
    public AssemblyExplorerWindow(
        IThemeService themeService,
        IWindowService windowService,
        DecompiledMemberResult initialResult,
        string? assemblyPath,
        IDefinitionResult? definition = null)
        : this(themeService, windowService)
    {
        _pendingInitialResult = initialResult;
        if (assemblyPath is null)
            return;
        _startFolder = Path.GetDirectoryName(assemblyPath) ?? "";
        _ = LoadAndRevealDefinitionAsync(assemblyPath, definition, initialResult);
    }

    /// <summary>
    /// A later F12 from the user's script, into this already open explorer (there's one per app, see
    /// <c>WindowService</c>): the page opens in a new tab — or in the only tab if it's still empty —
    /// and its target is revealed in the tree like the first one's.
    /// </summary>
    internal async Task OpenDefinitionInNewTabAsync(
        DecompiledMemberResult page, string? assemblyPath, IDefinitionResult? definition)
    {
        if (_tabs.Count > 1 || _activeTab.Current is not null)
        {
            var tab = new DocumentTab();
            _tabs.Insert(ActiveTabIndex + 1, tab);
            ActivateTab(tab);
        }
        Show(page, item: null, target: null, DecompilerView.Default); // F12's page is always default C#
        if (assemblyPath is not null)
            await LoadAndRevealDefinitionAsync(assemblyPath, definition, page);
    }

    // The initial page's row and target, once known. They may be found before or after the initial
    // page is shown (the editor shows it when it's attached), so both orders are handled.
    private TreeViewItem? _initialPageRow;
    private PageTarget? _initialPageTarget;

    // Loads the definition's assembly, reveals its member's row (its type's, when the tree doesn't list
    // the member) and attaches row + target to `page` in whichever tab shows it — selecting the row if
    // that tab is the active one, and re-rendering the page if the view isn't the default C# it was
    // decompiled in (other tabs re-render when activated). A page the user has navigated away from
    // keeps its tab's selection.
    private async Task LoadAndRevealDefinitionAsync(string assemblyPath, IDefinitionResult? definition, DecompiledMemberResult page)
    {
        if (await LoadAssemblyAsync(assemblyPath) is null || definition?.ContainingTypeFullName is not { } typeName) return;

        DefinitionTokens? tokens;
        try
        {
            tokens = await Task.Run(() => CSharpDecompilerService.ResolveDefinitionTokens(definition));
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException)
        {
            return; // the code is already shown; only the tree selection is missing
        }
        if (tokens is null) return;

        var (typeItem, memberItem) = await RevealAsync(assemblyPath, typeName, tokens.MemberToken);
        var row = memberItem ?? typeItem;
        var target = new PageTarget(assemblyPath, tokens.TypeToken, definition.DisplayName, tokens.MemberToken ?? 0);
        if (ReferenceEquals(page, _pendingInitialResult))
        {
            // the constructor's page may not be on screen yet: the editor shows it once attached
            _initialPageRow = row;
            _initialPageTarget = target;
        }

        foreach (var tab in _tabs)
        {
            if (tab.Current is not { } current || !ReferenceEquals(current.Result, page)) continue;
            tab.Current = current with { Item = row, Target = target };
            if (tab == _activeTab) SelectSilently(row);
        }
        if (_activeTab.Current is { } shown && ReferenceEquals(shown.Result, page))
            await RefreshCurrentAsync(); // does nothing while a newer selection renders
    }

    private AssemblyExplorerWindow(
        IThemeService themeService,
        IWindowService windowService)
    {
        InitializeComponent();
        _windowService = windowService;
        _activeTab = _tabs[0];

        BrowseButton.Click += async (_, _) => await BrowseAsync();

        // the tab strip: picking a tab shows it, its × closes it (a Button inside the item template)
        DocumentTabs.SelectionChanged += (_, _) =>
        {
            if (!_syncingTabStrip && DocumentTabs.SelectedItem is TabHeader { Tab: DocumentTab tab } && tab != _activeTab)
                ActivateTab(tab);
        };
        DocumentTabs.AddHandler(Button.ClickEvent, (_, e) =>
        {
            if (e.Source is Button { DataContext: TabHeader { Tab: DocumentTab tab } })
            {
                CloseTab(tab);
                e.Handled = true;
            }
        });
        // middle-click closes a tab, like browsers and IDEs (Tunnel: before the ListBox selects it)
        DocumentTabs.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(DocumentTabs).Properties.IsMiddleButtonPressed) return;
            if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)
                is not { DataContext: TabHeader { Tab: DocumentTab tab } }) return;
            e.Handled = true;
            CloseTab(tab);
        }, RoutingStrategies.Tunnel);

        // middle-click or Ctrl+click on a tree row opens it in a new tab
        TypeTree.AddHandler(PointerPressedEvent, (_, e) =>
        {
            var point = e.GetCurrentPoint(TypeTree);
            var newTab = point.Properties.IsMiddleButtonPressed
                         || (point.Properties.IsLeftButtonPressed && e.KeyModifiers == KeyModifiers.Control);
            if (!newTab || (e.Source as Visual)?.FindAncestorOfType<TreeViewItem>(includeSelf: true)
                    is not { Header: AssemblyTreeNode node } row) return;
            e.Handled = true;
            _ = OpenInNewTabAsync(node, row);
        }, RoutingStrategies.Tunnel);
        SaveCodeButton.Click += async (_, _) => await SaveCodeAsync();

        // Drop .dll/.exe files from the file manager anywhere on the window to add them to the tree.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        });
        AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            e.Handled = true;
            var paths = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
            if (paths is { Count: > 0 }) _ = OpenDroppedFilesAsync(paths);
        });
        BackButton.Click += (_, _) => GoBack();
        ForwardButton.Click += (_, _) => GoForward();

        LanguageBox.ItemsSource = new[] { "C#", "IL" };
        LanguageBox.SelectedIndex = 0;
        VersionBox.ItemsSource = LanguageVersions.Select(v => v.Label).ToList();
        VersionBox.SelectedIndex = 0;
        LanguageBox.SelectionChanged += (_, _) => OnViewBoxChanged();
        VersionBox.SelectionChanged += (_, _) => OnViewBoxChanged();
        TypeTree.SelectionChanged += (_, _) =>
        {
            // the context menu follows the selected row however it gets opened (right-click, Menu key,
            // ContextMenu.Open — the last doesn't raise Opening)
            BuildTreeContextMenu();
            if (!_suppressSelection && TypeTree.SelectedItem is TreeViewItem { Header: AssemblyTreeNode node } item)
                _ = SelectNodeAsync(node, item);
        };
        // A References row opens its assembly on double-click or Enter; Delete removes an assembly row.
        TypeTree.DoubleTapped += (_, _) =>
        {
            if (TypeTree.SelectedItem is TreeViewItem { Header: AssemblyTreeNode { Reference: { } reference } })
                _ = OpenReferenceAsync(reference);
        };
        TypeTree.KeyDown += (_, e) =>
        {
            if (TypeTree.SelectedItem is not TreeViewItem { Header: AssemblyTreeNode node } item) return;
            if (e.Key == Key.Enter && node.Reference is { } reference) _ = OpenReferenceAsync(reference);
            else if (e.Key == Key.Delete && node.Kind == AssemblyNodeKind.Assembly) RemoveAssembly(item);
            else return;
            e.Handled = true;
        };
        TreeContextMenu.Opening += (_, _) => BuildTreeContextMenu();
        // toggling "Include virtual calls" re-runs the shown analysis
        IncludeVirtualCallsBox.IsCheckedChanged += (_, _) =>
        {
            if (_lastAnalysis is var (node, kind) && AnalysisPanel.IsVisible) _ = AnalyzeAsync(node, kind);
        };
        CloseAnalysisButton.Click += (_, _) =>
        {
            _analysisCts?.Cancel();
            AnalysisPanel.IsVisible = false;
        };
        AnalysisResults.SelectionChanged += (_, _) =>
        {
            if (AnalysisResults.SelectedItem is AssemblyTreeNode result)
                _ = OpenSearchResultAsync(result);
        };
        RemoveAssemblyMenuItem.Click += (_, _) =>
        {
            if (TypeTree.SelectedItem is TreeViewItem { Header: AssemblyTreeNode { Kind: AssemblyNodeKind.Assembly } } item)
                RemoveAssembly(item);
        };

        // Alt+←/→ and the mouse's back/forward buttons, anywhere in the window (Tunnel: before the
        // editor or tree can use the keys).
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.KeyModifiers == KeyModifiers.Alt && e.Key == Key.Left) GoBack();
            else if (e.KeyModifiers == KeyModifiers.Alt && e.Key == Key.Right) GoForward();
            else if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.S) _ = SaveCodeAsync();
            else if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.W) CloseActiveTab();
            else if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.Tab) ActivateNextTab(+1);
            else if (e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key == Key.Tab) ActivateNextTab(-1);
            else if (e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key == Key.F)
            {
                SearchBox.Focus();
                SearchBox.SelectAll();
            }
            // ILSpy's Analyze shortcut: the selected row's first analysis
            else if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.R
                     && TypeTree.SelectedItem is TreeViewItem { Header: AssemblyTreeNode selected }
                     && CSharpDecompilerService.AnalysesFor(selected.Kind, selected.Member) is [var first, ..])
            {
                _ = AnalyzeAsync(selected, first);
            }
            else return;
            e.Handled = true;
        }, RoutingStrategies.Tunnel);

        SearchBox.TextChanged += (_, _) => _ = SearchDebouncedAsync(SearchBox.Text ?? "");
        SearchBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) SearchBox.Text = "";
            else if (e.Key == Key.Down && SearchResults.ItemCount > 0)
            {
                SearchResults.SelectedIndex = 0;
                SearchResults.ContainerFromIndex(0)?.Focus();
            }
            else return;
            e.Handled = true;
        };
        SearchResults.SelectionChanged += (_, _) =>
        {
            if (SearchResults.SelectedItem is AssemblyTreeNode result)
                _ = OpenSearchResultAsync(result);
        };
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            var kind = e.GetCurrentPoint(this).Properties.PointerUpdateKind;
            if (kind == PointerUpdateKind.XButton1Pressed) GoBack();
            else if (kind == PointerUpdateKind.XButton2Pressed) GoForward();
            else return;
            e.Handled = true;
        }, RoutingStrategies.Tunnel);

        var editor = new CodeTextEditor
        {
            IsReadOnly = true,
            ShowLineNumberText = true,
            ShowBreakpoints = false,
            Background = themeService.GetBrush("RunPad.Editor.BackgroundBrush")
        };

        editor.GoToDefinitionRequested += (_, _) => _ = NavigateAsync();
        // Ctrl+click = F12 at the clicked spot (Tunnel: before the text area starts a selection).
        editor.TextArea.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.KeyModifiers != KeyModifiers.Control
                || !e.GetCurrentPoint(editor).Properties.IsLeftButtonPressed) return;
            if (editor.GetPositionFromPoint(e.GetPosition(editor)) is not { } position) return;
            e.Handled = true;
            _ = NavigateAtAsync(position.Line, position.Column);
        }, RoutingStrategies.Tunnel);

        // Ctrl held over a navigable identifier underlines it and shows a hand cursor, like ILSpy/VS.
        editor.TextArea.TextView.LineTransformers.Add(_referenceUnderline);
        editor.TextArea.AddHandler(PointerMovedEvent, (_, e) =>
        {
            _lastPointerPosition = editor.GetPositionFromPoint(e.GetPosition(editor)) is { } p ? (p.Line, p.Column) : null;
            UpdateReferenceHover(e.KeyModifiers.HasFlag(KeyModifiers.Control) ? _lastPointerPosition : null);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        editor.TextArea.PointerExited += (_, _) =>
        {
            _lastPointerPosition = null;
            UpdateReferenceHover(null);
        };
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key is Key.LeftCtrl or Key.RightCtrl) UpdateReferenceHover(_lastPointerPosition);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyUpEvent, (_, e) =>
        {
            if (e.Key is Key.LeftCtrl or Key.RightCtrl) UpdateReferenceHover(null);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        EditorHost.Content = editor;

        // Same reasoning as EditorTabItemControl's own onAttached dance: SetLanguage calls Redraw()
        // internally, which needs the editor already in the visual tree.
        EventHandler<VisualTreeAttachmentEventArgs>? onAttached = null;
        onAttached = (_, _) =>
        {
            editor.AttachedToVisualTree -= onAttached;
            editor.SetLanguage(themeService.CurrentTheme.Editor.TextMateName, ".cs");
            if (_pendingInitialResult is { } initial)
            {
                // decompiled as default C#; the row and target may already be known
                Show(initial, _initialPageRow, _initialPageTarget, DecompilerView.Default);
                if (_initialPageRow is not null) SelectSilently(_initialPageRow);
                _ = RefreshCurrentAsync(); // with a target, in the current view if that's another one
            }
        };
        editor.AttachedToVisualTree += onAttached;
        RefreshTabStrip();
    }

    // XAML previewer only — mirrors the old DecompiledViewWindow's own empty ctor.
    public AssemblyExplorerWindow()
    {
        InitializeComponent();
        _windowService = null!;
        _activeTab = _tabs[0];
    }

    // Browse adds the picked assembly to the tree (like ILSpy's assembly list) and selects it.
    internal async Task BrowseAsync()
    {
        var path = await _windowService.ShowOpenAssemblyFileDialogAsync(_startFolder);
        if (path is null) return;
        if (await LoadAssemblyAsync(path) is { } item)
            SelectAndOpen(item);
    }

    /// <summary>
    /// Adds <paramref name="assemblyPath"/> to the tree, or returns its row if it's already there —
    /// the tree holds any number of assemblies (Browse, a References row, F12 into another assembly).
    /// Null if the file isn't a readable assembly (an error dialog has been shown).
    /// </summary>
    internal async Task<TreeViewItem?> LoadAssemblyAsync(string assemblyPath)
    {
        if (FindAssembly(assemblyPath) is { } existing) return existing.Item;

        AssemblyDescription description;
        IReadOnlyList<AssemblyTypeEntry> types;
        try
        {
            (description, types) = await Task.Run(() => (
                CSharpDecompilerService.DescribeAssembly(assemblyPath),
                CSharpDecompilerService.EnumerateTypes(assemblyPath)));
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException)
        {
            await _windowService.ShowErrorAsync(
                "Browse Assembly Failed", $"Could not load '{Path.GetFileName(assemblyPath)}':\n{ex.Message}");
            return null;
        }

        // another load of the same file may have finished while this one was reading it
        if (FindAssembly(assemblyPath) is { } loadedMeanwhile) return loadedMeanwhile.Item;

        var item = BuildAssemblyItem(description, types, assemblyPath);
        _assemblies.Add(new LoadedAssembly(assemblyPath, description, item));
        TypeTree.Items.Add(item);
        item.IsExpanded = true;
        AssemblyPathBlock.Text = assemblyPath;
        OnAssembliesChanged();
        return item;
    }

    // An assembly row: References (each referenced assembly, loadable from there), then its namespaces.
    private TreeViewItem BuildAssemblyItem(
        AssemblyDescription description, IReadOnlyList<AssemblyTypeEntry> types, string assemblyPath)
    {
        var text = description.Version is { } version ? $"{description.Name} ({version})" : description.Name;
        var item = new TreeViewItem { Header = Node(AssemblyNodeKind.Assembly, text, assemblyPath: assemblyPath) };

        var references = new TreeViewItem { Header = Node(AssemblyNodeKind.References, "References") };
        foreach (var reference in description.References)
        {
            references.Items.Add(new TreeViewItem
            {
                Header = Node(AssemblyNodeKind.Reference, reference.Name, reference: reference,
                    isUnavailable: reference.ResolvedPath is null),
            });
        }
        item.Items.Add(references);

        if (description.Resources.Count > 0)
        {
            var resources = new TreeViewItem { Header = Node(AssemblyNodeKind.Resources, "Resources") };
            foreach (var resource in description.Resources)
            {
                resources.Items.Add(new TreeViewItem
                {
                    Header = Node(AssemblyNodeKind.Resource, resource.Name,
                        resource.IsPublic ? AssemblyAccess.Public : AssemblyAccess.Private,
                        assemblyPath: assemblyPath, isUnavailable: !resource.IsEmbedded, resource: resource),
                });
            }
            item.Items.Add(resources);
        }

        foreach (var group in types.GroupBy(t => t.Namespace).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var nsItem = new TreeViewItem
            {
                Header = Node(AssemblyNodeKind.Namespace, string.IsNullOrEmpty(group.Key) ? "-" : group.Key),
            };
            foreach (var entry in group.OrderBy(e => e.Text, StringComparer.Ordinal))
                nsItem.Items.Add(CreateTypeItem(entry, assemblyPath));

            item.Items.Add(nsItem);
        }
        return item;
    }

    internal IReadOnlyList<string> LoadedAssemblyPaths => _assemblies.Select(a => a.Path).ToList();

    private static readonly string[] AssemblyExtensions = [".dll", ".exe", ".winmd"];

    // Files dropped on the window: each assembly is added (or found) in the tree and the last one is
    // selected, like Browse. Other files are ignored; an unreadable assembly gets Browse's error dialog.
    internal async Task OpenDroppedFilesAsync(IEnumerable<string> paths)
    {
        TreeViewItem? last = null;
        foreach (var path in paths.Where(p => AssemblyExtensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase)))
        {
            if (await LoadAssemblyAsync(path) is { } item) last = item;
        }
        if (last is not null) SelectAndOpen(last);
    }

    private LoadedAssembly? FindAssembly(string assemblyPath) =>
        _assemblies.FirstOrDefault(a => SamePath(a.Path, assemblyPath));

    // Removes an assembly row (context menu / Delete). The code pane keeps what it shows; history pages
    // keep their code but no longer point at rows (they may have been in the removed assembly).
    internal void RemoveAssembly(TreeViewItem assemblyItem)
    {
        var assembly = _assemblies.FirstOrDefault(a => a.Item == assemblyItem);
        if (assembly is null) return;

        _assemblies.Remove(assembly);
        TypeTree.Items.Remove(assemblyItem);
        foreach (var expanded in _expansions.Keys.Where(i => IsWithin(i, assemblyItem)).ToList())
            _expansions.Remove(expanded);
        DetachHistoryFromTree();
        if (_assemblies.Count == 0) AssemblyPathBlock.Text = "";
        OnAssembliesChanged();

        static bool IsWithin(TreeViewItem item, TreeViewItem ancestor)
        {
            for (StyledElement? e = item; e is not null; e = e.Parent)
            {
                if (e == ancestor) return true;
            }
            return false;
        }
    }

    // The searchable set changed: drop indexes of removed assemblies and refresh visible results.
    private void OnAssembliesChanged()
    {
        ++_assemblySetVersion;
        foreach (var path in _searchIndexes.Keys.Where(p => FindAssembly(p) is null).ToList())
            _searchIndexes.Remove(path);
        if (!string.IsNullOrWhiteSpace(_lastQuery))
            _ = SearchAsync(_lastQuery);
    }

    // A References row: load that assembly into the tree (or find it there) and open it.
    internal async Task OpenReferenceAsync(AssemblyReferenceEntry reference)
    {
        if (reference.ResolvedPath is not { } path) return;
        if (await LoadAssemblyAsync(path) is { } item)
            SelectAndOpen(item);
    }

    // Selects a row the normal way, so the selection decompiles it.
    private void SelectAndOpen(TreeViewItem item)
    {
        TypeTree.SelectedItem = item;
        item.BringIntoView();
    }

    private sealed record LoadedAssembly(string Path, AssemblyDescription Description, TreeViewItem Item);

    // A type row — top-level, nested, or a base/derived entry (which expands too, like ILSpy, from its
    // own assembly: a BCL base type lists the BCL's members). Gets a placeholder child (so the expander
    // shows) and loads its contents on first expansion; an unresolved base type stays a leaf.
    private TreeViewItem CreateTypeItem(AssemblyTypeEntry entry, string browsedAssemblyPath)
    {
        var node = Node(entry.Kind, entry.Text, entry.Access, entry.IsStatic, entry.IsAbstract,
            entry.AssemblyPath ?? browsedAssemblyPath, type: entry);
        var item = new TreeViewItem { Header = node };
        if (!entry.IsResolved) return item;

        item.Items.Add(new TreeViewItem { Header = "Loading..." });
        item.PropertyChanged += (_, e) =>
        {
            if (e.Property == TreeViewItem.IsExpandedProperty && item.IsExpanded)
                _ = ExpandAsync(item);
        };
        return item;
    }

    // internal: test hook (expanding a TreeViewItem headlessly fires this too, but tests need the Task).
    internal Task ExpandAsync(TreeViewItem item)
    {
        if (!_expansions.TryGetValue(item, out var task))
        {
            task = LoadTypeContentsAsync(item);
            _expansions[item] = task;
        }
        return task;
    }

    private async Task LoadTypeContentsAsync(TreeViewItem item)
    {
        if (item.Header is not AssemblyTreeNode { Type: { } type, AssemblyPath: { } assemblyPath }) return;

        AssemblyTypeContents contents;
        try
        {
            contents = await Task.Run(() => CSharpDecompilerService.GetTypeContents(assemblyPath, type.FullTypeName));
        }
        catch (Exception ex)
        {
            item.Items.Clear();
            item.Items.Add(new TreeViewItem { Header = $"Failed to load: {ex.Message}" });
            return;
        }

        item.Items.Clear();
        AddGroup(AssemblyNodeKind.BaseTypes, "Base Types", contents.BaseTypes);
        AddGroup(AssemblyNodeKind.DerivedTypes, "Derived Types", contents.DerivedTypes);
        foreach (var nested in contents.NestedTypes)
            item.Items.Add(CreateTypeItem(nested, assemblyPath));
        foreach (var member in contents.Members)
        {
            item.Items.Add(new TreeViewItem
            {
                Header = Node(member.Kind, member.Text, member.Access, member.IsStatic, member.IsAbstract,
                    assemblyPath, member: member),
            });
        }

        void AddGroup(AssemblyNodeKind kind, string text, IReadOnlyList<AssemblyTypeEntry> entries)
        {
            if (entries.Count == 0) return;
            var group = new TreeViewItem { Header = Node(kind, text) };
            foreach (var entry in entries)
                group.Items.Add(CreateTypeItem(entry, assemblyPath));
            item.Items.Add(group);
        }
    }

    // Selecting a tree row decompiles it in place, same pane recursive F12 uses (no new window — dnSpy/
    // ILSpy don't pop one per type either): a type shows the whole type, a member only itself.
    // Namespace and Base/Derived Types group rows have nothing to decompile. `item` is the row the
    // result is remembered with in the Back/Forward history (null: tests driving a bare node).
    internal async Task SelectNodeAsync(AssemblyTreeNode node, TreeViewItem? item = null)
    {
        var plainText = node switch
        {
            { Type.IsResolved: false } => $"// Could not resolve '{node.Type.FullTypeName}'.\n",
            // a References row: what it is and where it resolves (double-click/Enter opens it)
            { Reference: { } reference } => $"// {reference.FullName}\n" + (reference.ResolvedPath is { } resolved
                ? $"// {resolved}\n// Double-click or press Enter to open it in the tree.\n"
                : "// The referenced assembly could not be found.\n"),
            _ => null,
        };
        if (plainText is not null)
        {
            ++_requestVersion;
            Show(new DecompiledMemberResult(plainText, 1, node.Text), item, target: null);
            return;
        }

        // a resource page is the same in C# and IL: read off the UI thread, not re-rendered on a view switch
        if (node is { Resource: { } resource, AssemblyPath: { } resourceAssembly })
        {
            var resourceVersion = ++_requestVersion;
            DecompiledMemberResult page;
            try
            {
                page = await Task.Run(() => CSharpDecompilerService.DescribeResource(resourceAssembly, resource.Name));
            }
            catch (Exception ex)
            {
                page = new DecompiledMemberResult($"// Failed to read the resource: {ex.Message}\n", 1, node.Text);
            }
            if (resourceVersion == _requestVersion) Show(page, item, target: null);
            return;
        }

        var target = node switch
        {
            { Member: { } member, AssemblyPath: { } path } => new PageTarget(path, member.MetadataToken, node.Text),
            { Type: { } type, AssemblyPath: { } path } => new PageTarget(path, type.MetadataToken, node.Text),
            { Kind: AssemblyNodeKind.Assembly, AssemblyPath: { } path } =>
                new PageTarget(path, AssemblyDescription.Token, node.Text),
            _ => null,
        };
        if (target is null) return;

        await RenderAndShowAsync(++_requestVersion, target, item);
    }

    // The request (by its _requestVersion) whose new page is being rendered: a language/version switch
    // meanwhile re-renders that page rather than the one it's about to replace.
    private int? _pendingPageVersion;

    // Renders `target` as a new page in the current view and shows it, labelled with the view it was
    // rendered in — rendered again if the view changes meanwhile — unless a newer request (another
    // selection, Back/Forward, a tab switch) superseded `version`. `beforeShow` runs right before.
    private async Task RenderAndShowAsync(int version, PageTarget target, TreeViewItem? item, Action? beforeShow = null)
    {
        _pendingPageVersion = version;
        try
        {
            while (true)
            {
                var view = _view;
                var result = await RenderAsync(target, view);
                if (version != _requestVersion) return;
                if (view != _view) continue;

                beforeShow?.Invoke();
                Show(result, item, target, view);
                return;
            }
        }
        finally
        {
            if (_pendingPageVersion == version) _pendingPageVersion = null;
        }
    }

    // ── Analyze (ILSpy's analyzer: context menu / Ctrl+R) ──

    internal static string AnalysisLabel(AnalysisKind kind) => kind switch
    {
        AnalysisKind.UsedBy => "Used By",
        AnalysisKind.Uses => "Uses",
        AnalysisKind.ReadBy => "Read By",
        AnalysisKind.AssignedBy => "Assigned By",
        AnalysisKind.InstantiatedBy => "Instantiated By",
        AnalysisKind.OverriddenBy => "Overridden By",
        AnalysisKind.ImplementedBy => "Implemented By",
        AnalysisKind.ExposedBy => "Exposed By",
        _ => kind.ToString(),
    };

    // The tree's context menu for the selected row: its analyses, and "Remove assembly" on an assembly row.
    private void BuildTreeContextMenu()
    {
        var selected = TypeTree.SelectedItem as TreeViewItem;
        RemoveAssemblyMenuItem.IsEnabled = selected?.Header is AssemblyTreeNode { Kind: AssemblyNodeKind.Assembly };

        foreach (var old in TreeContextMenu.Items.OfType<Control>().Where(c => c.Tag is "analysis" or "row").ToList())
            TreeContextMenu.Items.Remove(old);
        if (selected?.Header is not AssemblyTreeNode node) return;

        var position = 0;
        // rows that show a page can open it in a new tab (namespace/group rows show nothing)
        if (node.Type is not null || node.Member is not null || node.Reference is not null || node.Resource is not null
            || node.Kind == AssemblyNodeKind.Assembly)
        {
            var newTab = new MenuItem { Header = "Open in New Tab", Tag = "row" };
            newTab.Click += (_, _) => _ = OpenInNewTabAsync(node, selected);
            TreeContextMenu.Items.Insert(position++, newTab);
            TreeContextMenu.Items.Insert(position++, new Separator { Tag = "row" });
        }

        var analyses = CSharpDecompilerService.AnalysesFor(node.Kind, node.Member);
        for (var i = 0; i < analyses.Count; i++)
        {
            var kind = analyses[i];
            var item = new MenuItem { Header = "Analyze: " + AnalysisLabel(kind), Tag = "analysis" };
            if (i == 0) item.InputGesture = new KeyGesture(Key.R, KeyModifiers.Control);
            item.Click += (_, _) => _ = AnalyzeAsync(node, kind);
            TreeContextMenu.Items.Insert(position++, item);
        }
        if (analyses.Count > 0)
            TreeContextMenu.Items.Insert(position, new Separator { Tag = "analysis" });
    }

    internal ContextMenu TreeMenu => TreeContextMenu;
    internal ListBox AnalysisResultList => AnalysisResults;

    // Runs one analysis of a type or member row over every loaded assembly, off the UI thread; a newer
    // analysis (or closing the pane) cancels a running one. Results open like search hits.
    internal async Task AnalyzeAsync(AssemblyTreeNode node, AnalysisKind kind)
    {
        var token = node.Member?.MetadataToken ?? node.Type?.MetadataToken;
        if (token is not { } targetToken || node.AssemblyPath is not { } assemblyPath) return;

        _analysisCts?.Cancel();
        var cts = _analysisCts = new CancellationTokenSource();
        _lastAnalysis = (node, kind);
        AnalysisTitle.Text = $"{AnalysisLabel(kind)}: {node.Text}";
        AnalysisStatus.Text = "Analyzing...";
        AnalysisResults.ItemsSource = null;
        AnalysisPanel.IsVisible = true;
        // only Used By of a member can count virtual calls of what it overrides/implements
        IncludeVirtualCallsBox.IsVisible = kind == AnalysisKind.UsedBy && node.Member is not null;
        var includeVirtualCalls = IncludeVirtualCallsBox.IsVisible && IncludeVirtualCallsBox.IsChecked == true;

        var assemblies = _assemblies.ToList();
        var paths = assemblies.Select(a => a.Path).ToList();
        IReadOnlyList<AssemblySearchEntry> hits;
        try
        {
            hits = await Task.Run(
                () => CSharpDecompilerService.Analyze(
                    kind, assemblyPath, targetToken, paths, cts.Token, includeVirtualCalls),
                cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            if (cts == _analysisCts) AnalysisStatus.Text = $"Analysis failed: {ex.Message}";
            return;
        }

        if (cts != _analysisCts) return;

        // a hit can be in an assembly that isn't in the tree (Uses → the BCL): name it like search does
        var names = assemblies.ToDictionary(a => a.Path, a => a.Description.Name, StringComparer.OrdinalIgnoreCase);
        var several = hits.Select(h => h.AssemblyPath).Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any()
                      || hits.Any(h => !names.ContainsKey(h.AssemblyPath));
        AnalysisResults.ItemsSource = hits
            .Select(hit => Node(hit.Kind, hit.Text, hit.Member?.Access ?? hit.Type.Access,
                hit.Member?.IsStatic ?? hit.Type.IsStatic, hit.Member?.IsAbstract ?? hit.Type.IsAbstract,
                hit.AssemblyPath, hit.Type, hit.Member,
                several
                    ? $"{hit.Location} ({(names.TryGetValue(hit.AssemblyPath, out var name) ? name : Path.GetFileNameWithoutExtension(hit.AssemblyPath))})"
                    : hit.Location))
            .ToList();
        AnalysisStatus.Text = hits.Count switch
        {
            0 => "No results",
            1 => "1 result",
            _ => $"{hits.Count} results",
        };
    }

    // ── Search (ILSpy's search pane, Ctrl+Shift+F) ──

    // Waits for typing to pause before searching; a newer keystroke cancels the pending search.
    private async Task SearchDebouncedAsync(string query)
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(SearchDelay, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        await SearchAsync(query, cts.Token);
    }

    // internal: test hook (no debounce). Builds the index off the UI thread on first use, filters it
    // off the UI thread too, and drops results a newer query or another assembly made stale.
    internal async Task SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        _lastQuery = query; // re-run when the set of assemblies changes
        if (string.IsNullOrWhiteSpace(query) || _assemblies.Count == 0)
        {
            SearchResults.ItemsSource = null;
            SearchPanel.IsVisible = false;
            return;
        }

        // every loaded assembly, each index built once (in parallel, off the UI thread)
        var assemblySet = _assemblySetVersion;
        var assemblies = _assemblies.ToList();
        var indexTasks = assemblies.Select(a =>
        {
            if (!_searchIndexes.TryGetValue(a.Path, out var task))
            {
                var path = a.Path;
                task = Task.Run(() => CSharpDecompilerService.BuildSearchIndex(path));
                _searchIndexes[path] = task;
            }
            return (a.Path, Task: task);
        }).ToList();

        IReadOnlyList<AssemblySearchEntry> hits;
        try
        {
            var indexes = await Task.WhenAll(indexTasks.Select(i => i.Task));
            hits = await Task.Run(
                () => CSharpDecompilerService.Search(indexes.SelectMany(i => i).ToList(), query, SearchLimit + 1),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            foreach (var (path, task) in indexTasks.Where(i => i.Task.IsFaulted))
            {
                if (_searchIndexes.TryGetValue(path, out var current) && current == task)
                    _searchIndexes.Remove(path); // let the next keystroke retry
            }
            SearchStatus.Text = $"Search failed: {ex.Message}";
            SearchResults.ItemsSource = null;
            SearchPanel.IsVisible = true;
            return;
        }

        if (cancellationToken.IsCancellationRequested || assemblySet != _assemblySetVersion) return;

        // with several assemblies, say which one a hit is in
        var names = assemblies.Count > 1
            ? assemblies.ToDictionary(a => a.Path, a => a.Description.Name)
            : null;
        SearchResults.ItemsSource = hits.Take(SearchLimit)
            .Select(hit => Node(hit.Kind, hit.Text, hit.Member?.Access ?? hit.Type.Access,
                hit.Member?.IsStatic ?? hit.Type.IsStatic, hit.Member?.IsAbstract ?? hit.Type.IsAbstract,
                hit.AssemblyPath, hit.Type, hit.Member,
                names is null ? hit.Location : $"{hit.Location} ({names[hit.AssemblyPath]})"))
            .ToList();
        SearchStatus.Text = hits.Count switch
        {
            0 => "No results",
            1 => "1 result",
            > SearchLimit => $"First {SearchLimit} results",
            _ => $"{hits.Count} results",
        };
        SearchPanel.IsVisible = true;
    }

    internal ListBox SearchResultList => SearchResults;

    // Like ILSpy: a search hit is revealed and selected in the tree, and decompiled from there.
    internal async Task OpenSearchResultAsync(AssemblyTreeNode result)
    {
        if (result is not { Type: { } type, AssemblyPath: { } assemblyPath }) return;

        var version = ++_requestVersion;
        // an analysis hit can be in an assembly that isn't in the tree yet (Uses → the BCL)
        await LoadAssemblyAsync(assemblyPath);
        if (version != _requestVersion) return;
        var (_, target) = await RevealAsync(assemblyPath, type.FullTypeName, result.Member?.MetadataToken);
        if (version != _requestVersion) return;

        if (target is { Header: AssemblyTreeNode node })
        {
            SelectSilently(target);
            await SelectNodeAsync(node, target);
        }
        else
        {
            await SelectNodeAsync(result); // not in the tree (shouldn't happen): decompile the hit itself
        }
    }

    // ── C# / IL (ILSpy's language and version boxes) ──

    /// <summary>The C# versions offered, newest first — "Latest" is the decompiler's own latest.</summary>
    internal static readonly IReadOnlyList<(string Label, LanguageVersion Version)> LanguageVersions =
    [
        ("C# latest", LanguageVersion.Latest),
        ("C# 14.0", LanguageVersion.CSharp14_0),
        ("C# 13.0", LanguageVersion.CSharp13_0),
        ("C# 12.0", LanguageVersion.CSharp12_0),
        ("C# 11.0", LanguageVersion.CSharp11_0),
        ("C# 10.0", LanguageVersion.CSharp10_0),
        ("C# 9.0", LanguageVersion.CSharp9_0),
        ("C# 8.0", LanguageVersion.CSharp8_0),
        ("C# 7.3", LanguageVersion.CSharp7_3),
        ("C# 7.0", LanguageVersion.CSharp7),
        ("C# 6.0", LanguageVersion.CSharp6),
        ("C# 5.0", LanguageVersion.CSharp5),
        ("C# 4.0", LanguageVersion.CSharp4),
        ("C# 3.0", LanguageVersion.CSharp3),
        ("C# 2.0", LanguageVersion.CSharp2),
        ("C# 1.0", LanguageVersion.CSharp1),
    ];

    internal DecompilerView View => _view;

    private void OnViewBoxChanged()
    {
        if (_syncingViewBoxes || LanguageBox.SelectedIndex < 0 || VersionBox.SelectedIndex < 0) return;
        _ = SetViewAsync(new DecompilerView(
            LanguageBox.SelectedIndex == 1 ? DecompilerLanguage.IL : DecompilerLanguage.CSharp,
            LanguageVersions[VersionBox.SelectedIndex].Version));
    }

    // Switching language/version re-renders the page in place (not a new history page); pages left in
    // another view are re-rendered when Back/Forward returns to them.
    internal async Task SetViewAsync(DecompilerView view)
    {
        if (view == _view) return;
        _view = view;
        _syncingViewBoxes = true;
        try
        {
            LanguageBox.SelectedIndex = view.Language == DecompilerLanguage.IL ? 1 : 0;
            VersionBox.SelectedIndex = Math.Max(0, LanguageVersions.ToList().FindIndex(v => v.Version == view.Version));
            VersionBox.IsVisible = view.Language == DecompilerLanguage.CSharp;
        }
        finally
        {
            _syncingViewBoxes = false;
        }

        await RefreshCurrentAsync();
    }

    private async Task RefreshCurrentAsync()
    {
        // a new page still rendering replaces the current one and renders itself in the new view
        if (_pendingPageVersion == _requestVersion) return;
        if (_activeTab.Current is not { Target: { } target } current || current.View == _view) return;

        var version = ++_requestVersion;
        var view = _view;
        var result = await RenderAsync(target, view);
        if (version != _requestVersion) return;
        _activeTab.Current = current with { Result = result, CaretLine = result.Line, View = view };
        ApplyResult(result, result.Line);
    }

    private static async Task<DecompiledMemberResult> RenderAsync(PageTarget target, DecompilerView view)
    {
        try
        {
            return await Task.Run(() => view.Language == DecompilerLanguage.IL
                ? IlDisassembler.DisassembleEntity(target.AssemblyPath, target.Token, target.Title)
                : CSharpDecompilerService.DecompileEntity(
                    target.AssemblyPath, target.Token, target.Title, view.Version, target.FocusToken));
        }
        catch (Exception ex)
        {
            return new DecompiledMemberResult($"// Failed to decompile: {ex.Message}", 1, target.Title);
        }
    }

    // One palette for every theme: icons sit straight on the panel background, and each colour keeps a
    // WCAG non-text contrast of at least 3:1 against all eight themes' panel backgrounds (#20242B ...
    // #F6F3EE) — checked by AssemblyExplorerWindowTests, so a new theme or colour can't silently fail it.
    internal static readonly IReadOnlyDictionary<AssemblyNodeKind, (string IconKey, Color Color)> KindIcons =
        new Dictionary<AssemblyNodeKind, (string, Color)>
        {
            [AssemblyNodeKind.Namespace] = ("Namespace", Color.FromRgb(0x86, 0x86, 0x86)),
            [AssemblyNodeKind.Class] = ("Type", Color.FromRgb(0xC2, 0x7C, 0x0E)),
            [AssemblyNodeKind.Struct] = ("Type", Color.FromRgb(0x2F, 0x86, 0xD6)),
            [AssemblyNodeKind.Interface] = ("Interface", Color.FromRgb(0x2E, 0x8B, 0xC0)),
            [AssemblyNodeKind.Enum] = ("Enum", Color.FromRgb(0xC2, 0x7C, 0x0E)),
            [AssemblyNodeKind.Delegate] = ("Delegate", Color.FromRgb(0x90, 0x61, 0xD6)),
            [AssemblyNodeKind.Field] = ("Field", Color.FromRgb(0x3D, 0x8A, 0xC7)),
            [AssemblyNodeKind.Property] = ("Property", Color.FromRgb(0x86, 0x86, 0x86)),
            [AssemblyNodeKind.Event] = ("Event", Color.FromRgb(0xD0, 0x76, 0x0B)),
            [AssemblyNodeKind.Constructor] = ("Member", Color.FromRgb(0x7F, 0x6B, 0xD8)),
            [AssemblyNodeKind.Method] = ("Member", Color.FromRgb(0x90, 0x61, 0xD6)),
            [AssemblyNodeKind.BaseTypes] = ("BaseTypes", Color.FromRgb(0x86, 0x86, 0x86)),
            [AssemblyNodeKind.DerivedTypes] = ("DerivedTypes", Color.FromRgb(0x86, 0x86, 0x86)),
            [AssemblyNodeKind.Assembly] = ("Assembly", Color.FromRgb(0x7F, 0x6B, 0xD8)),
            [AssemblyNodeKind.References] = ("References", Color.FromRgb(0x86, 0x86, 0x86)),
            [AssemblyNodeKind.Reference] = ("Assembly", Color.FromRgb(0x86, 0x86, 0x86)),
            [AssemblyNodeKind.Resources] = ("References", Color.FromRgb(0x86, 0x86, 0x86)),
            [AssemblyNodeKind.Resource] = ("Resource", Color.FromRgb(0x86, 0x86, 0x86)),
        };

    private AssemblyTreeNode Node(
        AssemblyNodeKind kind, string text, AssemblyAccess access = AssemblyAccess.Public,
        bool isStatic = false, bool isAbstract = false, string? assemblyPath = null,
        AssemblyTypeEntry? type = null, AssemblyMemberEntry? member = null, string? location = null,
        AssemblyReferenceEntry? reference = null, bool isUnavailable = false, AssemblyResourceEntry? resource = null)
    {
        var (iconKey, color) = KindIcons[kind];
        var accessKey = access switch
        {
            AssemblyAccess.Private or AssemblyAccess.PrivateProtected => "Private",
            AssemblyAccess.Protected or AssemblyAccess.ProtectedInternal => "Protected",
            AssemblyAccess.Internal => "Internal",
            _ => null,
        };
        var icons = new AssemblyTreeNodeIcons(
            Geometry("AsmIcon." + iconKey),
            new SolidColorBrush(color),
            accessKey is null ? null : Geometry("AsmOverlay." + accessKey),
            isStatic ? Geometry("AsmOverlay.Static") : null);
        return new AssemblyTreeNode(
            kind, text, access, icons, isStatic, isAbstract, assemblyPath, type, member, location, reference,
            isUnavailable, resource);

        Geometry? Geometry(string key) => this.TryFindResource(key, out var resource) ? resource as Geometry : null;
    }

    // internal (not private): lets tests drive a navigation directly without simulating a real F12
    // keypress + caret placement — same rationale as every other "internal test hook" in this session
    // (headless Avalonia has no reliable way to fake caret-dependent keyboard interaction either).
    // ── Ctrl+hover: underline the identifier F12/Ctrl+click would follow ──

    private readonly ReferenceUnderline _referenceUnderline = new();
    private (int Line, int Column)? _lastPointerPosition;

    internal ReferenceSpan? HoveredReference => _referenceUnderline.Span;

    // `position` null = Ctrl released / pointer gone. Only redraws when the underlined span changes.
    internal void UpdateReferenceHover((int Line, int Column)? position)
    {
        var span = position is { } p && _currentTree is { } tree
            ? CSharpDecompilerService.FindReferenceSpanAtPosition(tree, p.Line, p.Column)
            : null;
        if (span == _referenceUnderline.Span) return;

        _referenceUnderline.Span = span;
        _referenceUnderline.Range = span is null ? null : (
            Editor.Document.GetOffset(span.StartLine, span.StartColumn),
            Editor.Document.GetOffset(span.EndLine, span.EndColumn));
        Editor.TextArea.TextView.Cursor = span is null ? null : new Cursor(StandardCursorType.Hand);
        Editor.TextArea.TextView.Redraw();
    }

    private sealed class ReferenceUnderline : AvaloniaEdit.Rendering.DocumentColorizingTransformer
    {
        public ReferenceSpan? Span { get; set; }
        public (int Start, int End)? Range { get; set; }

        protected override void ColorizeLine(AvaloniaEdit.Document.DocumentLine line)
        {
            if (Range is not var (start, end)) return;
            var from = Math.Max(start, line.Offset);
            var to = Math.Min(end, line.EndOffset);
            if (from >= to) return;
            ChangeLinePart(from, to, element => element.TextRunProperties.SetTextDecorations(TextDecorations.Underline));
        }
    }

    // Ctrl+click: F12 at the clicked position. internal: test hook (no pixel-to-text mapping needed).
    internal Task NavigateAtAsync(int line, int column)
    {
        Editor.TextArea.Caret.Line = line;
        Editor.TextArea.Caret.Column = column;
        return NavigateAsync();
    }

    internal async Task NavigateAsync()
    {
        if (_currentTree is not { } tree) return;

        var caretLine = Editor.TextArea.Caret.Line;
        var caretColumn = Editor.TextArea.Caret.Column;

        var reference = await Task.Run(() =>
            CSharpDecompilerService.ResolveReferenceAtPosition(tree, caretLine, caretColumn));
        if (reference is null) return;

        var version = ++_requestVersion;

        // Like ILSpy: the target's assembly is added to the tree if it isn't there yet, the target is
        // revealed and selected, and the selection decompiles it (a member on its own). A member the
        // tree hides (e.g. an accessor), or an assembly that can't be loaded, falls back to the whole
        // declaring type with the caret on the member.
        TreeViewItem? typeItem = null;
        if (await LoadAssemblyAsync(reference.AssemblyPath) is not null)
        {
            if (version != _requestVersion) return;
            (typeItem, var target) = await RevealAsync(reference.AssemblyPath,
                reference.ContainingTypeFullName, reference.MemberName is null ? null : reference.MetadataToken);
            if (version != _requestVersion) return;

            if (target is { Header: AssemblyTreeNode node })
            {
                SelectSilently(target);
                await SelectNodeAsync(node, target);
                return;
            }
        }

        var page = new PageTarget(
            reference.AssemblyPath, reference.ContainingTypeMetadataToken, reference.DisplayName,
            reference.MemberName is null ? 0 : reference.MetadataToken);
        // Back on the UI thread after the render — Avalonia's own SynchronizationContext resumes the
        // continuation there, same as EditorTabItemControl.ShowGoToDefinitionAsync's identical
        // await Task.Run(...) pattern (an explicit
        // Dispatcher.Post here was a real, caught-by-test bug, not just unnecessary).
        await RenderAndShowAsync(version, page, typeItem, () =>
        {
            if (typeItem is not null) SelectSilently(typeItem);
        });
    }

    // The rows of a type of a loaded assembly and, when memberToken is given, of that member (null
    // when the tree doesn't list it, e.g. an accessor) — expanding the path to them on the way.
    private async Task<(TreeViewItem? TypeItem, TreeViewItem? Target)> RevealAsync(
        string assemblyPath, string typeFullName, int? memberToken)
    {
        var typeItem = FindAssembly(assemblyPath) is { } assembly
            ? await RevealTypeAsync(assembly.Item, typeFullName)
            : null;
        if (typeItem is null || memberToken is not { } token) return (typeItem, typeItem);

        await ExpandAsync(typeItem);
        var memberItem = typeItem.Items.OfType<TreeViewItem>().FirstOrDefault(i =>
            i.Header is AssemblyTreeNode { Member.MetadataToken: var t } && t == token);
        return (typeItem, memberItem);
    }

    // Expands the path to a type of an assembly row ("Ns.Outer+Inner": assembly → namespace row →
    // Outer → Inner) and returns its row, or null if the tree doesn't list it (e.g. compiler-generated).
    private async Task<TreeViewItem?> RevealTypeAsync(TreeViewItem assemblyItem, string reflectionName)
    {
        var parts = reflectionName.Split('+');
        TreeViewItem? current = null;
        assemblyItem.IsExpanded = true;
        foreach (var nsItem in assemblyItem.Items.OfType<TreeViewItem>())
        {
            current = nsItem.Items.OfType<TreeViewItem>().FirstOrDefault(i => FullName(i) == parts[0]);
            if (current is null) continue;
            nsItem.IsExpanded = true;
            break;
        }

        var name = parts[0];
        for (var i = 1; current is not null && i < parts.Length; i++)
        {
            await ExpandAsync(current);
            current.IsExpanded = true;
            name += "+" + parts[i];
            current = current.Items.OfType<TreeViewItem>().FirstOrDefault(c => FullName(c) == name);
        }
        return current;

        static string? FullName(TreeViewItem item) => (item.Header as AssemblyTreeNode)?.Type?.FullTypeName;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    // Selects a row without decompiling it again (Back/Forward restores, F12 reveals) — and expands
    // its ancestors and scrolls to it, so the tree shows where the code pane is.
    private void SelectSilently(TreeViewItem? item)
    {
        for (var parent = item?.Parent as TreeViewItem; parent is not null; parent = parent.Parent as TreeViewItem)
            parent.IsExpanded = true;

        _suppressSelection = true;
        try
        {
            TypeTree.SelectedItem = item;
        }
        finally
        {
            _suppressSelection = false;
        }
        item?.BringIntoView();
    }

    // ── Back/Forward (Alt+←/→, mouse buttons 4/5, toolbar) ──

    internal bool CanGoBack => _activeTab.Back.Count > 0;
    internal bool CanGoForward => _activeTab.Forward.Count > 0;

    internal void GoBack()
    {
        if (_activeTab.Current is null || !_activeTab.Back.TryPop(out var entry)) return;
        _activeTab.Forward.Push(Remember(_activeTab.Current));
        Restore(entry);
    }

    internal void GoForward()
    {
        if (_activeTab.Current is null || !_activeTab.Forward.TryPop(out var entry)) return;
        _activeTab.Back.Push(Remember(_activeTab.Current));
        Restore(entry);
    }

    private HistoryEntry Remember(HistoryEntry entry) => entry with { CaretLine = Editor.TextArea.Caret.Line };

    private void Restore(HistoryEntry entry)
    {
        ++_requestVersion; // a decompile still running for the page we're leaving must not land
        _activeTab.Current = entry;
        ApplyResult(entry.Result, entry.CaretLine);
        SelectSilently(entry.Item);
        UpdateHistoryButtons();
        _ = RefreshCurrentAsync(); // rendered in another language/version than the current one
    }

    // A new page: the current one goes onto the Back stack, Forward is dropped (browser semantics).
    // `target` null: a page that can't be re-rendered (an F12 result from the user's script, an error).
    // `view`: the one `result` was rendered in, when it may no longer be the current one.
    private void Show(DecompiledMemberResult result, TreeViewItem? item, PageTarget? target, DecompilerView? view = null)
    {
        if (_activeTab.Current is not null)
        {
            _activeTab.Back.Push(Remember(_activeTab.Current));
            _activeTab.Forward.Clear();
        }
        _activeTab.Current = new HistoryEntry(result, item, result.Line, target, view ?? _view);
        ApplyResult(result, result.Line);
        UpdateHistoryButtons();
    }

    // every tab's pages keep their code but stop pointing at tree rows (some may have been removed)
    private void DetachHistoryFromTree()
    {
        foreach (var tab in _tabs)
        {
            Detach(tab.Back);
            Detach(tab.Forward);
            tab.Current = tab.Current is null ? null : tab.Current with { Item = null };
        }

        static void Detach(Stack<HistoryEntry> stack)
        {
            var entries = stack.Reverse().Select(e => e with { Item = null }).ToList();
            stack.Clear();
            foreach (var entry in entries) stack.Push(entry);
        }
    }

    private void UpdateHistoryButtons()
    {
        BackButton.IsEnabled = CanGoBack;
        ForwardButton.IsEnabled = CanGoForward;
        SaveCodeButton.IsEnabled = _activeTab.Current is not null;
        RefreshTabStrip();
    }

    // ── Tabs: each its own page and Back/Forward history (ILSpy's document tabs) ──

    private readonly List<DocumentTab> _tabs = [new DocumentTab()];
    private DocumentTab _activeTab = null!; // _tabs[0], set by every constructor
    private bool _syncingTabStrip;

    internal int TabCount => _tabs.Count;
    internal int ActiveTabIndex => _tabs.IndexOf(_activeTab);
    internal IReadOnlyList<string> TabTitles => _tabs.Select(t => t.Title).ToList();

    // A tree row opened in a new tab (context menu, middle-click or Ctrl+click on the row).
    internal async Task OpenInNewTabAsync(AssemblyTreeNode node, TreeViewItem? item)
    {
        var tab = new DocumentTab();
        _tabs.Insert(ActiveTabIndex + 1, tab);
        ActivateTab(tab);
        SelectSilently(item);
        await SelectNodeAsync(node, item);
    }

    internal void ActivateTab(DocumentTab tab)
    {
        if (!_tabs.Contains(tab)) return;
        if (_activeTab.Current is { } leaving && _tabs.Contains(_activeTab))
            _activeTab.Current = Remember(leaving); // come back to the same line
        _activeTab = tab;
        ++_requestVersion; // a decompile still running for the tab we're leaving must not land here

        if (tab.Current is { } page)
        {
            ApplyResult(page.Result, page.CaretLine);
            SelectSilently(page.Item);
            _ = RefreshCurrentAsync(); // rendered in another language/version than the current one
        }
        else
        {
            ApplyResult(new DecompiledMemberResult("", 1, "Assembly Explorer"), 1);
            SelectSilently(null);
        }
        UpdateHistoryButtons();
    }

    // Closing the last tab empties it instead (there's always one).
    internal void CloseTab(DocumentTab tab)
    {
        var index = _tabs.IndexOf(tab);
        if (index < 0) return;
        if (_tabs.Count == 1)
        {
            tab.Current = null;
            tab.Back.Clear();
            tab.Forward.Clear();
            ActivateTab(tab);
            return;
        }

        _tabs.RemoveAt(index);
        if (tab == _activeTab)
            ActivateTab(_tabs[Math.Min(index, _tabs.Count - 1)]);
        else
            RefreshTabStrip();
    }

    internal void CloseActiveTab() => CloseTab(_activeTab);

    internal void ActivateNextTab(int step) =>
        ActivateTab(_tabs[((ActiveTabIndex + step) % _tabs.Count + _tabs.Count) % _tabs.Count]);

    private void RefreshTabStrip()
    {
        _syncingTabStrip = true;
        try
        {
            DocumentTabs.ItemsSource = _tabs.Select(t => new TabHeader(t.Title, t)).ToList();
            DocumentTabs.SelectedIndex = ActiveTabIndex;
        }
        finally
        {
            _syncingTabStrip = false;
        }
    }

    internal sealed class DocumentTab
    {
        public HistoryEntry? Current { get; set; }
        public Stack<HistoryEntry> Back { get; } = new();
        public Stack<HistoryEntry> Forward { get; } = new();
        public string Title => Current?.Result.Title ?? "New Tab";
    }

    /// <summary>A tab strip item: what the tab shows, and the tab its close button closes.</summary>
    public sealed record TabHeader(string Title, object Tab);

    // ── Save Code (toolbar / Ctrl+S): the shown page, as .cs or .il ──

    internal bool CanSaveCode => _activeTab.Current is not null;

    // Writes exactly what the code pane shows, as C# or IL per the view it was rendered in. A failed
    // write shows an error; a cancelled dialog does nothing. The next save starts in the same folder.
    internal async Task SaveCodeAsync()
    {
        if (_activeTab.Current is not { } page) return;

        var extension = page.View.Language == DecompilerLanguage.IL ? "il" : "cs";
        var path = await _windowService.ShowSaveCodeFileDialogAsync(
            SuggestedFileName(page.Result.Title), extension, _lastSaveFolder ?? _startFolder);
        if (path is null) return;

        try
        {
            await File.WriteAllTextAsync(path, page.Result.Text);
            _lastSaveFolder = Path.GetDirectoryName(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _windowService.ShowErrorAsync("Save Code Failed", $"Could not write '{path}':\n{ex.Message}");
        }
    }

    // A file name from a page title: "Run() : void" → "Run", "Dictionary<TKey, TValue>" → "Dictionary",
    // "Foo (1.0.0.0)" → "Foo"; characters a file name can't hold become '_'.
    internal static string SuggestedFileName(string title)
    {
        var name = title.Trim();
        foreach (var stop in new[] { " : ", "(", "<", " " })
        {
            var index = name.IndexOf(stop, StringComparison.Ordinal);
            if (index > 0) name = name[..index];
        }
        var invalid = Path.GetInvalidFileNameChars();
        name = string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));
        return name.Length == 0 ? "Decompiled" : name;
    }

    private void ApplyResult(DecompiledMemberResult result, int caretLine)
    {
        Title = result.Title;
        UpdateReferenceHover(null); // its offsets belong to the text being replaced
        _currentTree = result.SyntaxTree;

        Editor.Text = result.Text;
        Editor.TextArea.Caret.Line = caretLine;
        Editor.TextArea.Caret.Column = 1;
        Editor.ScrollToLine(caretLine);
    }

    internal sealed record HistoryEntry(
        DecompiledMemberResult Result, TreeViewItem? Item, int CaretLine, PageTarget? Target, DecompilerView View);
}
