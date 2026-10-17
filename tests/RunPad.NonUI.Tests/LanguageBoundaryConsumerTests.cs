using System.Reflection;
using RunPad.Languages.CSharp;
using RunPad.UserControls;
using Xunit;

namespace RunPad.NonUI.Tests;

// The editor tabs get what they need from the language modules through contracts, so neither tab
// control's constructor, fields or method signatures may name a type from a concrete language
// assembly. Registering those modules is the composition root's job (App).
public class LanguageBoundaryConsumerTests
{
    private static readonly string[] LanguageAssemblies = ["RunPad.Languages.CSharp", "RunPad.Languages.VB"];

    [Theory]
    [InlineData(typeof(EditorsTabItemControl))]
    [InlineData(typeof(EditorTabsControl))]
    public void TheTabControls_Should_NotDependOnAConcreteLanguage(Type type)
    {
        Assert.Empty(LanguageTypesIn(type));
    }

    // What the check is for: the tabs used to take, store and call the C# reference service.
    [Fact]
    public void TheCheck_Should_FlagAConsumerOfAConcreteLanguageService()
    {
        Assert.Equal(
            [
                "LeakyConsumer._references: RunPad.Languages.CSharp.CSharpReferenceService",
                "LeakyConsumer.references: System.Func`1[RunPad.Languages.CSharp.CSharpReferenceService]",
            ],
            LanguageTypesIn(typeof(LeakyConsumer)));
    }

    internal static IEnumerable<string> LanguageTypesIn(Type type)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                                 | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var types = type.GetFields(all).Select(f => (f.Name, f.FieldType))
            .Concat(type.GetConstructors(all).SelectMany(c => c.GetParameters()).Select(p => (p.Name ?? "", p.ParameterType)))
            .Concat(type.GetMethods(all).SelectMany(m => m.GetParameters().Select(p => (m.Name + "(" + p.Name + ")", p.ParameterType))
                .Append((m.Name, m.ReturnType))))
            .Concat(type.GetProperties(all).Select(p => (p.Name, p.PropertyType)));
        return types
            .Where(t => Expand(t.Item2).Any(x => LanguageAssemblies.Contains(x.Assembly.GetName().Name)))
            .Select(t => $"{type.Name}.{t.Item1}: {t.Item2}");
    }

    // Generic arguments and element types too: Func<CSharpReferenceService> leaks just the same.
    private static IEnumerable<Type> Expand(Type type)
    {
        yield return type;
        if (type.HasElementType)
            foreach (var t in Expand(type.GetElementType()!)) yield return t;
        if (type.IsGenericType)
            foreach (var argument in type.GetGenericArguments())
                foreach (var t in Expand(argument)) yield return t;
    }

    private sealed class LeakyConsumer(Func<CSharpReferenceService> references)
    {
        private readonly CSharpReferenceService _references = references();

        public override string ToString() => _references.GetExtensionReference();
    }
}
