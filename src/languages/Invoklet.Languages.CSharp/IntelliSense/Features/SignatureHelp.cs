using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using Invoklet.Contracts.Connections;
using Invoklet.Contracts.IntelliSense;
using Invoklet.Format.MdRun;
using Invoklet.Languages.CSharp.Compiler;
using Invoklet.Languages.DotnetCommon;
using Invoklet.Languages.DotnetCommon.Compiler;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using NuGet.Configuration;
using ContractSeverity = Invoklet.Contracts.IntelliSense.DiagnosticSeverity;
using ExecutionRequest = Invoklet.Contracts.Models.ExecutionRequest;
using RoslynSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace Invoklet.Languages.CSharp.IntelliSense;

/// <summary>The parameter hints of a call: which overloads fit the arguments written so far and which parameter the caret is on.</summary>
internal sealed class SignatureHelp(Func<string, Document> syncDocument, ScriptDocumentLayout layout, ILogger logger)
{
    private readonly Func<string, Document> _syncDocument = syncDocument;
    private readonly ScriptDocumentLayout _layout = layout;
    private readonly ILogger _logger = logger;

    internal Task<ISignatureHelpResult?> GetSignatureHelpAsync(
        string code, int position, char? triggerCharacter = null, CancellationToken ct = default)
        => RoslynMessageCulture.RunAsync(async () => (ISignatureHelpResult?)await TryGetSignatureHelpAsync(code, position, ct), ct);

    private async Task<ISignatureHelpResult?> TryGetSignatureHelpAsync(string code, int position, CancellationToken ct)
    {
        try
        {
            var document = _syncDocument(code);
            var model = await document.GetSemanticModelAsync(ct);
            if (model == null) return null;

            var root = await document.GetSyntaxRootAsync(ct);
            if (root == null) return null;

            var pos = _layout.ToDocumentOffset(code, position);
            if (SourcePositions.IsInsideComment(root, pos) || SourcePositions.IsInsideStringOrCharLiteral(root, pos)) return null;

            var node = root.FindNode(new TextSpan(Math.Max(0, pos - 1), 1), getInnermostNodeForTie: true);

            // The innermost call whose parentheses the caret is between: in `Outer(new Inner(|` that
            // is Inner, and once Inner's `)` is typed, Outer again. "new Foo(" (and the target-typed
            // `new(`) behaves like a method call.
            var (callLike, argumentList) = node.AncestorsAndSelf()
                .Select(n => (Call: n, List: n switch
                {
                    InvocationExpressionSyntax inv => inv.ArgumentList,
                    BaseObjectCreationExpressionSyntax oce => oce.ArgumentList,
                    _ => null,
                }))
                .FirstOrDefault(c => c.List is not null && SourcePositions.IsInsideArgumentList(c.List, pos));
            if (callLike == null || argumentList == null) return null;

            var argIndex = SourcePositions.CountArgumentsBefore(argumentList, pos);
            var argCount = argumentList.Arguments.Count;
            var isArgumentSlotEmpty = SourcePositions.IsArgumentSlotEmpty(argumentList, argIndex, pos);
            var argumentName = argIndex < argCount ? argumentList.Arguments[argIndex].NameColon?.Name.Identifier.ValueText : null;

            var methods = SourcePositions.GetCandidateMethods(model, callLike);

            if (methods.Count == 0) return null;

            var items = methods
                .Select(m => (ISignatureHelpItem)new SimpleSignatureHelpItem(m, ActiveParameter(m, argIndex, argumentName)))
                .ToList();
            var selectedIndex = SelectBestOverloadIndex(methods, argumentList, argCount, argumentName, model);

            return new SimpleSignatureHelpResult(items, selectedIndex, argIndex, isArgumentSlotEmpty);
        }
        catch (OperationCanceledException) { throw; } // see the Completions catch above
        catch (Exception ex) { _logger.LogWarning(ex, "Signature help failed"); return null; }
    }

    // Picking the highlighted overload by argument COUNT alone means e.g.
    // Foo(1, "x") highlights Foo(int, int) over Foo(int, string) whenever the int-int overload sorts
    // first — the arg count matches either way, but only one of them actually type-checks. This is a
    // lightweight approximation of real overload resolution, not a reimplementation of it: it prefers
    // the first candidate where every already-typed argument has an implicit (or identity) conversion
    // to the corresponding parameter, and falls back to the pre-existing count-only match — unchanged
    // — for anything that approximation can't confidently call (params/named/optional arguments,
    // nothing typed yet, or no candidate type-checks perfectly).
    // A named argument is checked against the parameter of its name, and an overload without that
    // name doesn't fit.
    private static int SelectBestOverloadIndex(
        List<IMethodSymbol> methods, ArgumentListSyntax argumentList, int argCount, string? argumentName, SemanticModel model)
    {
        var typeAware = methods.FindIndex(m =>
        {
            if (m.Parameters.Length < argCount) return false;
            if (argumentName is not null && m.Parameters.All(p => p.Name != argumentName)) return false;
            for (var i = 0; i < argCount; i++)
            {
                var argument = argumentList.Arguments[i];
                var parameter = argument.NameColon is { } nameColon
                    ? m.Parameters.FirstOrDefault(p => p.Name == nameColon.Name.Identifier.ValueText)
                    : m.Parameters[i];
                if (parameter == null) return false;
                var argType = model.GetTypeInfo(argument.Expression).ConvertedType;
                if (argType == null) continue; // couldn't infer (e.g. still typing) — don't penalize
                if (!model.Compilation.ClassifyConversion(argType, parameter.Type).IsImplicit)
                    return false;
            }
            return true;
        });
        if (typeAware >= 0) return typeAware;

        var arityOnly = methods.FindIndex(m => m.Parameters.Length >= argCount
            && (argumentName is null || m.Parameters.Any(p => p.Name == argumentName)));
        return arityOnly >= 0 ? arityOnly : methods.Count - 1;
    }

    // The parameter the argument at the caret goes to: the one it names, the one at its position,
    // or a `params` tail past the last.
    private static IParameterSymbol? ActiveParameter(IMethodSymbol method, int argumentIndex, string? argumentName)
    {
        if (argumentName is not null) return method.Parameters.FirstOrDefault(p => p.Name == argumentName);
        if (method.Parameters.Length == 0) return null;
        if (argumentIndex < method.Parameters.Length) return method.Parameters[argumentIndex];
        var last = method.Parameters[^1];
        return last.IsParams ? last : null;
    }

    // The caret sitting inside the NAME of a fresh declaration (a method/class/
    // property/variable/parameter the user is currently naming, e.g. "void Mai|" or "private |") isn't
    // an expression position — LookupSymbols/GetSymbolInfo don't distinguish "referencing an existing
    // symbol" from "declaring a brand-new one", so without this guard the editor would happily offer
    // (and auto-commit on the very next keystroke) some unrelated in-scope symbol that merely shares a
    // prefix with the name being typed, clobbering it. Returning null here means IsCommitCharacter is
    // safe by construction — there's simply no completion window to close over the declared name.

    private sealed class SimpleSignatureHelpResult(
        IReadOnlyList<ISignatureHelpItem> items, int selectedIndex, int argumentIndex, bool isArgumentSlotEmpty)
        : ISignatureHelpResult
    {
        public IReadOnlyList<ISignatureHelpItem> Items => items;
        public int SelectedItemIndex => selectedIndex;
        public int ArgumentIndex => argumentIndex;
        public bool IsArgumentSlotEmpty => isArgumentSlotEmpty;
    }

    private sealed class SimpleSignatureHelpItem : ISignatureHelpItem
    {
        // activeParameter is bolded in the header, the way LINQPad/VS Code do.
        public SimpleSignatureHelpItem(IMethodSymbol method, IParameterSymbol? activeParameter)
        {
            Signature = method.ToDisplayParts(SymbolFormats.Brief).ToSegments(activeParameter);
            Summary = XmlDocSummary.GetSummary(method);
            Parameters = method.Parameters
                .Select(p => (ISignatureHelpParameter)new SimpleSignatureHelpParameter(method, p))
                .ToList();
            ActiveParameterIndex = activeParameter is null ? -1 : method.Parameters.IndexOf(activeParameter);
        }

        public IReadOnlyList<DisplayTextSegment> Signature { get; }
        public string? Summary { get; }
        public IReadOnlyList<ISignatureHelpParameter> Parameters { get; }
        public int ActiveParameterIndex { get; }
    }

    private sealed class SimpleSignatureHelpParameter : ISignatureHelpParameter
    {
        public SimpleSignatureHelpParameter(IMethodSymbol method, IParameterSymbol parameter)
        {
            Name = parameter.Name;
            Signature = parameter.ToDisplayParts(SymbolFormats.Brief).ToSegments();
            Summary = XmlDocSummary.GetParameterSummary(method, parameter.Name);
        }

        public string Name { get; }
        public IReadOnlyList<DisplayTextSegment> Signature { get; }
        public string? Summary { get; }
    }
}
