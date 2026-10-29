using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reactive;
using System.Reactive.Linq;
using Invoklet.Contracts;
using Invoklet.Shared;
using ReactiveUI;

namespace Invoklet.Windows.Models;

// One editable row of the NuGet source configurator. The editor fields below the list bind straight
// to the selected row (no separate "Save Entry" step); nothing reaches the config file until OK.
public sealed class NuGetSourceItemViewModel : ReactiveObject
{
    private string _name;
    private string _url;
    private bool _isEnabled;
    private bool _useBasicAuth;
    private string _username;
    private string _password;

    public string Name
    {
        get => _name;
        set => this.RaiseAndSetIfChanged(ref _name, value ?? "");
    }

    public string Url
    {
        get => _url;
        set => this.RaiseAndSetIfChanged(ref _url, value ?? "");
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => this.RaiseAndSetIfChanged(ref _isEnabled, value);
    }

    // Authentication: false = none, true = basic (username + password or personal access token).
    public bool UseBasicAuth
    {
        get => _useBasicAuth;
        set
        {
            this.RaiseAndSetIfChanged(ref _useBasicAuth, value);
            this.RaisePropertyChanged(nameof(UseNoAuth));
        }
    }

    // RadioButton mirror of UseBasicAuth; the "false" half of a radio switch is ignored.
    public bool UseNoAuth
    {
        get => !UseBasicAuth;
        set { if (value) UseBasicAuth = false; }
    }

    public string Username
    {
        get => _username;
        set => this.RaiseAndSetIfChanged(ref _username, value ?? "");
    }

    public string Password
    {
        get => _password;
        set => this.RaiseAndSetIfChanged(ref _password, value ?? "");
    }

    internal NuGetSourceItemViewModel(NuGetSource source)
    {
        _name = source.Name;
        _url = source.Url;
        _isEnabled = source.IsEnabled;
        _useBasicAuth = source.HasCredentials;
        _username = source.Username ?? "";
        _password = source.Password ?? "";
    }

    internal NuGetSource ToSource() => UseBasicAuth && Username.Trim().Length > 0
        ? new NuGetSource(Name.Trim(), Url.Trim(), IsEnabled, Username.Trim(), Password)
        : new NuGetSource(Name.Trim(), Url.Trim(), IsEnabled);
}

// LINQPad-style "NuGet Settings → Package Sources": ordered list with enable checkboxes, Move to
// Top/Up/Down, Add/Remove, and an editor for the selected source. OK writes Invoklet's NuGet.Config
// through INuGetSourceStore (search and restore both pick it up); Cancel drops every edit.
public sealed class NuGetSourcesWindowViewModel : ViewModelBase
{
    private const string NewSourceName = "Package source";

    private readonly INuGetSourceStore _store;
    private NuGetSourceItemViewModel? _selectedSource;
    private string? _validationError;
    private int _selectedIndex = -1;

    public ObservableCollection<NuGetSourceItemViewModel> Sources { get; } = [];

    public NuGetSourceItemViewModel? SelectedSource
    {
        get => _selectedSource;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedSource, value);
            this.RaisePropertyChanged(nameof(HasSelection));
            UpdateSelectedIndex();
        }
    }

    public bool HasSelection => SelectedSource is not null;

    // Position of the selection; drives Move to Top/Up/Down availability (also after reordering).
    public int SelectedIndex
    {
        get => _selectedIndex;
        private set => this.RaiseAndSetIfChanged(ref _selectedIndex, value);
    }

    public string? ValidationError
    {
        get => _validationError;
        private set
        {
            this.RaiseAndSetIfChanged(ref _validationError, value);
            this.RaisePropertyChanged(nameof(HasValidationError));
            this.RaisePropertyChanged(nameof(CanSave));
        }
    }

    public bool HasValidationError => ValidationError is not null;
    public bool CanSave => ValidationError is null;

    public ReactiveCommand<Unit, Unit> MoveToTopCommand { get; }
    public ReactiveCommand<Unit, Unit> MoveUpCommand { get; }
    public ReactiveCommand<Unit, Unit> MoveDownCommand { get; }
    public ReactiveCommand<Unit, Unit> AddCommand { get; }
    public ReactiveCommand<Unit, Unit> RemoveCommand { get; }

    public NuGetSourcesWindowViewModel(INuGetSourceStore store)
    {
        _store = store;

        foreach (var source in store.Load() ?? [])
            Add(new NuGetSourceItemViewModel(source));
        SelectedSource = Sources.FirstOrDefault();

        Sources.CollectionChanged += (_, _) => UpdateSelectedIndex();
        var index = this.WhenAnyValue(x => x.SelectedIndex);

        MoveToTopCommand = ReactiveCommand.Create(() => MoveSelected(0), index.Select(i => i > 0));
        MoveUpCommand = ReactiveCommand.Create(() => MoveSelected(SelectedIndex - 1), index.Select(i => i > 0));
        MoveDownCommand = ReactiveCommand.Create(() => MoveSelected(SelectedIndex + 1),
            index.Select(i => i >= 0 && i < Sources.Count - 1));
        AddCommand = ReactiveCommand.Create(AddNew);
        RemoveCommand = ReactiveCommand.Create(RemoveSelected, this.WhenAnyValue(x => x.HasSelection));

        Validate();
    }

    // Writes the config file; false (nothing written) while an entry is invalid.
    public bool Save()
    {
        Validate();
        if (!CanSave) return false;
        _store.Save(Sources.Select(s => s.ToSource()).ToList());
        return true;
    }

    private void Add(NuGetSourceItemViewModel item)
    {
        item.PropertyChanged += OnItemChanged;
        Sources.Add(item);
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e) => Validate();

    private void AddNew()
    {
        var name = NewSourceName;
        for (var i = 2; Sources.Any(s => string.Equals(s.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)); i++)
            name = $"{NewSourceName} {i}";

        var item = new NuGetSourceItemViewModel(new NuGetSource(name, ""));
        Add(item);
        SelectedSource = item;
        Validate();
    }

    private void RemoveSelected()
    {
        if (SelectedSource is not { } item) return;
        var index = Sources.IndexOf(item);
        item.PropertyChanged -= OnItemChanged;
        Sources.RemoveAt(index);
        SelectedSource = Sources.Count == 0 ? null : Sources[Math.Min(index, Sources.Count - 1)];
        Validate();
    }

    private void MoveSelected(int newIndex)
    {
        if (SelectedSource is not { } item) return;
        var oldIndex = Sources.IndexOf(item);
        if (oldIndex < 0 || newIndex < 0 || newIndex >= Sources.Count || newIndex == oldIndex) return;
        Sources.Move(oldIndex, newIndex);
        SelectedSource = item;
    }

    private void UpdateSelectedIndex() => SelectedIndex = SelectedSource is null ? -1 : Sources.IndexOf(SelectedSource);

    private void Validate() => ValidationError = FindError();

    private string? FindError()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in Sources)
        {
            var name = source.Name.Trim();
            if (name.Length == 0)
                return "Every package source needs a name.";
            if (!seen.Add(name))
                return $"There is more than one package source named \"{name}\".";
            if (source.Url.Trim().Length == 0)
                return $"Enter a URL or a local folder for \"{name}\".";
            if (source.Url.Contains("://", StringComparison.Ordinal)
                && !Uri.TryCreate(source.Url.Trim(), UriKind.Absolute, out _))
                return $"The source of \"{name}\" is not a valid URL.";
            if (source.UseBasicAuth && source.Username.Trim().Length == 0)
                return $"Enter a username for \"{name}\", or choose no authentication.";
        }
        return null;
    }
}
