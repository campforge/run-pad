using System.Reactive;
using Invoklet.Contracts.Models;
using Invoklet.Format.RPad;
using Invoklet.Services;
using Invoklet.Services.Examples;
using Invoklet.Services.Preferences;
using Invoklet.Services.Recovery;
using Invoklet.Shared;
using ReactiveUI;

namespace Invoklet.UserControls.Models;

public enum RunState { Idle, Run, RunDebug, Paused }

// Where the tab shows its Markdown preview: not at all, beside the editor, or in its place.
public enum ScriptPreviewMode { Off, Side, Full }

public sealed class EditorTabItemViewModel : ReactiveObject, IDisposable
{
    private readonly IPreferencesService _prefs;
    private readonly IEditorTabActions _actions;

    // Set once by EditorsTabItemControl's constructor — the View-layer serializer for this tab's
    // live editor content, which only exists once the View itself does (EditorTabItemViewModel is
    // constructed by MainWindowViewModel BEFORE its control). IEditorTabActions' Save/Duplicate
    // implementations read this instead of taking it as a parameter, so this ViewModel's commands
    // can delegate straight to _actions without waiting for the View to hand a callback down first.
    internal Guid RecoveryId { get; init; } = Guid.NewGuid();
    internal RecoveryTab? RestoredRecovery { get; init; }

    /// <summary>The file this tab's references are relative to: its own, or, for an untitled copy of
    /// a file (a recovered draft whose file was already open), that file. Null when there is none.</summary>
    internal string? ReferenceAnchorPath => FilePath ?? RestoredRecovery?.OriginalPath;
    internal Func<RecoveryTab>? CaptureRecovery { get; set; }
    internal TaskCompletionSource RecoveryReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal bool RequiresBaselineConfirmation { get; set; }

    internal Action<string>? WriteToFile { get; set; }

    // What this tab's file held when the tab last read it or wrote it itself; null while unknown (a
    // tab whose content never came from disk). An in-place save compares the file with it first.
    internal ScriptFileState? DiskState { get; set; }

    // The last file WriteToFile wrote and what it wrote there. It becomes DiskState only when that
    // write was a save of this tab to its own file (in place or Save As), never for Duplicate.
    internal (string Path, ScriptFileState State)? LastWrite { get; set; }

    internal void AcceptWrittenState(string path)
    {
        if (LastWrite is { } written && written.Path == path)
        {
            DiskState = written.State;
            RequiresBaselineConfirmation = false;
            DeclinedDiskState = null;
        }
    }

    // A version of the file another program wrote that the user chose not to reload over their
    // unsaved edits: Invoklet doesn't ask about that same version again. Saving or reloading forgets it.
    internal ScriptFileState? DeclinedDiskState { get; set; }

    // An in-place save still waiting for the user's choice (see InPlaceSave).
    internal Task<InPlaceSaveResult>? PendingSave { get; set; }
    internal Task? PendingSaveAs { get; set; }

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
    internal string UntitledTitle { get; }

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
    // workspace/BCL metadata off the UI thread (see EditorTabsControl.CreateIntelliSenseAsync) — the tab
    // becomes ActiveTab and is clickable before this is set. Without change notification, opening the
    // Dependencies window (WindowService.ShowDependenciesAsync) during that window would take a
    // one-time null snapshot and the "+" buttons would stay disabled for the rest of that dialog's
    // lifetime even after the front matter became available.
    public RpadFrontMatter? FrontMatter
    {
        get;
        internal set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public string FileName => FilePath is null ? RestoredRecovery?.Title ?? Example?.Title ?? UntitledTitle : Path.GetFileNameWithoutExtension(FilePath);

    /// <summary>The bundled example this untitled tab was opened from: its document is the tab's
    /// starting text (a copy — the example itself never changes). Null for any other tab.</summary>
    public ExampleScript? Example { get; init; }

    public ReactiveCommand<Unit, Unit> CloseCommand { get; }

    public List<DotnetSdk> AvailableSdks { get; }

    // The toolbar's list: "latest (.NET …)" first — a script that follows the newest SDK — then each
    // installed SDK by version.
    public DotnetSdk? LatestSdk { get; }
    public IReadOnlyList<DotnetSdk> SdkChoices { get; }

    public List<string> Kinds { get; } = new() { "Statements", "Program", "Expression" };

    public IReadOnlyList<string> ArgumentSetChoices { get; private set; } = ["Defaults"];

    private string _selectedArgumentSet = "Defaults";
    public string SelectedArgumentSet
    {
        get => _selectedArgumentSet;
        internal set => this.RaiseAndSetIfChanged(ref _selectedArgumentSet, value);
    }

    internal void SetArgumentSetChoices(RpadFrontMatter? frontMatter)
    {
        IEnumerable<string> names = frontMatter?.ArgumentSets.Keys ?? Enumerable.Empty<string>();
        ArgumentSetChoices = ["Defaults", .. names];
        this.RaisePropertyChanged(nameof(ArgumentSetChoices));
        SelectedArgumentSet = frontMatter?.SelectedArgumentSet ?? "Defaults";
    }

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

    public EditorLayoutState Layout { get; }
    public ScriptPreviewMode PreviewMode
    {
        get => Layout.PreviewMode;
        set => Layout.PreviewMode = value;
    }
    private readonly IDisposable _layoutSubscription;

    // The toolbar's preview button: lit while any preview shows, and it opens the side-by-side one.
    public bool PreviewShown
    {
        get => PreviewMode != ScriptPreviewMode.Off;
        set => PreviewMode = value ? ScriptPreviewMode.Side : ScriptPreviewMode.Off;
    }

    // As in VS Code: the same key opens the preview and goes back to the editor; the other key
    // switches between the side-by-side preview and none.
    public void TogglePreview()
        => PreviewMode = PreviewMode == ScriptPreviewMode.Full ? ScriptPreviewMode.Off : ScriptPreviewMode.Full;

    public void TogglePreviewToSide()
        => PreviewMode = PreviewMode == ScriptPreviewMode.Side ? ScriptPreviewMode.Off : ScriptPreviewMode.Side;

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
    // extracted into CollapsiblePanelState (src/main/Invoklet/Shared), constructed below.
    public CollapsiblePanelState OutputPanel { get; }

    // ── Constructor ───────────────────────────────────────────────────────────

    public EditorTabItemViewModel(
        string? filePath,
        List<DotnetSdk> sdks,
        IPreferencesService prefs,
        IEditorTabActions actions,
        RpadLanguage newFileLanguage = RpadLanguage.CSharp,
        RpadKind newFileKind = RpadKind.Statements,
        string untitledTitle = "Untitled")
    {
        FilePath = filePath;
        AvailableSdks = sdks;
        LatestSdk = TargetSdkResolver.LatestChoice(sdks);
        SdkChoices = LatestSdk is null ? sdks : [LatestSdk, .. sdks];
        _selectedSdk = LatestSdk;
        _prefs = prefs;
        _actions = actions;
        NewFileLanguage = newFileLanguage;
        NewFileKind = newFileKind;
        UntitledTitle = untitledTitle;

        CloseCommand = ReactiveCommand.CreateFromTask(() => _actions.CloseAsync(this));
        SaveAsCommand = ReactiveCommand.CreateFromTask(() => _actions.SaveAsAsync(this));
        DuplicateCommand = ReactiveCommand.Create(() => _actions.Duplicate(this));
        OpenContainingFolderCommand = ReactiveCommand.Create(() => _actions.OpenContainingFolder(this));

        Layout = EditorLayoutState.For(prefs);
        OutputPanel = Layout.OutputPanel;
        DebugPanel.Layout = Layout;
        _layoutSubscription = Layout.WhenAnyValue(x => x.PreviewMode).Subscribe(_ =>
        {
            this.RaisePropertyChanged(nameof(PreviewMode));
            this.RaisePropertyChanged(nameof(PreviewShown));
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
        _layoutSubscription.Dispose();
        _prefs.Changed -= OnPreferencesChanged;
        GC.SuppressFinalize(this);
    }
}
