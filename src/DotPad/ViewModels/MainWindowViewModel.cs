using System;
using System.Collections.ObjectModel;
using DotPad.Build;
using System.Collections.Generic;
using System.Linq;
using DotPad.Build.Models;
using DotPad.Models;
using DynamicData;

namespace DotPad.ViewModels;

public class MainWindowViewModel : ViewModelBase, IDisposable
{
    readonly DotnetSdksService _dotnetSdksService = new();
    readonly FileSystemService _fileSystemService = new();
    public string Greeting => "Welcome to Avalonia!";

    public ObservableCollection<Node> Nodes { get; }

    public List<ExecutionPlatform> DotnetPlatforms { get; }
    public bool EnableRunButton { get; set; } = true;

    public string? SelectedDotnetPlatform { get; set; }

    public MainWindowViewModel()
    {

        var list = _dotnetSdksService.GetSdkInfos().ToList();
        DotnetPlatforms = list;
        SelectedDotnetPlatform = list.FirstOrDefault()?.Description;
        var root = _fileSystemService.GetOrCreateDefaultProFilesFolder();
        var files = _fileSystemService.GetFilesInFolder(root);

        Nodes = new ObservableCollection<Node>();

        Nodes.AddRange(files.Select(s => new Node(s)));

        // Nodes = new ObservableCollection<Node>
        // {
        //     new("Animals", new ObservableCollection<Node>
        //     {
        //         new("Mammals", new ObservableCollection<Node>
        //         {
        //             new("Lion"), new("Cat"), new("Zebra")
        //         })
        //     })
        // };
    }

    public void Dispose()
    {
        _fileSystemService.Dispose();
    }
}
