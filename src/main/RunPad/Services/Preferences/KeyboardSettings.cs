using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Input;

namespace RunPad.Services.Preferences;

/// <summary>
/// The user's own key bindings, by <see cref="KeyCommand.Id"/>. Only changed bindings are stored, as
/// in VS Code's keybindings.json: everything else follows <see cref="KeyCommands"/>' defaults for the
/// current OS, so a default can change in a later version without being pinned by an old settings
/// file. An empty string unbinds a command.
/// </summary>
public sealed class KeyboardSettings
{
    public Dictionary<string, string> Bindings { get; set; } = new();

    /// <summary>Whether the macOS defaults apply. Settable for tests.</summary>
    [JsonIgnore]
    public bool IsMac { get; set; } = OperatingSystem.IsMacOS();

    /// <summary>The modifier held with a click or the mouse wheel (go to definition, zoom): Ctrl, or
    /// Cmd on macOS.</summary>
    [JsonIgnore]
    public KeyModifiers CommandModifier => IsMac ? KeyModifiers.Meta : KeyModifiers.Control;

    /// <summary>The key text for <paramref name="command"/>: the user's binding, else the default
    /// for this OS. Empty when the command has no key.</summary>
    public string KeyFor(KeyCommand command)
        => Bindings.TryGetValue(command.Id, out var key) ? key : command.DefaultKeyFor(IsMac);

    public KeyGesture? GestureFor(KeyCommand command) => KeyGestureText.Parse(KeyFor(command));

    public bool Matches(KeyEventArgs e, KeyCommand command) => GestureFor(command)?.Matches(e) == true;

    public bool IsCustomized(KeyCommand command) => Bindings.ContainsKey(command.Id);

    /// <summary>Binds <paramref name="command"/> to <paramref name="key"/>; the default key (or null)
    /// removes the user's binding instead of storing a copy of the default.</summary>
    public void SetKey(KeyCommand command, string? key)
    {
        key = key is null ? null : KeyGestureText.Normalize(key) ?? key.Trim();
        if (key is null || key == command.DefaultKeyFor(IsMac)) Bindings.Remove(command.Id);
        else Bindings[command.Id] = key;
    }

    /// <summary>Other commands bound to the same key as <paramref name="command"/> where one keypress
    /// can reach both (<see cref="KeyCommands.ScopesOf"/>). A file-tree key may repeat an editor key,
    /// and an Assembly Explorer key a main-window one: they never see the same keypress.</summary>
    public IEnumerable<KeyCommand> ConflictsOf(KeyCommand command)
    {
        if (GestureFor(command) is not { } gesture) yield break;
        var scopes = KeyCommands.ScopesOf(command);
        foreach (var other in KeyCommands.All)
        {
            if (other == command || !KeyCommands.ScopesOf(other).Any(o => scopes.Any(s => s.Overlaps(o)))) continue;
            if (GestureFor(other) is { } g && g.Key == gesture.Key && g.KeyModifiers == gesture.KeyModifiers)
                yield return other;
        }
    }

    // ── Settings written before version 0.5 ──────────────────────────────────

    // Those stored every binding, defaults included, as one property each ("Run": "F5"). Read here
    // and turned into Bindings by MigrateLegacy; values that were the old defaults are dropped, so
    // they follow the new (and on macOS, Cmd-based) defaults.
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Legacy { get; set; }

    private static readonly (string Property, KeyCommand Command, string OldDefault)[] LegacyProperties =
    [
        ("Run", KeyCommands.RunOrContinue, "F5"),
        ("Cancel", KeyCommands.Stop, "Escape"),
        ("StepOver", KeyCommands.StepOver, "F10"),
        ("StepIn", KeyCommands.StepInto, "F11"),
        ("StepOut", KeyCommands.StepOut, "Shift+F11"),
        ("Format", KeyCommands.Format, "Shift+Alt+F"),
        ("GoToLine", KeyCommands.GoToLine, "Ctrl+G"),
        ("Save", KeyCommands.Save, "Ctrl+S"),
        ("SaveAs", KeyCommands.SaveAs, "Ctrl+Shift+S"),
        ("OpenDependencies", KeyCommands.OpenDependencies, "F4"),
        ("CloseTab", KeyCommands.CloseTab, "Ctrl+W"),
        ("RenameFile", KeyCommands.RenameFile, "F2"),
        ("DeleteFile", KeyCommands.DeleteFile, "Delete"),
        ("NewFile", KeyCommands.NewFile, "Ctrl+N"),
        ("ResolveAllUsings", KeyCommands.ResolveAllUsings, "Ctrl+Alt+U"),
    ];

    /// <summary>Moves bindings from the pre-0.5 format into <see cref="Bindings"/>. Returns whether
    /// anything was found, so the caller can save the file in the new format.</summary>
    public bool MigrateLegacy()
    {
        if (Legacy is not { Count: > 0 } legacy) return false;
        foreach (var (property, command, oldDefault) in LegacyProperties)
        {
            if (!legacy.TryGetValue(property, out var value) || value.ValueKind != JsonValueKind.String) continue;
            var key = value.GetString() ?? "";
            if (Bindings.ContainsKey(command.Id)
                || KeyGestureText.Normalize(key) == KeyGestureText.Normalize(oldDefault)) continue;
            Bindings[command.Id] = KeyGestureText.Normalize(key) ?? key;
        }
        Legacy = null;
        return true;
    }
}
