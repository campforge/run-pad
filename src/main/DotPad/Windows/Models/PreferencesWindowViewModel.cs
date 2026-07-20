using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using DotPad.Avalonia.Themes;
using DotPad.Contracts;
using DotPad.Services;
using DotPad.Services.Preferences;
using DotPad.Shared;
using ReactiveUI;

namespace DotPad.Windows.Models;

public class PreferencesWindowViewModel : ViewModelBase, IDisposable
{
    // ── Settings tree (Rider/VS-style navigator — see PreferencesWindow.axaml) ─

    // Id prefix for a language's own node under Languages (e.g. "languages.csharp") — shared by the
    // tree-building code below and IsLanguageNodeSelected, so a language's node id always round-trips
    // through the same prefix rather than two independently-typed literals drifting apart.
    private const string LanguageNodeIdPrefix = "languages.";

    // Deliberately NOT prefixed with "languages." (unlike the language nodes themselves) — that
    // prefix is what IsLanguageNodeSelected uses to distinguish "a language's own node" from
    // everything else under Languages; giving this node that same prefix would make selecting it
    // incorrectly satisfy that check too and show the wrong placeholder instead of the real
    // SDK/debugger fields.
    private const string DotnetNodeId = "dotnet";

    private static readonly SettingsNode InterfaceNode = new("interface", "Interface");
    private static readonly SettingsNode EditorNode = new("editor", "Editor");
    private static readonly SettingsNode KeyboardNode = new("keyboard", "Keyboard");

    private readonly IThemeService _themeService;
    private readonly IPreferencesService _prefs;

    // Built once from the registry in the constructor, not a static — unlike the language-agnostic
    // nodes above, this genuinely depends on which languages are registered (TODO.md #49 item 4: used
    // to be two hardcoded SettingsNodes, "languages.csharp"/"languages.vb", so a third registered
    // language would never show up in this tree without editing this file directly).
    private readonly SettingsNode _languagesNode;
    private readonly IReadOnlyList<SettingsNode> _fullTree;

    private InterfaceTheme? _selectedInterfaceTheme;
    private EditorTheme? _selectedEditorTheme;
    private string _filterText = "";
    private SettingsNode? _selectedNode;

    public ObservableCollection<InterfaceTheme> AvailableInterfaceThemes { get; } = [];
    public ObservableCollection<EditorTheme> AvailableEditorThemes { get; } = [];

    public ObservableCollection<SettingsNode> Nodes { get; } = [];

    public PreferencesWindowViewModel(
        IThemeService themeService, IPreferencesService prefs, ILanguageHostRegistry languageRegistry)
    {
        _themeService = themeService;
        _prefs = prefs;

        // Every registered language today is .NET-based (C#, VB — see TODO.md's "Реорганизация
        // Preferences" note), so they all nest under one ".NET" runtime-family group carrying the
        // shared SDK/debugger path fields (still backed by the same DebuggerSettings/AppSettings.
        // Debugger — this is a pure tree-shape change, no settings.json migration). A future non-
        // .NET language would need a real RuntimeFamily concept on ILanguageHostFactory to pick its
        // own group instead of hardcoding ".NET" here — deliberately not built until there's a
        // second runtime family to actually distinguish (same reasoning as TODO #87's deferral).
        var languageNodes = languageRegistry.SupportedLanguages
            .Select(lang => new SettingsNode(LanguageNodeIdPrefix + lang, languageRegistry.GetDisplayName(lang)))
            .ToList();
        var dotnetNode = new SettingsNode(DotnetNodeId, ".NET", languageNodes);
        _languagesNode = new SettingsNode("languages", "Languages", [dotnetNode]);
        _fullTree = [InterfaceNode, EditorNode, KeyboardNode, _languagesNode];

        // Initialize directly — no bindings exist yet, so no RaisePropertyChanged needed
        var theme = themeService.CurrentTheme;
        _selectedInterfaceTheme = theme.Interface;
        _selectedEditorTheme = theme.Editor;
        foreach (var t in themeService.InterfaceThemes.Where(t => t.IsDark == theme.Interface.IsDark))
            AvailableInterfaceThemes.Add(t);
        foreach (var t in themeService.EditorThemes.Where(t => t.IsDark == theme.Editor.IsDark))
            AvailableEditorThemes.Add(t);

        themeService.ThemeChanged += OnThemeChanged;

        RebuildNodes();
        _selectedNode = InterfaceNode;
    }

    // ── Settings tree: search/filter + selection ───────────────────────────────

    public string FilterText
    {
        get => _filterText;
        set
        {
            if (_filterText == value) return;
            this.RaiseAndSetIfChanged(ref _filterText, value);
            RebuildNodes();
        }
    }

    public SettingsNode? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (ReferenceEquals(_selectedNode, value)) return;
            this.RaiseAndSetIfChanged(ref _selectedNode, value);
            this.RaisePropertyChanged(nameof(IsInterfaceSelected));
            this.RaisePropertyChanged(nameof(IsEditorSelected));
            this.RaisePropertyChanged(nameof(IsDotnetSelected));
            this.RaisePropertyChanged(nameof(IsKeyboardSelected));
            this.RaisePropertyChanged(nameof(IsLanguagesSelected));
            this.RaisePropertyChanged(nameof(IsLanguageNodeSelected));
            this.RaisePropertyChanged(nameof(IsCSharpNodeSelected));
            this.RaisePropertyChanged(nameof(IsGenericLanguagePlaceholderVisible));
            this.RaisePropertyChanged(nameof(SelectedLanguageSettingsPlaceholder));
        }
    }

    public bool IsInterfaceSelected => _selectedNode?.Id == "interface";
    public bool IsEditorSelected => _selectedNode?.Id == "editor";
    public bool IsDotnetSelected => _selectedNode?.Id == DotnetNodeId;
    public bool IsKeyboardSelected => _selectedNode?.Id == "keyboard";
    public bool IsLanguagesSelected => _selectedNode?.Id == "languages";

    // True for one of Languages' own child nodes (e.g. "languages.csharp") — not the "Languages"
    // parent node itself, which has its own separate placeholder (IsLanguagesSelected). Replaces the
    // old IsLanguagesCSharpSelected/IsLanguagesVisualBasicSelected pair (TODO.md #49 item 4) — a third
    // registered language needs no new property and no new XAML panel, just an entry in the registry.
    public bool IsLanguageNodeSelected =>
        _selectedNode is not null && _selectedNode.Id != "languages"
            && _selectedNode.Id.StartsWith(LanguageNodeIdPrefix, StringComparison.Ordinal);

    // C#'s own node (TODO.md #62 "inlay hints", user request 2026-07-20) — the first language to
    // actually need a real settings panel instead of the shared placeholder below. A bespoke property
    // alongside the generic IsLanguageNodeSelected check, same dual-track shape IsDotnetSelected
    // already uses (a hardcoded special case sitting next to the generic system, not a reversion of
    // it) — VB (or any future language with nothing to configure) still falls through to the shared
    // placeholder untouched.
    public bool IsCSharpNodeSelected => _selectedNode?.Id == LanguageNodeIdPrefix + "csharp";

    // A language node is selected AND it's not C# (which has its own real panel below) — the
    // placeholder panel's own IsVisible binding, since Avalonia XAML has no inline boolean AND.
    public bool IsGenericLanguagePlaceholderVisible => IsLanguageNodeSelected && !IsCSharpNodeSelected;

    // "No {Name}-specific settings yet." — the placeholder every OTHER language still shares (see
    // PreferencesWindow.axaml); null when no language node is selected, or when the selected one has
    // its own real panel instead (C# — see IsCSharpNodeSelected).
    public string? SelectedLanguageSettingsPlaceholder =>
        IsGenericLanguagePlaceholderVisible ? $"No {_selectedNode!.Name}-specific settings yet." : null;

    // Rebuilds the displayed (possibly filtered) tree from the fixed full tree. A node survives the
    // filter if its own name matches or any descendant's does, so e.g. typing "key" still surfaces
    // "Keyboard", and typing "c#" surfaces "Languages" (for its matching child) even though the
    // parent's own name doesn't match.
    private void RebuildNodes()
    {
        Nodes.Clear();
        foreach (var node in _fullTree)
        {
            var filtered = FilterNode(node);
            if (filtered is not null) Nodes.Add(filtered);
        }
    }

    private SettingsNode? FilterNode(SettingsNode node)
    {
        if (string.IsNullOrWhiteSpace(_filterText)) return node;

        var matchingChildren = node.Children
            .Select(FilterNode)
            .Where(c => c is not null)
            .Select(c => c!)
            .ToList();

        var selfMatches = node.Name.Contains(_filterText, StringComparison.OrdinalIgnoreCase);
        if (!selfMatches && matchingChildren.Count == 0) return null;

        // Comparing counts alone (the original check here) wrongly treated "every child slot still
        // has SOME node in it" as "nothing changed anywhere below" — true whenever a node has
        // exactly one child and that child survives filtering, REGARDLESS of whether filtering
        // rewrote something further down inside that child (e.g. Languages -> .NET -> C#/VB: with
        // only ".NET" as Languages' one child, ".NET" surviving with just C# left inside it still
        // "counted" as 1 == 1, so Languages kept pointing at the original, unfiltered ".NET" node —
        // discovered via FilterText_MatchingALanguageName_Should_StillSurfaceTheParentLanguagesAnd
        // DotnetNodes once nesting one language group under another made a 1-child case reachable).
        // Reference-comparing the actual child nodes catches this at any depth.
        return matchingChildren.SequenceEqual(node.Children)
            ? node
            : new SettingsNode(node.Id, node.Name, matchingChildren);
    }

    public void Dispose()
    {
        _themeService.ThemeChanged -= OnThemeChanged;
        GC.SuppressFinalize(this);
    }

    private void OnThemeChanged(ActiveTheme theme) => RefreshThemeLists(theme);

    private void RefreshThemeLists(ActiveTheme theme)
    {
        // Only rebuild lists when dark/light polarity flips — avoids ComboBox flicker on
        // simple theme-within-same-mode changes
        if (AvailableInterfaceThemes.Count == 0 ||
            AvailableInterfaceThemes[0].IsDark != theme.Interface.IsDark)
        {
            AvailableInterfaceThemes.Clear();
            foreach (var t in _themeService.InterfaceThemes.Where(t => t.IsDark == theme.Interface.IsDark))
                AvailableInterfaceThemes.Add(t);
        }

        if (AvailableEditorThemes.Count == 0 ||
            AvailableEditorThemes[0].IsDark != theme.Editor.IsDark)
        {
            AvailableEditorThemes.Clear();
            foreach (var t in _themeService.EditorThemes.Where(t => t.IsDark == theme.Editor.IsDark))
                AvailableEditorThemes.Add(t);
        }

        _selectedInterfaceTheme = theme.Interface;
        this.RaisePropertyChanged(nameof(SelectedInterfaceTheme));
        _selectedEditorTheme = theme.Editor;
        this.RaisePropertyChanged(nameof(SelectedEditorTheme));
    }

    // ── Appearance mode ───────────────────────────────────────────────────────

    public ThemeMode SelectedMode
    {
        get => _prefs.Appearance.Mode;
        set
        {
            if (_prefs.Appearance.Mode == value) return;
            _themeService.SetMode(value);
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(IsSystemMode));
            this.RaisePropertyChanged(nameof(IsDarkMode));
            this.RaisePropertyChanged(nameof(IsLightMode));
        }
    }

    public bool IsSystemMode { get => SelectedMode == ThemeMode.System; set { if (value) SelectedMode = ThemeMode.System; } }
    public bool IsDarkMode { get => SelectedMode == ThemeMode.Dark; set { if (value) SelectedMode = ThemeMode.Dark; } }
    public bool IsLightMode { get => SelectedMode == ThemeMode.Light; set { if (value) SelectedMode = ThemeMode.Light; } }

    // ── Interface theme ───────────────────────────────────────────────────────

    public InterfaceTheme? SelectedInterfaceTheme
    {
        get => _selectedInterfaceTheme;
        set
        {
            // Guard prevents re-triggering SetInterfaceTheme when ComboBox writes back
            // the same value after RefreshThemeLists updates the backing field
            if (_selectedInterfaceTheme == value) return;
            this.RaiseAndSetIfChanged(ref _selectedInterfaceTheme, value);
            if (value is not null) _themeService.SetInterfaceTheme(value);
        }
    }

    // ── Editor theme ──────────────────────────────────────────────────────────

    public EditorTheme? SelectedEditorTheme
    {
        get => _selectedEditorTheme;
        set
        {
            if (_selectedEditorTheme == value) return;
            this.RaiseAndSetIfChanged(ref _selectedEditorTheme, value);
            if (value is not null) _themeService.SetEditorTheme(value);
        }
    }

    // ── Editor settings ───────────────────────────────────────────────────────

    public bool ShowLineNumbers
    {
        get => _prefs.Editor.ShowLineNumbers;
        set => SetPref(_prefs.Editor.ShowLineNumbers, value, v => _prefs.Editor.ShowLineNumbers = v);
    }

    public bool ShowFolding
    {
        get => _prefs.Editor.ShowFolding;
        set => SetPref(_prefs.Editor.ShowFolding, value, v => _prefs.Editor.ShowFolding = v);
    }

    public bool ShowTabs
    {
        get => _prefs.Editor.ShowTabs;
        set => SetPref(_prefs.Editor.ShowTabs, value, v => _prefs.Editor.ShowTabs = v);
    }

    public bool ShowSpaces
    {
        get => _prefs.Editor.ShowSpaces;
        set => SetPref(_prefs.Editor.ShowSpaces, value, v => _prefs.Editor.ShowSpaces = v);
    }

    public bool ShowEndOfLine
    {
        get => _prefs.Editor.ShowEndOfLine;
        set => SetPref(_prefs.Editor.ShowEndOfLine, value, v => _prefs.Editor.ShowEndOfLine = v);
    }

    public bool ShowColumnRuler
    {
        get => _prefs.Editor.ShowColumnRuler;
        set => SetPref(_prefs.Editor.ShowColumnRuler, value, v => _prefs.Editor.ShowColumnRuler = v);
    }

    public bool WordWrap
    {
        get => _prefs.Editor.WordWrap;
        set => SetPref(_prefs.Editor.WordWrap, value, v => _prefs.Editor.WordWrap = v);
    }

    public bool CommitCharactersEnabled
    {
        get => _prefs.Editor.CommitCharactersEnabled;
        set => SetPref(_prefs.Editor.CommitCharactersEnabled, value, v => _prefs.Editor.CommitCharactersEnabled = v);
    }

    public double FontSize
    {
        get => _prefs.Editor.FontSize;
        set => SetPref(_prefs.Editor.FontSize, value, v => _prefs.Editor.FontSize = v);
    }

    // ── C# (TODO.md #62 "inlay hints", user request 2026-07-20) ────────────────

    public bool InlayHintsEnabled
    {
        get => _prefs.CSharp.InlayHintsEnabled;
        set => SetPref(_prefs.CSharp.InlayHintsEnabled, value, v => _prefs.CSharp.InlayHintsEnabled = v);
    }

    // ── Debugger ──────────────────────────────────────────────────────────────

    public string DotnetPath
    {
        get => _prefs.Debugger.DotnetPath;
        set => SetPref(_prefs.Debugger.DotnetPath, value, v => _prefs.Debugger.DotnetPath = v);
    }

    public string DebuggerPath
    {
        get => _prefs.Debugger.DebuggerPath;
        set => SetPref(_prefs.Debugger.DebuggerPath, value, v => _prefs.Debugger.DebuggerPath = v);
    }

    // ── Keyboard shortcuts ────────────────────────────────────────────────────

    public string HotkeyRun
    {
        get => _prefs.Keyboard.Run;
        set => SetPref(_prefs.Keyboard.Run, value, v => _prefs.Keyboard.Run = v);
    }

    public string HotkeyCancel
    {
        get => _prefs.Keyboard.Cancel;
        set => SetPref(_prefs.Keyboard.Cancel, value, v => _prefs.Keyboard.Cancel = v);
    }

    public string HotkeyStepOver
    {
        get => _prefs.Keyboard.StepOver;
        set => SetPref(_prefs.Keyboard.StepOver, value, v => _prefs.Keyboard.StepOver = v);
    }

    public string HotkeyStepIn
    {
        get => _prefs.Keyboard.StepIn;
        set => SetPref(_prefs.Keyboard.StepIn, value, v => _prefs.Keyboard.StepIn = v);
    }

    public string HotkeyStepOut
    {
        get => _prefs.Keyboard.StepOut;
        set => SetPref(_prefs.Keyboard.StepOut, value, v => _prefs.Keyboard.StepOut = v);
    }

    public string HotkeyFormat
    {
        get => _prefs.Keyboard.Format;
        set => SetPref(_prefs.Keyboard.Format, value, v => _prefs.Keyboard.Format = v);
    }

    public string HotkeyGoToLine
    {
        get => _prefs.Keyboard.GoToLine;
        set => SetPref(_prefs.Keyboard.GoToLine, value, v => _prefs.Keyboard.GoToLine = v);
    }

    // TODO.md #80: these 4 used to be literal Key.XXX checks in MainWindow.axaml.cs/FileTreeView.cs,
    // not editable here at all — same pattern as the 7 above, just newly wired up.
    public string HotkeyOpenDependencies
    {
        get => _prefs.Keyboard.OpenDependencies;
        set => SetPref(_prefs.Keyboard.OpenDependencies, value, v => _prefs.Keyboard.OpenDependencies = v);
    }

    public string HotkeyCloseTab
    {
        get => _prefs.Keyboard.CloseTab;
        set => SetPref(_prefs.Keyboard.CloseTab, value, v => _prefs.Keyboard.CloseTab = v);
    }

    public string HotkeyRenameFile
    {
        get => _prefs.Keyboard.RenameFile;
        set => SetPref(_prefs.Keyboard.RenameFile, value, v => _prefs.Keyboard.RenameFile = v);
    }

    public string HotkeyDeleteFile
    {
        get => _prefs.Keyboard.DeleteFile;
        set => SetPref(_prefs.Keyboard.DeleteFile, value, v => _prefs.Keyboard.DeleteFile = v);
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    private void SetPref<T>(T current, T value, Action<T> assign, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return;
        assign(value);
        _prefs.Save();
        this.RaisePropertyChanged(name);
    }
}
