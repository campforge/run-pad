using Microsoft.Extensions.Logging;
using RunPad.Contracts;
using RunPad.Format.RPad;
using RunPad.Languages.DotnetCommon;

namespace RunPad.Languages.CSharp;

public sealed class CSharpLanguageHostFactory : ILanguageHostFactory
{
    private readonly ILoggerFactory _loggerFactory;

    public CSharpLanguageHostFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
    }

    public string Language => "csharp";
    public string DisplayName => "C#";

    // See CSharpLanguageHost.SupportedCapabilities — single source of truth.
    public LanguageHostCapabilities Capabilities => CSharpLanguageHost.SupportedCapabilities;

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

    public string? CodeForKind(string code, RpadKind kind) => ProgramTemplate.CodeForKind(code, kind);

    public EditorLanguageProfile EditorProfile { get; } = new()
    {
        CompletionTriggers = new HashSet<char> { '.' },
        OverloadTriggers = new HashSet<char> { '(', ',' },
        IdentifierChars = new HashSet<char> { '_' },
        ContextualTriggers = new HashSet<char> { '<' }, // generic type arguments, e.g. "List<"
        // "new " — constructible-type completion; "override " — overridable-member completion with
        // stub generation.
        WordTriggers = new HashSet<string> { "new", "override" },
        StatementTerminators = new HashSet<char> { ';' },
        BracePairs = new Dictionary<char, char> { ['('] = ')', ['['] = ']', ['{'] = '}' },
        // '(' deliberately excluded — see RunPad.Avalonia.CodeTextEditor.CodeTextEditor.AutoCloseBraces:
        // opening a call's parameter list is exactly when signature help is about to show the real
        // parameter list, and an auto-inserted ')' sitting there fights that instead of helping.
        AutoCloseBraces = new HashSet<char> { '[', '{' },
        IndentationStrategy = IndentationStrategyKind.CStyle,
        // File tree's per-language icon — pictogrammers.com/library/mdi/icon/language-csharp.
        IconResourceKey = "Icon.LanguageCSharp",
    };

    public ILanguageHost Create(ILanguageHostOptions? options = null)
    {
        var opts = options as CompiledDotnetLanguageHostOptions
            ?? throw new ArgumentException(
                $"Expected {nameof(CompiledDotnetLanguageHostOptions)}.", nameof(options));
        return new CSharpLanguageHost(opts, _loggerFactory);
    }
}
