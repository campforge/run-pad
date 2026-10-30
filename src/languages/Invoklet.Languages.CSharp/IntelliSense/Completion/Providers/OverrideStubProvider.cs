using static Invoklet.Languages.CSharp.IntelliSense.Completion.CompletionItems;
using static Invoklet.Languages.CSharp.IntelliSense.Completion.OverrideStubs;

namespace Invoklet.Languages.CSharp.IntelliSense.Completion.Providers;

// "override " lists the base members still open to override, each inserted as a whole stub.
// Checked before the syntax guards: the keyword can only be a modifier, so the incomplete
// declaration after it does not need to parse as anything in particular.
internal sealed class OverrideStubProvider : ICompletionProvider
{
    public SimpleCompletionList? Provide(CompletionRequest request)
    {
        if (!IsAfterOverrideModifier(request.Code, request.Position)) return null;

        var type = GetEnclosingType(request.Model, request.Offset);
        var items = type?.BaseType is null
            ? []
            : GetOverridableMethods(type)
                .Select(m => BuildOverrideStubItem(m, request.Model, request.Offset))
                .OrderBy(i => i.SortText, StringComparer.Ordinal)
                .ToList();
        return new SimpleCompletionList(items, CompletionContextKind.OverrideStub);
    }
}
