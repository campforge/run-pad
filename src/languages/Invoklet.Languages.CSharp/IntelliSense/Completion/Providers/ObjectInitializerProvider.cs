using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Invoklet.Languages.CSharp.IntelliSense.Completion.ObjectInitializerCompletions;

namespace Invoklet.Languages.CSharp.IntelliSense.Completion.Providers;

// `new Foo { X = 1, | }` lists Foo's settable members not assigned yet, not what is in scope.
// Only the direct `new Foo { }` shape: a nested initializer without its own `new` and `with { }`
// are left to the general list. A member's value (`P = |`) takes the expected-type completion.
internal sealed class ObjectInitializerProvider : ICompletionProvider
{
    public SimpleCompletionList? Provide(CompletionRequest request)
    {
        if (request.ExpectedType is not null
            || TryFindObjectInitializer(request.Root, request.Offset) is not { } initializer
            || initializer.Parent is not BaseObjectCreationExpressionSyntax objectCreation)
            return null;
        var items = BuildObjectInitializerCompletions(request.Model, request.Offset, objectCreation, initializer);
        return new SimpleCompletionList(items, CompletionContextKind.ObjectInitializerMember);
    }
}
