using System.Collections.ObjectModel;
using DotPad.Assets.Models;
using DotPad.Build;
using DotPad.Build.Core;
using DotPad.Build.Core.Models;
using DotPad.Shared;
using DotPad.UserControls.Models;
using DynamicData;
using ReactiveUI;

namespace DotPad.Windows.Models;

public class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly DotnetSdksService _dotnetSdksService;
    private readonly IDotpFileService _fileSystemService;

    private readonly Dictionary<string, EditorTabItemViewModel> _openTabs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<EditorTabItemViewModel, IDisposable> _closeSubscriptions = new();

    // ── Tab collection ────────────────────────────────────────────────────────

    public ObservableCollection<EditorTabItemViewModel> Tabs { get; } = [];

    private EditorTabItemViewModel? _activeTab;
    public EditorTabItemViewModel? ActiveTab
    {
        get => _activeTab;
        set => this.RaiseAndSetIfChanged(ref _activeTab, value);
    }

    public EditorTabItemViewModel NewFile()
    {
        var folder = _fileSystemService.GetOrCreateDefaultFolder();
        var path = _fileSystemService.CreateNewFile(folder);
        Nodes.Add(new Node(Path.GetFileName(path), path));
        return OpenTab(path);
    }

    public EditorTabItemViewModel OpenTab(string? filePath)
    {
        if (filePath is not null && _openTabs.TryGetValue(filePath, out var existing))
        {
            ActiveTab = existing;
            return existing;
        }

        var vm = new EditorTabItemViewModel(filePath, DotnetPlatforms);
        _closeSubscriptions[vm] = vm.CloseCommand.Subscribe(_ => CloseTab(vm));

        if (filePath is not null) _openTabs[filePath] = vm;
        Tabs.Add(vm);
        ActiveTab = vm;
        return vm;
    }

    internal void CloseTab(EditorTabItemViewModel vm)
    {
        if (_closeSubscriptions.TryGetValue(vm, out var d))
        {
            d.Dispose();
            _closeSubscriptions.Remove(vm);
        }
        if (vm.FilePath is not null) _openTabs.Remove(vm.FilePath);

        var nextActive = ActiveTab == vm ? Tabs.LastOrDefault(t => t != vm) : null;
        Tabs.Remove(vm);
        if (nextActive is not null) ActiveTab = nextActive;
    }

    // ── File tree ─────────────────────────────────────────────────────────────

    public ObservableCollection<Node> Nodes { get; }
    public List<ExecutionPlatform> DotnetPlatforms { get; }

    public MainWindowViewModel(
        DotnetSdksService dotnetSdksService,
        IDotpFileService fileSystemService)
    {
        _dotnetSdksService = dotnetSdksService;
        _fileSystemService = fileSystemService;

        DotnetPlatforms = _dotnetSdksService.GetSdkInfos().ToList();

        var root = _fileSystemService.GetOrCreateDefaultFolder();
        var files = _fileSystemService.GetFilesInFolder(root);

        Nodes = new ObservableCollection<Node>();
        Nodes.AddRange(files.Select(s => new Node(Path.GetFileName(s), s)));
    }

    public void Dispose()
    {
        foreach (var d in _closeSubscriptions.Values) d.Dispose();
        _fileSystemService.Dispose();
    }
}
