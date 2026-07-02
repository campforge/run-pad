using System.Collections.ObjectModel;
using System.Reactive;
using DotPad.Build.Core.Models;
using DotPad.Shared;
using ReactiveUI;

namespace DotPad.UserControls.Models;

public enum RunState { Idle, Run, RunDebug, Paused }

public sealed class EditorTabItemViewModel : ControllModelBase
{
    public string? FilePath { get; }
    public string FileName => FilePath is null ? "Untitled" : Path.GetFileName(FilePath);

    public ReactiveCommand<Unit, Unit> CloseCommand { get; }

    public List<ExecutionPlatform> DotnetPlatforms { get; }

    private ExecutionPlatform? _selectedDotnetPlatform;
    public ExecutionPlatform? SelectedDotnetPlatform
    {
        get => _selectedDotnetPlatform;
        set => this.RaiseAndSetIfChanged(ref _selectedDotnetPlatform, value);
    }

    private bool _debugModeEnabled = true;
    public bool DebugModeEnabled
    {
        get => _debugModeEnabled;
        set => this.RaiseAndSetIfChanged(ref _debugModeEnabled, value);
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
        }
    }

    public bool EnableRunButton => _runState == RunState.Idle;
    public bool EnableCancelButton => _runState != RunState.Idle;
    public bool IsDebugging => _runState is RunState.RunDebug or RunState.Paused;
    public bool IsPaused => _runState == RunState.Paused;
    public bool IsRunningProgress => _runState is RunState.Run or RunState.RunDebug;

    public ObservableCollection<DebugThread> DebugThreads { get; } = [];
    public ObservableCollection<DebugLocal> DebugLocals { get; } = [];
    public ObservableCollection<DebugFrame> DebugCallStack { get; } = [];
    public ObservableCollection<WatchExpression> WatchExpressions { get; } = [];

    public EditorTabItemViewModel(string? filePath, List<ExecutionPlatform> platforms)
    {
        FilePath = filePath;
        DotnetPlatforms = platforms;
        _selectedDotnetPlatform = platforms.FirstOrDefault();
        CloseCommand = ReactiveCommand.Create(() => { });
    }
}
