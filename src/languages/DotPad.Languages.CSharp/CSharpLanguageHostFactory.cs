using DotPad.Contracts;
using Microsoft.Extensions.Logging;

namespace DotPad.Languages.CSharp;

public sealed class CSharpLanguageHostFactory : ILanguageHostFactory
{
    private readonly ILoggerFactory _loggerFactory;

    public CSharpLanguageHostFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
    }

    public string Language => "csharp";

    public LanguageHostCapabilities Capabilities =>
        LanguageHostCapabilities.Run |
        LanguageHostCapabilities.Debug |
        LanguageHostCapabilities.Breakpoints |
        LanguageHostCapabilities.VariableInspection |
        LanguageHostCapabilities.MemoryDump;

    public IReadOnlyDictionary<string, string> Snippets { get; } = new Dictionary<string, string>
    {
        ["for"] = "for (int i = 0; i < ${0}; i++)\n{\n\t\n}",
        ["foreach"] = "foreach (var item in ${0})\n{\n\t\n}",
        ["while"] = "while (${0})\n{\n\t\n}",
        ["do"] = "do\n{\n\t${0}\n} while ();",
        ["if"] = "if (${0})\n{\n\t\n}",
        ["ife"] = "if (${0})\n{\n\t\n}\nelse\n{\n\t\n}",
        ["try"] = "try\n{\n\t${0}\n}\ncatch (Exception ex)\n{\n\t\n}",
        ["tryf"] = "try\n{\n\t${0}\n}\nfinally\n{\n\t\n}",
        ["sw"] = "switch (${0})\n{\n\tcase :\n\t\tbreak;\n\tdefault:\n\t\tbreak;\n}",
        ["prop"] = "public ${0} Property { get; set; }",
        ["propg"] = "public ${0} Property { get; private set; }",
        ["class"] = "class ${0}\n{\n\t\n}",
        ["interface"] = "interface I${0}\n{\n\t\n}",
        ["ctor"] = "public ${0}()\n{\n\t\n}",
        ["using"] = "using (var resource = ${0})\n{\n\t\n}",
        ["lock"] = "lock (${0})\n{\n\t\n}",
    };

    public EditorLanguageProfile EditorProfile { get; } = new()
    {
        CompletionTriggers = new HashSet<char> { '.' },
        OverloadTriggers = new HashSet<char> { '(', ',' },
        IdentifierChars = new HashSet<char> { '_' },
        ContextualTriggers = new HashSet<char> { '<' }, // generic type arguments, e.g. "List<"
        WordTriggers = new HashSet<string> { "new" }, // constructible-type completion after "new "
        StatementTerminators = new HashSet<char> { ';' },
        BracePairs = new Dictionary<char, char> { ['('] = ')', ['['] = ']', ['{'] = '}' },
        // '(' deliberately excluded — see DotPad.Avalonia.CodeTextEditor.CodeTextEditor.AutoCloseBraces:
        // opening a call's parameter list is exactly when signature help is about to show the real
        // parameter list, and an auto-inserted ')' sitting there fights that instead of helping.
        AutoCloseBraces = new HashSet<char> { '[', '{' },
        IndentationStrategy = IndentationStrategyKind.CStyle,
    };

    public ILanguageHost Create(ILanguageHostOptions? options = null)
    {
        var opts = options as CSharpLanguageHostOptions
            ?? throw new ArgumentException(
                $"Expected {nameof(CSharpLanguageHostOptions)}.", nameof(options));
        return new CSharpLanguageHost(opts, _loggerFactory);
    }
}
