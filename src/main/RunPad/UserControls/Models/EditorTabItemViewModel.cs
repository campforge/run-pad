using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Controls;
using ReactiveUI;
using RunPad.Contracts.Models;
using RunPad.Format.RPad;
using RunPad.Services.Preferences;
using RunPad.Shared;

namespace RunPad.UserControls.Models;

public enum RunState { Idle, Run, RunDebug, Paused }

public sealed class EditorTabItemViewModel : ReactiveObject, IDisposable
{
    private readonly IPreferencesService _prefs;
    private readonly IEditorTabActions _actions;

    // Set once by EditorsTabItemControl's constructor — the View-layer serializer for this tab's
    // live editor content, which only exists once the View itself does (EditorTabItemViewModel is
    // constructed by MainWindowViewModel BEFORE its control). IEditorTabActions' Save/Duplicate
    // implementations read this instead of taking it as a parameter, so this ViewModel's commands
    // can delegate straight to _actions without waiting for the View to hand a callback down first.
    internal Action<string>? WriteToFile { get; set; }

    // Settable (not just get-only) so a successful Save As (MainWindowViewModel.SaveTabAsAsync) can
    // give a previously untitled tab a real path in place, instead of needing to close/reopen a new
    // EditorTabItemViewModel — FileName/HasFilePath both derive from this, so both need to react too.
    public string? FilePath
    {
        get;
        internal set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(FileName));
            this.RaisePropertyChanged(nameof(HasFilePath));
        }
    }

    // Whether this tab has ever been saved anywhere — used to enable/disable tab context-menu items
    // (Duplicate/Open Containing Folder) that need a real file on disk to act on.
    public bool HasFilePath => FilePath is not null;

    // Front matter this tab should be created with the first time it's actually written to disk
    // (Save/Save As) — only meaningful while FilePath is null (a brand-new, never-saved tab; see
    // MainWindowViewModel.NewRpadFile and EditorsTabItemControl.SetDocumentContent's null-FilePath
    // branch). Once saved, the file's own front matter (State.RpadDocument) is the source of truth
    // and these are no longer consulted.
    public RpadLanguage NewFileLanguage { get; }
    public RpadKind NewFileKind { get; }

    // Whether the editor's text has changed since this tab was last written to disk — starts false
    // (a freshly opened/created tab has nothing unsaved yet) and flips true on the first real edit;
    // see EditorsTabItemControl's constructor for why the TextChanged subscription that sets this is
    // attached only after the initial SetDocumentContent call, not before. Cleared back to false by
    // every successful write (SaveIfNeeded, the pre-run save in RunAsync, Save As).
    public bool IsDirty
    {
        get;
        internal set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public ReactiveCommand<Unit, Unit> SaveAsCommand { get; }
    public ReactiveCommand<Unit, Unit> DuplicateCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenContainingFolderCommand { get; }

    // Reactive (rather than a plain auto-property) because tab creation loads the Roslyn
    // workspace/BCL metadata off the UI thread (see EditorTabsControl.CreateControlAsync) — the tab
    // becomes ActiveTab and is clickable before this is set. Without change notification, opening the
    // Dependencies window (WindowService.ShowDependenciesAsync) during that window would take a
    // one-time null snapshot and the "+" buttons would stay disabled for the rest of that dialog's
    // lifetime even after the front matter became available.
    public RpadFrontMatter? FrontMatter
    {
        get;
        internal set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public string FileName => FilePath is null ? "Untitled" : Path.GetFileNameWithoutExtension(FilePath);

    public ReactiveCommand<Unit, Unit> CloseCommand { get; }

    public List<DotnetSdk> AvailableSdks { get; }

    public List<string> Kinds { get; } = new() { "Statements", "Program", "Expression" };

    private string? _selectedKind = "Statements";
    public string? SelectedKind
    {
        get => _selectedKind;
        set => this.RaiseAndSetIfChanged(ref _selectedKind, value);
    }

    private DotnetSdk? _selectedSdk;
    public DotnetSdk? SelectedSdk
    {
        get => _selectedSdk;
        set => this.RaiseAndSetIfChanged(ref _selectedSdk, value);
    }

    public bool DebugModeEnabled
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = true;

    private RunState _runState;
    public RunState RunState
    {
        get => _runState;
        internal set
        {
            this.RaiseAndSetIfChanged(ref _runState, value);
            this.RaisePropertyChanged(nameof(EnableRunButton));
            this.RaisePropertyChanged(nameof(EnableCancelButton));
            this.RaisePropertyChanged(nameof(IsDebugging));
            this.RaisePropertyChanged(nameof(IsPaused));
            this.RaisePropertyChanged(nameof(IsRunningProgress));
            this.RaisePropertyChanged(nameof(EnableRunOrContinueButton));
            this.RaisePropertyChanged(nameof(TooltipRunOrContinue));
        }
    }

    public bool EnableRunButton => _runState == RunState.Idle;
    public bool EnableCancelButton => _runState != RunState.Idle;
    public bool IsDebugging => _runState is RunState.RunDebug or RunState.Paused;
    public bool IsPaused => _runState == RunState.Paused;
    public bool IsRunningProgress => _runState is RunState.Run or RunState.RunDebug;

    // Run and Continue are the same button in the toolbar (EditorTabsControl.RunOrContinueActive
    // already implements this logic for the hotkey) — enabled when either action is currently valid.
    public bool EnableRunOrContinueButton => EnableRunButton || IsPaused;
    public string TooltipRunOrContinue => IsPaused ? TooltipContinue : TooltipRun;

    // ── Hotkey tooltips — owned and kept up-to-date by this ViewModel ─────────

    public string TooltipRun
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = "Run";

    public string TooltipContinue
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = "Continue";

    public string TooltipCancel
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = "Cancel";

    public string TooltipStepOver
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = "Step Over";

    public string TooltipStepIn
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = "Step In";

    public string TooltipStepOut
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = "Step Out";

    // ── Debug panel (Threads/Locals/Watch/Call Stack/Exceptions) ──────────────

    public DebugPanelViewModel DebugPanel { get; } = new();

    // ── Output/debug panel layout (collapsible) ───────────────────────────────

    // Was its own copy of the collapse/expand-plus-throttled-persist scheme,
    // duplicated near-verbatim in MainWindowViewModel.SidebarColumnWidth/ToggleSidebar —
    // extracted into CollapsiblePanelState (src/main/RunPad/Shared), constructed below.
    public CollapsiblePanelState OutputPanel { get; }

    // ── Constructor ───────────────────────────────────────────────────────────

    public EditorTabItemViewModel(
        string? filePath,
        List<DotnetSdk> sdks,
        IPreferencesService prefs,
        IEditorTabActions actions,
        RpadLanguage newFileLanguage = RpadLanguage.CSharp,
        RpadKind newFileKind = RpadKind.Statements)
    {
        FilePath = filePath;
        AvailableSdks = sdks;
        _selectedSdk = sdks.FirstOrDefault();
        _prefs = prefs;
        _actions = actions;
        NewFileLanguage = newFileLanguage;
        NewFileKind = newFileKind;

        CloseCommand = ReactiveCommand.CreateFromTask(() => _actions.CloseAsync(this));
        SaveAsCommand = ReactiveCommand.CreateFromTask(() => _actions.SaveAsAsync(this));
        DuplicateCommand = ReactiveCommand.Create(() => _actions.Duplicate(this));
        OpenContainingFolderCommand = ReactiveCommand.Create(() => _actions.OpenContainingFolder(this));

        OutputPanel = new CollapsiblePanelState(
            _prefs.Layout.DebugPanelCollapsed, _prefs.Layout.DebugPanelHeight, defaultExpandedSize: 220,
            onChanged: (collapsed, size) =>
            {
                _prefs.Layout.DebugPanelCollapsed = collapsed;
                if (size > 0) _prefs.Layout.DebugPanelHeight = size;
                _prefs.Save();
            });

        SyncTooltips();
        _prefs.Changed += OnPreferencesChanged;
    }

    private void OnPreferencesChanged() => SyncTooltips();

    private void SyncTooltips()
    {
        var kb = _prefs.Keyboard;
        TooltipRun = Label("Run", kb.KeyFor(KeyCommands.RunOrContinue));
        TooltipContinue = Label("Continue", kb.KeyFor(KeyCommands.RunOrContinue));
        TooltipCancel = Label("Stop", kb.KeyFor(KeyCommands.Stop));
        TooltipStepOver = Label("Step Over", kb.KeyFor(KeyCommands.StepOver));
        TooltipStepIn = Label("Step Into", kb.KeyFor(KeyCommands.StepInto));
        TooltipStepOut = Label("Step Out", kb.KeyFor(KeyCommands.StepOut));
        this.RaisePropertyChanged(nameof(TooltipRunOrContinue));

        static string Label(string name, string key)
            => string.IsNullOrWhiteSpace(key) ? name : $"{name} ({key})";
    }

    public void Dispose()
    {
        _prefs.Changed -= OnPreferencesChanged;
        GC.SuppressFinalize(this);
    }
}
