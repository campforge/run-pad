using System.Collections.ObjectModel;
using ReactiveUI;

namespace DotPad.UserControls.Models;

public sealed class DebugLocal : ReactiveObject
{
    public string Name { get; }
    public string Value { get; }
    public string TypeName { get; }
    public bool HasChildren { get; }

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            this.RaiseAndSetIfChanged(ref _isExpanded, value);
            if (value && !_loaded && _loader is not null)
                _ = LoadAsync();
        }
    }

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        private set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }

    private bool _loaded;
    public ObservableCollection<DebugLocal> Children { get; } = [];

    private readonly Func<Task<IReadOnlyList<DebugLocal>>>? _loader;

    public DebugLocal(string name, string value, string typeName,
        bool hasChildren = false, Func<Task<IReadOnlyList<DebugLocal>>>? loader = null)
    {
        Name = name;
        Value = value;
        TypeName = typeName;
        HasChildren = hasChildren;
        _loader = loader;
        if (hasChildren)
            Children.Add(new DebugLocal("", "...", ""));
    }

    private async Task LoadAsync()
    {
        if (_loaded) return;
        IsLoading = true;
        Children.Clear();
        try
        {
            var items = await _loader!();
            foreach (var item in items) Children.Add(item);
            _loaded = true;
        }
        catch (Exception ex)
        {
            Children.Add(new DebugLocal("", $"Error: {ex.Message}", ""));
        }
        finally
        {
            IsLoading = false;
        }
    }
}
