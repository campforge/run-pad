using System.Reactive;
using Invoklet.Services.Preferences;
using ReactiveUI;

namespace Invoklet.Windows.Models;

/// <summary>One command on the Keyboard page of Preferences.</summary>
public sealed class KeyBindingRowViewModel : ReactiveObject
{
    private readonly KeyboardSettings _keyboard;
    private readonly Action _changed;

    public KeyBindingRowViewModel(KeyCommand command, KeyboardSettings keyboard, Action changed)
    {
        Command = command;
        _keyboard = keyboard;
        _changed = changed;
        ResetCommand = ReactiveCommand.Create(() => { Key = DefaultKey; });
    }

    public KeyCommand Command { get; }
    public string Category => Command.Category;
    public string Title => Command.Title;
    public string DefaultKey => Command.DefaultKeyFor(_keyboard.IsMac);
    public string DefaultKeyText => DefaultKey.Length == 0 ? "Default: none" : $"Default: {DefaultKey}";

    /// <summary>The bound key as typed; empty unbinds the command.</summary>
    public string Key
    {
        get => _keyboard.KeyFor(Command);
        set
        {
            if (value == Key) return;
            _keyboard.SetKey(Command, value);
            _changed();
        }
    }

    public bool IsCustomized => _keyboard.IsCustomized(Command);

    /// <summary>Why the binding may not work: not a key combination, or a key shared with another
    /// command. Empty when there is nothing to report.</summary>
    public string Warning
    {
        get
        {
            var key = Key;
            if (key.Length > 0 && KeyGestureText.Parse(key) is null) return "Not a key combination";
            var others = _keyboard.ConflictsOf(Command).Select(c => c.Title).ToList();
            return others.Count == 0 ? "" : "Also used by " + string.Join(", ", others);
        }
    }

    public bool HasWarning => Warning.Length > 0;

    public ReactiveCommand<Unit, Unit> ResetCommand { get; }

    /// <summary>Called after any binding changed: a change elsewhere can add or clear a conflict here.</summary>
    internal void Refresh()
    {
        this.RaisePropertyChanged(nameof(Key));
        this.RaisePropertyChanged(nameof(IsCustomized));
        this.RaisePropertyChanged(nameof(Warning));
        this.RaisePropertyChanged(nameof(HasWarning));
    }
}
