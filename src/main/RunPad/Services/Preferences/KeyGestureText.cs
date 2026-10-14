using Avalonia.Input;

namespace RunPad.Services.Preferences;

/// <summary>
/// The text form of a key binding, as shown in menus and Preferences and stored in settings:
/// "Ctrl+Shift+S", "Cmd+,", "Ctrl+]". Avalonia's own <see cref="KeyGesture.Parse"/> only knows key
/// enum names ("OemComma", "OemCloseBrackets"); both forms are accepted here.
/// </summary>
public static class KeyGestureText
{
    private static readonly (Key Key, string Text)[] Symbols =
    [
        (Key.OemComma, ","), (Key.OemPeriod, "."), (Key.OemMinus, "-"), (Key.OemPlus, "="),
        (Key.OemOpenBrackets, "["), (Key.OemCloseBrackets, "]"), (Key.OemPipe, "\\"),
        (Key.OemSemicolon, ";"), (Key.OemQuotes, "'"), (Key.OemQuestion, "/"), (Key.OemTilde, "`"),
        (Key.Back, "Backspace"), (Key.Return, "Enter"), (Key.Escape, "Esc"),
        (Key.D0, "0"), (Key.D1, "1"), (Key.D2, "2"), (Key.D3, "3"), (Key.D4, "4"),
        (Key.D5, "5"), (Key.D6, "6"), (Key.D7, "7"), (Key.D8, "8"), (Key.D9, "9"),
    ];

    /// <summary>Null for an empty string (no binding) or text that is not a key gesture.</summary>
    public static KeyGesture? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();

        // The key is everything after the last '+' that is not itself the key ("Ctrl+=" or "Ctrl++").
        var split = text.Length > 1 && text.EndsWith('+') ? text.Length - 2 : text.LastIndexOf('+');
        var modifiers = split < 0 ? "" : text[..split];
        var keyText = split < 0 ? text : text[(split + 1)..];

        var symbol = Symbols.FirstOrDefault(s => string.Equals(s.Text, keyText, StringComparison.OrdinalIgnoreCase));
        if (symbol.Text is not null) keyText = symbol.Key.ToString();
        else if (keyText == "+") keyText = nameof(Key.OemPlus);

        try
        {
            return KeyGesture.Parse(modifiers.Length == 0 ? keyText : modifiers + "+" + keyText);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public static string Format(KeyGesture gesture)
    {
        var parts = new List<string>();
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Meta)) parts.Add("Cmd");
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        var symbol = Symbols.FirstOrDefault(s => s.Key == gesture.Key);
        parts.Add(symbol.Text ?? gesture.Key.ToString());
        return string.Join("+", parts);
    }

    /// <summary>The canonical text for <paramref name="text"/>, or null when it doesn't parse.</summary>
    public static string? Normalize(string? text) => Parse(text) is { } gesture ? Format(gesture) : null;
}
