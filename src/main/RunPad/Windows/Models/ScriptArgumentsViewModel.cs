using System.Collections.ObjectModel;
using System.Reactive;
using System.Text.Json;
using ReactiveUI;
using RunPad.Format.RPad;

namespace RunPad.Windows.Models;

// A type an argument can have, as the type list shows it.
public sealed record ArgumentTypeOption(RpadArgumentType Type)
{
    public string Label => RpadArgument.TypeName(Type);
}

// One declared argument being edited: a row of name, type, default and (for an enum) its values.
public sealed class ArgumentRowViewModel : ReactiveObject
{
    public static IReadOnlyList<ArgumentTypeOption> Types { get; } = [.. Enum.GetValues<RpadArgumentType>().Select(t => new ArgumentTypeOption(t))];

    private string _name;
    private ArgumentTypeOption _type;
    private string _defaultText;
    private string _valuesText;
    private readonly RpadValue? _originalDefault;

    internal ArgumentRowViewModel(RpadArgument argument, Action<ArgumentRowViewModel> remove)
    {
        _name = argument.Name;
        _type = Types.First(t => t.Type == argument.Type);
        _originalDefault = argument.Default;
        _defaultText = argument.Default is { } value ? ValueText(value) : "";
        _valuesText = string.Join(", ", argument.Values);
        RemoveCommand = ReactiveCommand.Create(() => remove(this));
    }

    internal event Action? Edited;

    public string Name { get => _name; set => Set(ref _name, value ?? ""); }

    public ArgumentTypeOption Type
    {
        get => _type;
        set
        {
            if (value is null) return; // the ComboBox clears it while its items are replaced
            Set(ref _type, value);
            this.RaisePropertyChanged(nameof(IsEnum));
            this.RaisePropertyChanged(nameof(IsList));
        }
    }

    /// <summary>The default value; empty for none. A list has one item per line.</summary>
    public string DefaultText
    {
        get => _defaultText;
        set
        {
            Set(ref _defaultText, value ?? "");
            this.RaisePropertyChanged(nameof(EnumDefault));
        }
    }

    /// <summary>An enum's default, picked from its values. The ComboBox clears its selection while
    /// the values are retyped; that keeps the default instead of dropping it.</summary>
    public string? EnumDefault
    {
        get => EnumValues.Contains(_defaultText) ? _defaultText : null;
        set
        {
            if (value is not null) DefaultText = value;
        }
    }

    /// <summary>An enum's values, separated by commas.</summary>
    public string ValuesText
    {
        get => _valuesText;
        set
        {
            Set(ref _valuesText, value ?? "");
            this.RaisePropertyChanged(nameof(EnumValues));
            this.RaisePropertyChanged(nameof(EnumDefault));
        }
    }

    public IReadOnlyList<string> EnumValues =>
        [.. _valuesText.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];

    public bool IsEnum => _type.Type == RpadArgumentType.Enum;
    public bool IsList => _type.Type == RpadArgumentType.StringList;

    public ReactiveCommand<Unit, Unit> RemoveCommand { get; }

    internal RpadArgument ToArgument() => new()
    {
        Name = _name.Trim(),
        Type = _type.Type,
        Default = ValueOf(_defaultText, IsList, _originalDefault),
        Values = IsEnum ? [.. _valuesText.Split(',').Select(v => v.Trim()).Where(v => v.Length > 0)] : [],
    };

    // A value as the editor shows it: text as it is, a list one item per line.
    internal static string ValueText(RpadValue value) =>
        value.Items is { } items ? RpadArgumentLines.Format([.. items.Select(i => i.Text ?? "")]) : value.Text ?? "";

    // The value the text means: none when empty, a list of its lines for a list argument. The loaded
    // value is kept while its text is unchanged, so an item with a line break survives a save.
    internal static RpadValue? ValueOf(string text, bool list, RpadValue? original)
    {
        if (original is not null && ValueText(original) == text) return original;
        if (text.Length == 0) return null;
        return list ? RpadValue.List(RpadArgumentLines.Parse(text).Select(RpadValue.Of)) : RpadValue.Of(text);
    }

    private void Set<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        this.RaiseAndSetIfChanged(ref field, value);
        Edited?.Invoke();
    }
}

// One set's value for one declared argument: empty uses the argument's default.
public sealed class ArgumentSetValueViewModel : ReactiveObject
{
    private readonly ArgumentSetItemViewModel _set;

    internal ArgumentSetValueViewModel(ArgumentSetItemViewModel set, ArgumentRowViewModel argument)
    {
        _set = set;
        Argument = argument;
    }

    public ArgumentRowViewModel Argument { get; }

    public string Text
    {
        get => _set.TextFor(Argument);
        set
        {
            if (_set.TextFor(Argument) == (value ?? "")) return;
            _set.SetText(Argument, value ?? "");
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(ValueError));
        }
    }

    public string Hint => Argument.DefaultText.Length > 0 ? $"default: {Argument.DefaultText.Replace("\n", ", ")}" : "not passed";

    public string? ValueError
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Text)) return null;
            var declaration = new RpadArgument
            {
                Name = Argument.Name,
                Type = Argument.Type.Type,
                Values = [.. Argument.EnumValues],
            };
            return declaration.Check(RpadValue.Of(Text));
        }
    }

    public bool HasError => ValueError is not null;
}

// One named set being edited: its name and a value for each declared argument.
public sealed class ArgumentSetItemViewModel : ReactiveObject
{
    private string _name;
    private readonly Dictionary<ArgumentRowViewModel, (string Text, RpadValue? Original)> _values = [];
    private IReadOnlyList<ArgumentSetValueViewModel> _rows = [];

    internal ArgumentSetItemViewModel(string name) => _name = name;

    internal event Action? Edited;

    public string Name
    {
        get => _name;
        set
        {
            if (_name == (value ?? "")) return;
            this.RaiseAndSetIfChanged(ref _name, value ?? "");
            Edited?.Invoke();
        }
    }

    /// <summary>A value row for each declared argument, in their order.</summary>
    public IReadOnlyList<ArgumentSetValueViewModel> Values
    {
        get => _rows;
        private set => this.RaiseAndSetIfChanged(ref _rows, value);
    }

    // Values for names that aren't declared (a hand-edited file): not shown, kept as they are.
    internal Dictionary<string, RpadValue> Undeclared { get; } = new(StringComparer.Ordinal);

    internal void Load(ArgumentRowViewModel argument, RpadValue value) => _values[argument] = (ArgumentRowViewModel.ValueText(value), value);

    internal string TextFor(ArgumentRowViewModel argument) => _values.TryGetValue(argument, out var v) ? v.Text : "";

    internal void SetText(ArgumentRowViewModel argument, string text)
    {
        _values[argument] = (text, _values.TryGetValue(argument, out var v) ? v.Original : null);
        Edited?.Invoke();
    }

    internal void ShowRows(IEnumerable<ArgumentRowViewModel> arguments) => Values = [.. arguments.Select(a => new ArgumentSetValueViewModel(this, a))];

    internal Dictionary<string, RpadValue> ToValues(IEnumerable<ArgumentRowViewModel> arguments)
    {
        var values = new Dictionary<string, RpadValue>(Undeclared, StringComparer.Ordinal);
        foreach (var argument in arguments)
            if (_values.TryGetValue(argument, out var v) && ArgumentRowViewModel.ValueOf(v.Text, argument.IsList, v.Original) is { } value)
                values[argument.Name.Trim()] = value;
        return values;
    }
}

// What a run uses: the arguments' defaults (Set null) or one of the sets.
public sealed record ArgumentRunChoice(ArgumentSetItemViewModel? Set)
{
    public string Label => Set is { } set ? set.Name : "Defaults";
}

// The Arguments tab of Dependencies: the arguments the script declares, sets of their values, the set
// runs use. Edits are a draft, applied with the dialog's other changes (ApplyTo).
public sealed class ScriptArgumentsViewModel : ReactiveObject
{
    private RpadFrontMatter? _original;
    private ArgumentSetItemViewModel? _selectedSet;
    private ArgumentRunChoice? _runChoice;
    private IReadOnlyList<ArgumentRunChoice> _runChoices = [];
    private IReadOnlyList<string> _problems = [];
    private string? _blockingError;
    // A selectedArgumentSet: naming a set the script doesn't have, kept as it is until a choice is made.
    private string? _missingSelection;
    private bool _loading;

    public ScriptArgumentsViewModel()
    {
        AddArgumentCommand = ReactiveCommand.Create(AddArgument);
        AddSetCommand = ReactiveCommand.Create(AddSet);
        RemoveSetCommand = ReactiveCommand.Create(RemoveSelectedSet,
            this.WhenAnyValue(x => x.SelectedSet, (ArgumentSetItemViewModel? s) => s is not null));
    }

    internal event Action? Edited;

    public ObservableCollection<ArgumentRowViewModel> Arguments { get; } = [];
    public ObservableCollection<ArgumentSetItemViewModel> Sets { get; } = [];

    public ArgumentSetItemViewModel? SelectedSet
    {
        get => _selectedSet;
        set => this.RaiseAndSetIfChanged(ref _selectedSet, value);
    }

    /// <summary>The choices for runs: the defaults, then each set by name.</summary>
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

    /// <summary>Everything wrong with the draft: names, values of the wrong type, missing enum values.</summary>
    public IReadOnlyList<string> Problems
    {
        get => _problems;
        private set
        {
            this.RaiseAndSetIfChanged(ref _problems, value);
            this.RaisePropertyChanged(nameof(HasProblems));
        }
    }

    public bool HasProblems => Problems.Count > 0;

    /// <summary>A problem that blocks saving (an empty, invalid or repeated name), or null. A value of the
    /// wrong type doesn't: the run reports it.</summary>
    public string? BlockingError
    {
        get => _blockingError;
        private set
        {
            this.RaiseAndSetIfChanged(ref _blockingError, value);
            this.RaisePropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => BlockingError is not null;

    public ReactiveCommand<Unit, Unit> AddArgumentCommand { get; }
    public ReactiveCommand<Unit, Unit> AddSetCommand { get; }
    public ReactiveCommand<Unit, Unit> RemoveSetCommand { get; }

    internal void Load(RpadFrontMatter? fm)
    {
        _loading = true;
        _original = fm;
        foreach (var row in Arguments) row.Edited -= OnArgumentEdited;
        foreach (var set in Sets) set.Edited -= OnSetEdited;
        Arguments.Clear();
        Sets.Clear();
        foreach (var argument in fm?.Arguments ?? []) Arguments.Add(Track(new ArgumentRowViewModel(argument, RemoveArgument)));
        foreach (var (name, values) in fm?.ArgumentSets ?? [])
        {
            var set = Track(new ArgumentSetItemViewModel(name));
            foreach (var (key, value) in values)
                if (Arguments.FirstOrDefault(a => a.Name == key) is { } argument) set.Load(argument, value);
                else set.Undeclared[key] = value;
            Sets.Add(set);
        }
        // A selected set the script doesn't have shows as the defaults; it stays in the file (and a run
        // says it's missing) until another choice is made here.
        var selected = Sets.FirstOrDefault(s => s.Name == fm?.SelectedArgumentSet);
        _missingSelection = selected is null ? fm?.SelectedArgumentSet : null;
        RefreshSets(selected);
        SelectedSet = Sets.FirstOrDefault();
        _loading = false;
        OnDraftChanged();
    }

    internal bool HasChanges => _original is { } fm && Json(Draft()) != Json(fm);

    internal void ApplyTo(RpadFrontMatter fm)
    {
        var draft = Draft();
        fm.Arguments = draft.Arguments;
        fm.ArgumentSets = draft.ArgumentSets;
        fm.SelectedArgumentSet = draft.SelectedArgumentSet;
    }

    // The draft as front matter, with only what this tab edits.
    private RpadFrontMatter Draft() => new()
    {
        Arguments = [.. Arguments.Select(a => a.ToArgument())],
        ArgumentSets = SetsByName(),
        SelectedArgumentSet = RunChoice?.Set?.Name.Trim() ?? _missingSelection,
    };

    // A repeated name (while one is being typed) keeps the first set; the repeat is a blocking error.
    private Dictionary<string, Dictionary<string, RpadValue>> SetsByName()
    {
        var sets = new Dictionary<string, Dictionary<string, RpadValue>>(StringComparer.Ordinal);
        foreach (var set in Sets) sets.TryAdd(set.Name.Trim(), set.ToValues(Arguments));
        return sets;
    }

    private static string Json(RpadFrontMatter fm) =>
        JsonSerializer.Serialize(new { fm.Arguments, fm.ArgumentSets, fm.SelectedArgumentSet });

    private ArgumentRowViewModel Track(ArgumentRowViewModel row)
    {
        row.Edited += OnArgumentEdited;
        return row;
    }

    private ArgumentSetItemViewModel Track(ArgumentSetItemViewModel set)
    {
        set.Edited += OnSetEdited;
        return set;
    }

    private void OnArgumentEdited()
    {
        // The sets' rows show each argument's name, type and default.
        foreach (var set in Sets) set.ShowRows(Arguments);
        OnDraftChanged();
    }

    private void OnSetEdited()
    {
        // A renamed set keeps being the one runs use: the choice holds the set, not its name.
        RefreshSets(RunChoice?.Set);
        OnDraftChanged();
    }

    private void AddArgument()
    {
        var name = "argument";
        for (var i = 2; Arguments.Any(a => a.Name == name); i++) name = $"argument{i}";
        Arguments.Add(Track(new ArgumentRowViewModel(new RpadArgument { Name = name }, RemoveArgument)));
        OnArgumentEdited();
    }

    // Its values in the sets go with it.
    private void RemoveArgument(ArgumentRowViewModel row)
    {
        row.Edited -= OnArgumentEdited;
        Arguments.Remove(row);
        OnArgumentEdited();
    }

    private void AddSet()
    {
        var name = "set";
        for (var i = 2; Sets.Any(s => s.Name == name); i++) name = $"set{i}";
        var set = Track(new ArgumentSetItemViewModel(name));
        Sets.Add(set);
        SelectedSet = set;
        RefreshSets(RunChoice?.Set);
        OnDraftChanged();
    }

    private void RemoveSelectedSet()
    {
        if (SelectedSet is not { } set) return;
        var index = Sets.IndexOf(set);
        set.Edited -= OnSetEdited;
        Sets.Remove(set);
        SelectedSet = Sets.Count == 0 ? null : Sets[Math.Min(index, Sets.Count - 1)];
        // Runs that used the removed set go back to the defaults.
        RefreshSets(RunChoice?.Set == set ? null : RunChoice?.Set);
        OnDraftChanged();
    }

    private void RefreshSets(ArgumentSetItemViewModel? selected)
    {
        foreach (var set in Sets) set.ShowRows(Arguments);
        RunChoices = [new ArgumentRunChoice(null), .. Sets.Select(s => new ArgumentRunChoice(s))];
        _runChoice = RunChoices.First(c => c.Set == selected);
        this.RaisePropertyChanged(nameof(RunChoice));
    }

    private void OnDraftChanged()
    {
        if (_loading) return;
        var draft = Draft();
        Problems = RpadArguments.Validate(draft);
        var names = Sets.Select(s => s.Name.Trim()).ToList();
        BlockingError = Problems.FirstOrDefault(p => p.Contains("argument name") || p.Contains("declared twice"))
            ?? (names.Any(n => n.Length == 0) ? "Every argument set needs a name."
                : names.GroupBy(n => n, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } repeated
                    ? $"Two argument sets are named '{repeated.Key}'." : null);
        Edited?.Invoke();
    }
}
