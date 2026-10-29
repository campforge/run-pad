using Invoklet.Languages.CSharp.IntelliSense;
using Microsoft.Extensions.Logging;
using Invoklet.Contracts;
using Invoklet.Contracts.IntelliSense;
using Invoklet.Contracts.Models;
using Invoklet.Format.RPad;
using Invoklet.Languages.DotnetCommon;

namespace Invoklet.Languages.CSharp;

public sealed class CSharpLanguageHostFactory : ILanguageHostFactory
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly IRuntimeReferenceLocator _runtimeReferences;
    private readonly CSharpReferenceService _references = new();

    public CSharpLanguageHostFactory(ILoggerFactory loggerFactory, IRuntimeReferenceLocator? runtimeReferences = null)
    {
        _loggerFactory = loggerFactory;
        _runtimeReferences = runtimeReferences ?? new DotnetRuntimeReferenceLocator();
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
        // ${1:…} fields: Tab moves from the type to the name; Enter leaves the snippet.
        ["prop"] = "public ${1:int} ${2:Property} { get; set; }${0}",
        ["propg"] = "public ${1:int} ${2:Property} { get; private set; }${0}",
        ["class"] = "class ${0}\n{\n\t\n}",
        ["interface"] = "interface I${0}\n{\n\t\n}",
        ["ctor"] = "public ${0}()\n{\n\t\n}",
        ["using"] = "using (var resource = ${0})\n{\n\t\n}",
        ["lock"] = "lock (${0})\n{\n\t\n}",
    };

    public string? CodeForKind(string code, RpadKind kind, string indent = "    ") => ProgramTemplate.CodeForKind(code, kind, indent);

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
        // '(' deliberately excluded — see Invoklet.Avalonia.CodeTextEditor.CodeTextEditor.AutoCloseBraces:
        // opening a call's parameter list is exactly when signature help is about to show the real
        // parameter list, and an auto-inserted ')' sitting there fights that instead of helping.
        // '{' too, by user request: a block's closing brace is typed, not inserted; in a new type body
        // the editor offers a property template instead (MemberTemplateSnippet).
        AutoCloseBraces = new HashSet<char> { '[' },
        MemberTemplateSnippet = "prop",
        MemberTemplateTitle = "Insert property",
        LineCommentPrefix = "//",
        IndentationStrategy = IndentationStrategyKind.CStyle,
        // File tree's per-language icon — pictogrammers.com/library/mdi/icon/language-csharp.
        IconResourceKey = "Icon.LanguageCSharp",
        SourceFileExtension = CSharpProjectProfile.Instance.SourceFileExtension,
    };

    // The framework's assemblies and, given apart so that a target's reference assemblies never
    // replace it, Invoklet.Runtime: a self-contained Invoklet ships it in the framework's own folder.
    public IIntelliSenseService? CreateIntelliSense(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var framework = _references.GetDefault();
        cancellationToken.ThrowIfCancellationRequested();
        return new CSharpIntelliSenseService(framework, _loggerFactory.CreateLogger<CSharpIntelliSenseService>(),
            additionalReferencePaths: [_runtimeReferences.GetRuntimeAssemblyPath()]);
    }

    // A throwaway instance: the expensive part (InvokletRoslynHost.HostServices, the MEF composition) is a
    // process-wide lazy singleton every instance shares, and its first analysis loads the references.
    public void WarmUpIntelliSense() => CreateIntelliSense(CancellationToken.None)?.NotifyTextChanged("");

    public CompiledDotnetLanguageHostOptions CreateOptions(ExecutionRequest request)
        => CompiledDotnetLanguageHostOptions.From(request, _runtimeReferences.GetRuntimeAssemblyPath());

    public ILanguageHost Create(ExecutionRequest request) => Create(CreateOptions(request));

    public ILanguageHost Create(ILanguageHostOptions? options = null)
    {
        var opts = options as CompiledDotnetLanguageHostOptions
            ?? throw new ArgumentException(
                $"Expected {nameof(CompiledDotnetLanguageHostOptions)}.", nameof(options));
        return new CSharpLanguageHost(opts, _loggerFactory);
    }
}
