using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reactive;
using ReactiveUI;
using RunPad.Services.Secrets;
using RunPad.Shared;

namespace RunPad.Windows.Models;

// One editable row of the Secret Store window; the editor under the list binds straight to it.
public sealed class SecretItemViewModel : ReactiveObject
{
    public string Name
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value ?? "");
    }

    public string Value
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value ?? "");
    }

    internal SecretItemViewModel(Secret secret)
    {
        Name = secret.Name;
        Value = secret.Value;
    }

    internal Secret ToSecret() => new(Name.Trim(), Value);
}

// Tools → Secret Store: the user's named secrets, which scripts read with Util.GetSecret("name").
// Edits stay in the window until OK writes the whole store; Cancel drops them. A store that can't
// be read is reported and never overwritten from here.
public sealed class SecretStoreWindowViewModel : ViewModelBase
{
    private const string NewSecretName = "secret";

    private readonly ISecretStore _store;

    public ObservableCollection<SecretItemViewModel> Secrets { get; } = [];

    public SecretItemViewModel? SelectedSecret
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(HasSelection));
            IsValueRevealed = false;
        }
    }

    public bool HasSelection => SelectedSecret is not null;

    // Shows the selected value instead of dots; hidden again whenever the selection changes.
    public bool IsValueRevealed
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(ValuePasswordChar));
        }
    }

    public char ValuePasswordChar => IsValueRevealed ? '\0' : '●';

    // Why the store couldn't be read; while set, nothing can be saved.
    public string? LoadError { get; }

    public bool HasLoadError => LoadError is not null;

    public string? ValidationError
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(HasValidationError));
            this.RaisePropertyChanged(nameof(CanSave));
        }
    }

    public bool HasValidationError => ValidationError is not null;

    public bool CanSave => LoadError is null && ValidationError is null;

    public string StorePath => _store.FilePath;

    public ReactiveCommand<Unit, Unit> AddCommand { get; }
    public ReactiveCommand<Unit, Unit> RemoveCommand { get; }

    public SecretStoreWindowViewModel(ISecretStore store)
    {
        _store = store;
        try
        {
            foreach (var secret in store.Load()) Add(new SecretItemViewModel(secret));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            LoadError = $"{ex.Message} It is kept unchanged: {store.FilePath}";
        }
        SelectedSecret = Secrets.FirstOrDefault();

        AddCommand = ReactiveCommand.Create(AddNew, this.WhenAnyValue(x => x.HasLoadError, error => !error));
        RemoveCommand = ReactiveCommand.Create(RemoveSelected, this.WhenAnyValue(x => x.HasSelection));
        Validate();
    }

    // Writes the store; false (nothing written) while a secret is invalid or the store unreadable.
    public bool Save()
    {
        Validate();
        if (!CanSave) return false;
        _store.Save([.. Secrets.Select(s => s.ToSecret())]);
        return true;
    }

    private void Add(SecretItemViewModel item)
    {
        item.PropertyChanged += OnItemChanged;
        Secrets.Add(item);
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e) => Validate();

    private void AddNew()
    {
        var name = NewSecretName;
        for (var i = 2; Secrets.Any(s => string.Equals(s.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)); i++)
            name = $"{NewSecretName}{i}";
        var item = new SecretItemViewModel(new Secret(name, ""));
        Add(item);
        SelectedSecret = item;
        Validate();
    }

    private void RemoveSelected()
    {
        if (SelectedSecret is not { } item) return;
        var index = Secrets.IndexOf(item);
        item.PropertyChanged -= OnItemChanged;
        Secrets.RemoveAt(index);
        SelectedSecret = Secrets.Count == 0 ? null : Secrets[Math.Min(index, Secrets.Count - 1)];
        Validate();
    }

    // Util.GetSecret finds a secret by name, letter case aside, so names must differ beyond case.
    private void Validate()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var secret in Secrets)
        {
            var name = secret.Name.Trim();
            if (name.Length == 0)
            {
                ValidationError = "Every secret needs a name.";
                return;
            }
            if (!names.Add(name))
            {
                ValidationError = $"Two secrets are named '{name}'; names must differ (letter case aside).";
                return;
            }
        }
        ValidationError = null;
    }
}
