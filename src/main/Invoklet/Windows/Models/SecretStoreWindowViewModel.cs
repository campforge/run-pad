using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reactive;
using System.Security.Cryptography;
using Invoklet.Services.Secrets;
using Invoklet.Shared;
using ReactiveUI;

namespace Invoklet.Windows.Models;

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
    private string? _version;
    private Secret[] _saved = [];

    public ObservableCollection<SecretItemViewModel> Secrets { get; } = [];

    public SecretItemViewModel? SelectedSecret
    {
        get;
        set
        {
            IsValueRevealed = false;
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(HasSelection));
        }
    }

    public bool HasSelection => SelectedSecret is not null;
    public bool IsEmpty => LoadError is null && Secrets.Count == 0;

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

    public string? SaveError
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(StoreError));
            this.RaisePropertyChanged(nameof(HasStoreError));
        }
    }

    public string? StoreError => LoadError ?? SaveError;
    public bool HasStoreError => StoreError is not null;

    public bool HasLoadError => LoadError is not null;

    /// <summary>Why the values aren't encrypted on this machine (no system keychain), when they aren't.</summary>
    public string? StoreWarning => _store.Warning;
    public bool HasStoreWarning => StoreWarning is not null;

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

    // Whether the secrets differ from what was loaded or last saved; selection and reveal state don't count.
    public bool HasChanges { get; private set; }

    public ReactiveCommand<Unit, Unit> AddCommand { get; }
    public ReactiveCommand<Unit, Unit> RemoveCommand { get; }

    public SecretStoreWindowViewModel(ISecretStore store)
    {
        _store = store;
        Secrets.CollectionChanged += (_, _) => this.RaisePropertyChanged(nameof(IsEmpty));
        try
        {
            var snapshot = store.LoadSnapshot();
            _version = snapshot.Version;
            foreach (var secret in snapshot.Secrets) Add(new SecretItemViewModel(secret));
            _saved = [.. Secrets.Select(s => s.ToSecret())];
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
        SaveError = null;
        Validate();
        if (!CanSave) return false;
        try
        {
            Secret[] secrets = [.. Secrets.Select(s => s.ToSecret())];
            _version = _store.Save(secrets, _version);
            _saved = secrets;
            UpdateHasChanges();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            // Exception text can contain data from a serializer or encryption provider. Keep secrets
            // out of UI errors while retaining the user's edits and allowing a retry.
            SaveError = ex switch
            {
                SecretStoreConflictException =>
                    "The secret store changed in another window. Reopen it before saving. Your edits have not been written.",
                SecretStoreBusyException =>
                    "The secret store is in use by another Invoklet window or a running script. Try again in a moment. Your edits are still here.",
                // Our own wording and the keychain's error: no secret value in it.
                Invoklet.Build.Core.Secrets.SecretKeyUnavailableException => ex.Message + " Your edits are still here.",
                _ => "Could not save the secret store. Check access and available disk space, then try again. Your edits are still here.",
            };
            return false;
        }
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

    private void UpdateHasChanges()
    {
        var changed = LoadError is null && !Secrets.Select(s => s.ToSecret()).SequenceEqual(_saved);
        if (changed == HasChanges) return;
        HasChanges = changed;
        this.RaisePropertyChanged(nameof(HasChanges));
    }

    // Util.GetSecret finds a secret by name, letter case aside, so names must differ beyond case.
    private void Validate()
    {
        UpdateHasChanges();
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
