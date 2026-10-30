using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Invoklet.Languages.CSharp;
using Invoklet.UserControls;
using Invoklet.UserControls.Models;
using Invoklet.Windows;
using Invoklet.Windows.Models;
using Xunit;

namespace Invoklet.NonUI.Tests;

// The editor tabs and the language menus get what they need from the language modules through
// contracts, so none of these consumers' constructors, fields or method signatures may name a type
// from a concrete language assembly. Registering those modules is the composition root's job (App).
public class LanguageBoundaryConsumerTests
{
    private static readonly string[] LanguageAssemblies = ["Invoklet.Languages.CSharp", "Invoklet.Languages.VB"];

    [Theory]
    [InlineData(typeof(EditorsTabItemControl))]
    [InlineData(typeof(EditorTabsControl))]
    [InlineData(typeof(MainWindow))]
    [InlineData(typeof(MainWindowViewModel))]
    [InlineData(typeof(FileTreeView))]
    [InlineData(typeof(FileTreeViewModel))]
    [InlineData(typeof(PreferencesWindowViewModel))]
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
                "LeakyConsumer._references: Invoklet.Languages.CSharp.CSharpReferenceService",
                "LeakyConsumer.references: System.Func`1[Invoklet.Languages.CSharp.CSharpReferenceService]",
            ],
            LanguageTypesIn(typeof(LeakyConsumer)));
    }

    // Running a script only asks the language for a host with a plain ExecutionRequest: nothing in the
    // app — tab, window or composition — builds the .NET backend's host options or resolves
    // references itself, not even inside a method body, so the check reads the assembly's references.
    [Fact]
    public void TheApp_Should_NotBuildTheBackendsHostOptions()
    {
        Assert.Empty(BackendTypesReferencedBy(typeof(EditorTabsControl).Assembly));
    }

    internal static readonly string[] BackendExecutionTypes =
    [
        "Invoklet.Languages.DotnetCommon.CompiledDotnetLanguageHostOptions",
        "Invoklet.Languages.DotnetCommon.ReferencePathResolver",
        "Invoklet.Build.Core.Models.ExecutionOptions",
        "Invoklet.Build.Core.Models.RuntimeOptions",
        "Invoklet.Build.Core.Models.DebuggerOptions",
    ];

    internal static IEnumerable<string> BackendTypesReferencedBy(Assembly assembly)
    {
        using var stream = File.OpenRead(assembly.Location);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        return metadata.TypeReferences
            .Select(h => metadata.GetTypeReference(h))
            .Select(t => metadata.GetString(t.Namespace) + "." + metadata.GetString(t.Name))
            .Where(BackendExecutionTypes.Contains)
            .Distinct()
            .ToList();
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
