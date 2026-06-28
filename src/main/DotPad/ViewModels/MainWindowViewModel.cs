using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using DotPad.Build;
using DotPad.Build.Core;
using DotPad.Build.Core.Models;
using DotPad.Models;
using DynamicData;
using ReactiveUI;

namespace DotPad.ViewModels;

internal class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly DotnetSdksService _dotnetSdksService;
    private readonly IDotpFileService _fileSystemService;

    public string GreetingDebugger => "Debugger!";
    public ObservableCollection<Node> Nodes { get; }
    public List<ExecutionPlatform> DotnetPlatforms { get; }

    private bool _enableRunButton = true;
    public bool EnableRunButton
    {
        get => _enableRunButton;
        set => this.RaiseAndSetIfChanged(ref _enableRunButton, value);
    }

    private bool _enableCancelButton = false;
    public bool EnableCancelButton
    {
        get => _enableCancelButton;
        set => this.RaiseAndSetIfChanged(ref _enableCancelButton, value);
    }

    private ExecutionPlatform? _selectedDotnetPlatform;
    public ExecutionPlatform? SelectedDotnetPlatform
    {
        get => _selectedDotnetPlatform;
        set => this.RaiseAndSetIfChanged(ref _selectedDotnetPlatform, value);
    }

    public MainWindowViewModel(DotnetSdksService dotnetSdksService, IDotpFileService fileSystemService)
    {
        _dotnetSdksService = dotnetSdksService;
        _fileSystemService = fileSystemService;

        var list = _dotnetSdksService.GetSdkInfos().ToList();
        DotnetPlatforms = list;
        SelectedDotnetPlatform = list.FirstOrDefault();

        var root = _fileSystemService.GetOrCreateDefaultFolder();
        var files = _fileSystemService.GetFilesInFolder(root);

        Nodes = new ObservableCollection<Node>();
        Nodes.AddRange(files.Select(s => new Node(s)));
    }

    public void Dispose()
    {
        _fileSystemService.Dispose();
    }
}
