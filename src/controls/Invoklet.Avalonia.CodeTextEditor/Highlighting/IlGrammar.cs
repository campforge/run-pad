using AvaloniaEdit;
using AvaloniaEdit.TextMate;
using TextMateSharp.Grammars;
using TextMateSharp.Internal.Grammars.Reader;
using TextMateSharp.Internal.Types;
using TextMateSharp.Registry;
using TextMateSharp.Themes;

namespace Invoklet.Avalonia.CodeTextEditor;

/// <summary>
/// The TextMate grammar for common ILAsm text, as <c>ildasm</c> and the Assembly Explorer print it.
/// TextMateSharp's bundled grammars have none. Covered: <c>//</c> and <c>/* */</c> comments, double-quoted
/// strings with escapes, <c>IL_xxxx</c> labels, <c>.directives</c> (<c>.ctor</c>/<c>.cctor</c> as names),
/// every <see cref="System.Reflection.Emit.OpCodes"/> mnemonic, primitive types and <c>native int</c>,
/// <c>[assembly]</c> references, <c>!0</c>/<c>!!0</c> generic parameters, declaration and calling-convention
/// keywords, hexadecimal/decimal/floating-point numbers and <c>'quoted'</c> names. Not covered: signature
/// grammar (type names stay unscoped), <c>.data</c> byte blobs and preprocessor-style constructs.
/// </summary>
public static class IlGrammar
{
    public const string ScopeName = "source.il";

    private const string ResourceName = "Invoklet.Avalonia.CodeTextEditor.Highlighting.il.tmLanguage.json";

    public static IRawGrammar Load()
    {
        using var stream = typeof(IlGrammar).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The embedded resource '{ResourceName}' is missing.");
        using var reader = new StreamReader(stream);
        return GrammarReader.ReadGrammarSync(reader);
    }
}

/// <summary>
/// The bundled TextMate registry plus <see cref="IlGrammar"/>: every other scope and every theme come from
/// <paramref name="inner"/>, so the IL grammar follows the editor's theme like any other language.
/// </summary>
public sealed class IlAwareRegistryOptions(RegistryOptions inner) : IRegistryOptions
{
    public RegistryOptions Inner { get; } = inner;

    public IRawGrammar GetGrammar(string scopeName) =>
        scopeName == IlGrammar.ScopeName ? IlGrammar.Load() : Inner.GetGrammar(scopeName);

    public ICollection<string> GetInjections(string scopeName) =>
        scopeName == IlGrammar.ScopeName ? [] : Inner.GetInjections(scopeName);

    public IRawTheme GetTheme(string scopeName) => Inner.GetTheme(scopeName);

    public IRawTheme GetDefaultTheme() => Inner.GetDefaultTheme();
}

/// <summary>
/// IL highlighting for a plain <see cref="TextEditor"/> (a read-only IL view, not a <see cref="CodeTextEditor"/>).
/// Install it once the editor is in the visual tree: TextMate redraws the text view as it installs.
/// </summary>
public sealed class IlHighlighting : IDisposable
{
    private readonly RegistryOptions _registry;
    private readonly TextMate.Installation _installation;

    private IlHighlighting(TextEditor editor, ThemeName theme)
    {
        _registry = new RegistryOptions(theme);
        _installation = editor.InstallTextMate(new IlAwareRegistryOptions(_registry));
        _installation.SetGrammar(IlGrammar.ScopeName);
    }

    public static IlHighlighting Install(TextEditor editor, ThemeName theme) => new(editor, theme);

    public string GrammarScope => IlGrammar.ScopeName;

    public void SetTheme(ThemeName theme) => _installation.SetTheme(_registry.LoadTheme(theme));

    internal TextMate.Installation Installation => _installation;

    public void Dispose() => _installation.Dispose();
}
