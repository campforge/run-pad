using Invoklet.Contracts.IntelliSense;
using Invoklet.Languages.CSharp.IntelliSense.Syntax;
using Microsoft.CodeAnalysis;

namespace Invoklet.Languages.CSharp.IntelliSense.Completion;

/// <summary>The stubs offered after <c>override</c>: the base members still open to override, written out with a call to the base.</summary>
internal static class OverrideStubs
{
    // ── Override-member stubs ────────────────────────

    // True when the word immediately before position — skipping back over the trigger space itself —
    // is the whole word "override" (not e.g. "myoverride"). Deliberately a raw string scan, not a
    // syntax-tree check: at this position nothing has been typed for the member yet, so there's no
    // stable node to anchor a tree-based check to (same rationale as GetIdentifierPrefix elsewhere in
    // this file).
    internal static bool IsAfterOverrideModifier(string code, int position)
    {
        var end = Math.Clamp(position, 0, code.Length);
        while (end > 0 && code[end - 1] is ' ' or '\t') end--;

        const string keyword = "override";
        if (end < keyword.Length) return false;
        var start = end - keyword.Length;
        if (!code.AsSpan(start, keyword.Length).SequenceEqual(keyword)) return false;

        return start == 0 || !(char.IsLetterOrDigit(code[start - 1]) || code[start - 1] == '_');
    }

    // Every method in currentType's base chain that's still overridable — virtual, abstract, or a
    // non-sealed override further up (itself still a valid slot to override again) — deduplicated to
    // the most-derived occurrence of each signature (walking from BaseType upward, the first hit for
    // a given OriginalDefinition wins), and excluding anything currentType has already overridden
    // itself. A sealed override is excluded outright rather than falling through to a less-derived,
    // still-overridable declaration of the same signature — sealed really does mean "stop offering
    // this one here", not "offer the next-best thing instead".
    internal static IEnumerable<IMethodSymbol> GetOverridableMethods(INamedTypeSymbol currentType)
    {
        var alreadyOverridden = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        foreach (var own in currentType.GetMembers().OfType<IMethodSymbol>().Where(m => m.IsOverride))
            for (var overridden = own.OverriddenMethod; overridden != null; overridden = overridden.OverriddenMethod)
                alreadyOverridden.Add(overridden.OriginalDefinition);

        var seen = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        for (var type = currentType.BaseType; type != null; type = type.BaseType)
        {
            foreach (var m in type.GetMembers().OfType<IMethodSymbol>())
            {
                if (m.MethodKind != MethodKind.Ordinary) continue;
                if (!m.IsVirtual && !m.IsAbstract && !m.IsOverride) continue;
                if (m.IsSealed) continue;
                if (!seen.Add(m.OriginalDefinition)) continue;
                if (alreadyOverridden.Contains(m.OriginalDefinition)) continue;

                yield return m;
            }
        }
    }

    // An override's accessibility must match the overridden member's exactly — C# doesn't allow
    // changing it — so this only ever needs to round-trip Accessibility back to the keyword(s) that
    // produced it, not handle every possible value (private/NotApplicable can't reach here: an
    // overridable member is never private).
    internal static string AccessibilityKeyword(Accessibility accessibility) => accessibility switch
    {
        Accessibility.Public => "public",
        Accessibility.Protected => "protected",
        Accessibility.ProtectedOrInternal => "protected internal",
        Accessibility.ProtectedAndInternal => "private protected",
        Accessibility.Internal => "internal",
        _ => "protected",
    };

    internal static string FormatOverrideParameter(IParameterSymbol p, SemanticModel model, int pos)
    {
        var refPrefix = p.RefKind switch
        {
            RefKind.Ref => "ref ",
            RefKind.Out => "out ",
            RefKind.In => "in ",
            _ => "",
        };
        var paramsPrefix = p.IsParams ? "params " : "";
        return $"{paramsPrefix}{refPrefix}{p.Type.ToMinimalDisplayString(model, pos)} {p.Name}";
    }

    internal static string ForwardOverrideArgument(IParameterSymbol p) => p.RefKind switch
    {
        RefKind.Ref => $"ref {p.Name}",
        RefKind.Out => $"out {p.Name}",
        RefKind.In => $"in {p.Name}",
        _ => p.Name,
    };

    // The stub template — same "${0}" cursor-marker/tab-indent syntax CodeTextEditor.ExpandSnippet
    // already expands for the hand-authored Snippets dictionary (ILanguageHostFactory.Snippets), so
    // no new insertion mechanism is needed on the editor side. An abstract member has no base
    // implementation to call, so it gets a NotImplementedException stub instead — calling base.M(...)
    // there wouldn't compile.
    internal static ICompletionItem BuildOverrideStubItem(IMethodSymbol method, SemanticModel model, int pos)
    {
        var returnType = method.ReturnsVoid ? "void" : method.ReturnType.ToMinimalDisplayString(model, pos);
        var parameters = string.Join(
            ", ", method.Parameters.Select(p => FormatOverrideParameter(p, model, pos)));
        var arguments = string.Join(", ", method.Parameters.Select(ForwardOverrideArgument));
        var call = method.IsAbstract
            ? "throw new NotImplementedException();"
            : method.ReturnsVoid
                ? $"base.{method.Name}({arguments});"
                : $"return base.{method.Name}({arguments});";

        var template =
            $"{AccessibilityKeyword(method.DeclaredAccessibility)} override {returnType} {method.Name}({parameters})\n" +
            "{\n" +
            $"\t{call}${{0}}\n" +
            "}";

        return new SimpleCompletionItem(
            method.Name, CompletionItemKind.Method,
            method.ToDisplayParts(SymbolFormats.Brief).ToSegments(), XmlDocSummary.GetSummary(method),
            SortText: method.Name, Priority: 1.0, UsingsToAdd: [], InsertTemplate: template,
            Source: CompletionSourceKind.Template);
    }
}
