using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive;
using DotPad.Build.Core.Models;
using ReactiveUI;

namespace DotPad.ViewModels;

public sealed class EditorTabViewModel : ControllModelBase
{
    public string?  FilePath { get; }
    public string   FileName => FilePath is null ? "Untitled" : Path.GetFileName(FilePath);

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

    private bool _enableRunButton = true;
    public bool EnableRunButton
    {
        get => _enableRunButton;
        set => this.RaiseAndSetIfChanged(ref _enableRunButton, value);
    }

    private bool _enableCancelButton;
    public bool EnableCancelButton
    {
        get => _enableCancelButton;
        set => this.RaiseAndSetIfChanged(ref _enableCancelButton, value);
    }

    private bool _isRunningProgress;
    public bool IsRunningProgress
    {
        get => _isRunningProgress;
        set => this.RaiseAndSetIfChanged(ref _isRunningProgress, value);
    }

    private bool _isPaused;
    public bool IsPaused
    {
        get => _isPaused;
        set => this.RaiseAndSetIfChanged(ref _isPaused, value);
    }

    private bool _isDebugging;
    public bool IsDebugging
    {
        get => _isDebugging;
        set => this.RaiseAndSetIfChanged(ref _isDebugging, value);
    }

    private string _debugInfo = "No active debug session.";
    public string DebugInfo
    {
        get => _debugInfo;
        set => this.RaiseAndSetIfChanged(ref _debugInfo, value);
    }

    public EditorTabViewModel(string? filePath, List<ExecutionPlatform> platforms)
    {
        FilePath                = filePath;
        DotnetPlatforms         = platforms;
        _selectedDotnetPlatform = platforms.FirstOrDefault();
        CloseCommand            = ReactiveCommand.Create(() => { });
    }
}
