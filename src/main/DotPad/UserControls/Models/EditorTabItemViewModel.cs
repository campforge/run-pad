using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Controls;
using DotPad.Contracts.Models;
using DotPad.Format.RPad;
using DotPad.Services.Preferences;
using ReactiveUI;

namespace DotPad.UserControls.Models;

public enum RunState { Idle, Run, RunDebug, Paused }

public sealed class EditorTabItemViewModel : ReactiveObject, IDisposable
{
    private readonly IPreferencesService _prefs;

    public string? FilePath { get; }
    public RpadFrontMatter? FrontMatter { get; internal set; }

    public string FileName => FilePath is null ? "Untitled" : Path.GetFileNameWithoutExtension(FilePath);

    public ReactiveCommand<Unit, Unit> CloseCommand { get; }

    public List<ExecutionPlatform> DotnetPlatforms { get; }

    public List<string> Kinds  { get; } = new() { "Script", "Program", "Library" };

    private string? _selectedKind;
    public string? SelectedKind
    {
        get => _selectedKind;
        set => this.RaiseAndSetIfChanged(ref _selectedKind, value);
    }

    private ExecutionPlatform? _selectedDotnetPlatform;
    public ExecutionPlatform? SelectedDotnetPlatform
    {
        get => _selectedDotnetPlatform;
        set => this.RaiseAndSetIfChanged(ref _selectedDotnetPlatform, value);
    }

    public bool DebugModeEnabled
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = true;

    public bool BreakOnAllExceptions
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public bool BreakOnUncaughtExceptions
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = true;

    // DAP "justMyCode" — when true, the debugger steps over framework/library code with no
    // symbols instead of stepping into it. Off by default to match this project's prior hardcoded
    // behavior (ANALYSIS.md feature #21).
    public bool JustMyCode
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

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

    // ── Debug collections ─────────────────────────────────────────────────────

    public ObservableCollection<DebugThread> DebugThreads { get; } = [];
    public ObservableCollection<DebugLocal> DebugLocals { get; } = [];
    public ObservableCollection<DebugFrame> DebugCallStack { get; } = [];

    // ── Watch expressions ─────────────────────────────────────────────────────

    public ObservableCollection<WatchExpression> WatchExpressions { get; } = [];

    private WatchExpression? _selectedWatchExpression;
    public WatchExpression? SelectedWatchExpression
    {
        get => _selectedWatchExpression;
        set => this.RaiseAndSetIfChanged(ref _selectedWatchExpression, value);
    }

    public ReactiveCommand<Unit, Unit> AddWatchCommand { get; }
    public ReactiveCommand<Unit, Unit> RemoveWatchCommand { get; }

    // ── Output/debug panel layout (collapsible) ───────────────────────────────

    private double _lastExpandedOutputPanelHeight;

    public GridLength OutputPanelHeight
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public ReactiveCommand<Unit, Unit> ToggleOutputPanelCommand { get; }

    private void ToggleOutputPanel()
    {
        if (OutputPanelHeight.Value > 0)
        {
            _lastExpandedOutputPanelHeight = OutputPanelHeight.Value;
            OutputPanelHeight = new GridLength(0);
        }
        else
        {
            OutputPanelHeight = new GridLength(_lastExpandedOutputPanelHeight > 0 ? _lastExpandedOutputPanelHeight : 220);
        }
    }

    // ── Constructor ───────────────────────────────────────────────────────────

    public EditorTabItemViewModel(string? filePath, List<ExecutionPlatform> platforms, IPreferencesService prefs)
    {
        FilePath = filePath;
        DotnetPlatforms = platforms;
        _selectedDotnetPlatform = platforms.FirstOrDefault();
        _prefs = prefs;

        CloseCommand = ReactiveCommand.Create(() => { });

        AddWatchCommand = ReactiveCommand.Create(
            () => WatchExpressions.Add(new WatchExpression()));

        RemoveWatchCommand = ReactiveCommand.Create(
            () => { if (SelectedWatchExpression is { } w) WatchExpressions.Remove(w); },
            this.WhenAnyValue(x => x.SelectedWatchExpression).Select(w => w is not null));

        OutputPanelHeight = _prefs.Layout.DebugPanelCollapsed
            ? new GridLength(0)
            : new GridLength(_prefs.Layout.DebugPanelHeight);
        _lastExpandedOutputPanelHeight = _prefs.Layout.DebugPanelHeight;
        ToggleOutputPanelCommand = ReactiveCommand.Create(ToggleOutputPanel);
        this.WhenAnyValue(x => x.OutputPanelHeight)
            .Skip(1)
            .Throttle(TimeSpan.FromMilliseconds(400), RxApp.MainThreadScheduler)
            .Subscribe(h =>
            {
                _prefs.Layout.DebugPanelCollapsed = h.Value <= 0;
                if (h.Value > 0) _prefs.Layout.DebugPanelHeight = h.Value;
                _prefs.Save();
            });

        SyncTooltips();
        _prefs.Changed += OnPreferencesChanged;
    }

    private void OnPreferencesChanged() => SyncTooltips();

    private void SyncTooltips()
    {
        var kb = _prefs.Keyboard;
        TooltipRun = Label("Run", kb.Run);
        TooltipContinue = Label("Continue", kb.Run);
        TooltipCancel = Label("Cancel", kb.Cancel);
        TooltipStepOver = Label("Step Over", kb.StepOver);
        TooltipStepIn = Label("Step In", kb.StepIn);
        TooltipStepOut = Label("Step Out", kb.StepOut);
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
