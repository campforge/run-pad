using System.Collections.ObjectModel;
using System.Reactive;
using ReactiveUI;
using RunPad.Format.RPad;

namespace RunPad.Windows.Models;

// One named argument set being edited: its name and its arguments, one per line.
public sealed class ArgumentSetItemViewModel : ReactiveObject
{
    private string _name;
    private string _text;

    internal ArgumentSetItemViewModel(string name, IReadOnlyList<string> arguments)
    {
        _name = name;
        Original = arguments;
        _text = RpadArgumentLines.Format(arguments);
        Fits = RpadArgumentLines.Fits(arguments);
    }

    internal event Action? Edited;

    public string Name
    {
        get => _name;
        set
        {
            this.RaiseAndSetIfChanged(ref _name, value ?? "");
            Edited?.Invoke();
        }
    }

    public string Text
    {
        get => _text;
        set
        {
            this.RaiseAndSetIfChanged(ref _text, value ?? "");
            Edited?.Invoke();
        }
    }

    // The list as it was loaded: kept as it is while its text isn't edited, so an argument with a line
    // break (which has no line of its own) survives a save.
    internal IReadOnlyList<string> Original { get; }

    // False when an argument has a line break: the text can't show it, and editing it would split it.
    public bool Fits { get; }

    internal IReadOnlyList<string> Arguments =>
        _text == RpadArgumentLines.Format(Original) ? Original : RpadArgumentLines.Parse(_text);
}

// What a run uses: the script's args: (Set null) or one of its sets.
public sealed record ArgumentRunChoice(ArgumentSetItemViewModel? Set)
{
    public string Label => Set is { } set ? set.Name : "Default (args:)";
}

// The Arguments tab of Dependencies: the script's args:, its named sets and the one runs use. Edits
// are a draft, applied to the front matter with the dialog's other changes (ApplyTo).
public sealed class ScriptArgumentsViewModel : ReactiveObject
{
    private RpadFrontMatter? _original;
    private string _defaultText = "";
    private ArgumentSetItemViewModel? _selectedSet;
    private ArgumentRunChoice? _runChoice;
    private string? _error;
    private IReadOnlyList<string> _originalArgs = [];
    private IReadOnlyList<ArgumentRunChoice> _runChoices = [];
    // A selectedArgumentSet: naming a set the script doesn't have, kept as it is until a run choice is made.
    private string? _missingSelection;

    public ScriptArgumentsViewModel()
    {
        AddCommand = ReactiveCommand.Create(AddSet);
        RemoveCommand = ReactiveCommand.Create(RemoveSelected,
            this.WhenAnyValue(x => x.SelectedSet, (ArgumentSetItemViewModel? s) => s is not null));
    }

    internal event Action? Edited;

    /// <summary>The script's <c>args:</c>, one argument per line.</summary>
    public string DefaultText
    {
        get => _defaultText;
        set
        {
            this.RaiseAndSetIfChanged(ref _defaultText, value ?? "");
            OnDraftChanged();
        }
    }

    public ObservableCollection<ArgumentSetItemViewModel> Sets { get; } = [];

    public ArgumentSetItemViewModel? SelectedSet
    {
        get => _selectedSet;
        set => this.RaiseAndSetIfChanged(ref _selectedSet, value);
    }

    /// <summary>The choices for runs: the default arguments, then each set by name.</summary>
    public IReadOnlyList<ArgumentRunChoice> RunChoices
    {
        get => _runChoices;
        private set => this.RaiseAndSetIfChanged(ref _runChoices, value);
    }

    public ArgumentRunChoice? RunChoice
    {
        get => _runChoice;
        set
        {
            if (value is null || value == _runChoice) return; // the ComboBox clears it while its items are replaced
            this.RaiseAndSetIfChanged(ref _runChoice, value);
            _missingSelection = null;
            OnDraftChanged();
        }
    }

    /// <summary>Why the sets can't be saved (an empty or repeated name), or null.</summary>
    public string? Error
    {
        get => _error;
        private set
        {
            this.RaiseAndSetIfChanged(ref _error, value);
            this.RaisePropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => Error is not null;

    public ReactiveCommand<Unit, Unit> AddCommand { get; }
    public ReactiveCommand<Unit, Unit> RemoveCommand { get; }

    internal void Load(RpadFrontMatter? fm)
    {
        _original = fm;
        foreach (var set in Sets) set.Edited -= OnSetEdited;
        Sets.Clear();
        _originalArgs = fm?.Args ?? [];
        _defaultText = RpadArgumentLines.Format(_originalArgs);
        foreach (var (name, arguments) in fm?.ArgumentSets ?? [])
            Sets.Add(Track(new ArgumentSetItemViewModel(name, arguments)));
        // A selected set the script doesn't have shows as the default; it stays in the file (and a run
        // says it's missing) until another choice is made here.
        var selected = Sets.FirstOrDefault(s => s.Name == fm?.SelectedArgumentSet);
        _missingSelection = selected is null ? fm?.SelectedArgumentSet : null;
        RebuildChoices(selected);
        SelectedSet = Sets.FirstOrDefault();
        Error = null;
        this.RaisePropertyChanged(nameof(DefaultText));
    }

    internal bool HasChanges
    {
        get
        {
            if (_original is not { } fm) return false;
            var sets = fm.ArgumentSets.ToList();
            return !DefaultArguments.SequenceEqual(fm.Args ?? [], StringComparer.Ordinal)
                   || sets.Count != Sets.Count
                   || sets.Zip(Sets).Any(p => p.First.Key != p.Second.Name
                                             || !p.First.Value.SequenceEqual(p.Second.Arguments, StringComparer.Ordinal))
                   || fm.SelectedArgumentSet != SelectedName;
        }
    }

    internal void ApplyTo(RpadFrontMatter fm)
    {
        var args = DefaultArguments;
        // Kept null rather than empty when there were none, as the other lists are.
        fm.Args = fm.Args is null && args.Count == 0 ? null : [.. args];
        fm.ArgumentSets = Sets.ToDictionary(s => s.Name, s => s.Arguments.ToList(), StringComparer.Ordinal);
        fm.SelectedArgumentSet = SelectedName;
    }

    private string? SelectedName => RunChoice?.Set?.Name ?? _missingSelection;

    private IReadOnlyList<string> DefaultArguments =>
        _defaultText == RpadArgumentLines.Format(_originalArgs) ? _originalArgs : RpadArgumentLines.Parse(_defaultText);

    private ArgumentSetItemViewModel Track(ArgumentSetItemViewModel set)
    {
        set.Edited += OnSetEdited;
        return set;
    }

    private void OnSetEdited()
    {
        // A renamed set keeps being the one runs use: the choice holds the set, not its name.
        RebuildChoices(RunChoice?.Set);
        OnDraftChanged();
    }

    private void AddSet()
    {
        var name = "set";
        for (var i = 2; Sets.Any(s => s.Name == name); i++) name = $"set {i}";
        var set = Track(new ArgumentSetItemViewModel(name, []));
        Sets.Add(set);
        SelectedSet = set;
        RebuildChoices(RunChoice?.Set);
        OnDraftChanged();
    }

    private void RemoveSelected()
    {
        if (SelectedSet is not { } set) return;
        var index = Sets.IndexOf(set);
        set.Edited -= OnSetEdited;
        Sets.Remove(set);
        SelectedSet = Sets.Count == 0 ? null : Sets[Math.Min(index, Sets.Count - 1)];
        // Runs that used the removed set go back to the default arguments.
        RebuildChoices(RunChoice?.Set == set ? null : RunChoice?.Set);
        OnDraftChanged();
    }

    private void RebuildChoices(ArgumentSetItemViewModel? selected)
    {
        RunChoices = [new ArgumentRunChoice(null), .. Sets.Select(s => new ArgumentRunChoice(s))];
        _runChoice = RunChoices.First(c => c.Set == selected);
        this.RaisePropertyChanged(nameof(RunChoice));
    }

    private void OnDraftChanged()
    {
        var names = Sets.Select(s => s.Name).ToList();
        Error = names.Any(string.IsNullOrWhiteSpace) ? "Every argument set needs a name."
            : names.GroupBy(n => n, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } repeated
                ? $"Two argument sets are named '{repeated.Key}'."
                : null;
        Edited?.Invoke();
    }
}
