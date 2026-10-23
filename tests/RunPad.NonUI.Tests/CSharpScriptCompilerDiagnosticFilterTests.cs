using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RunPad.Languages.CSharp;
using RunPad.Languages.CSharp.Compiler;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// Proves CSharpScriptCompiler.IsSyntheticWrapperEntryPointWarning's structural check in isolation,
// against a tiny in-memory compilation, so it doesn't need a real `dotnet build` of a whole script
// (CSharpLanguageHostProgramKindCompileTests.
// CompileAsync_Should_Succeed_ForAStaticMain_WithAPrivateHelperMethod covers the full pipeline, including
// the build, but is Category=RealBuild and only exercises this through it).
public class CSharpScriptCompilerDiagnosticFilterTests
{
    private static Diagnostic[] DiagnosticsFor(string code, string id)
    {
        var tree = CSharpSyntaxTree.ParseText(code, CSharpScriptCompiler.ParseOptions(10, 0),
            cancellationToken: TestContext.Current.CancellationToken);
        var compilation = CSharpCompilation.Create("test", [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.ConsoleApplication));
        return [.. compilation.GetDiagnostics(TestContext.Current.CancellationToken).Where(d => d.Id == id)];
    }

    private static Diagnostic[] Cs7022DiagnosticsFor(string code) => DiagnosticsFor(code, "CS7022");

    // Exactly RpadKindTransform's own shape: a top-level call statement glued in front of the wrapper
    // class, a static Main inside it, calling the trampoline that actually invokes it.
    [Fact]
    public void IsSyntheticWrapperEntryPointWarning_Should_BeTrue_ForAStaticMainInsideTheGeneratedWrapperClass()
    {
        var code = $$"""
            {{RpadKindTransform.ProgramWrapperClassName}}.__RunPadRun(args);

            class {{RpadKindTransform.ProgramWrapperClassName}}
            {
                static void Main()
                {
                }

                internal static void __RunPadRun(string[] args) { Main(); }
            }
            """;

        var diagnostic = Assert.Single(Cs7022DiagnosticsFor(code));
        Assert.True(CSharpScriptCompiler.IsSyntheticWrapperEntryPointWarning(diagnostic));
    }

    // A genuine conflict unrelated to RunPad's own transform (no wrapper class involved at all) must
    // still surface — the filter is tied to the generated wrapper class, not to the diagnostic ID alone.
    [Fact]
    public void IsSyntheticWrapperEntryPointWarning_Should_BeFalse_ForAUserOwnStaticMain_OutsideAnyWrapperClass()
    {
        var code = """
            System.Console.WriteLine("top-level");

            class UserProgram
            {
                static void Main()
                {
                }
            }
            """;

        var diagnostic = Assert.Single(Cs7022DiagnosticsFor(code));
        Assert.False(CSharpScriptCompiler.IsSyntheticWrapperEntryPointWarning(diagnostic));
    }

    // Same shape as RunPad's own wrapper, but under a different class name: a user script could, in
    // principle, declare its own class with a static Main of its own, with no connection to the
    // generated wrapper RpadKindTransform produces. Confirms the check keys on the exact wrapper class
    // name, not merely "any class containing a static Main that conflicts with top-level code".
    [Fact]
    public void IsSyntheticWrapperEntryPointWarning_Should_BeFalse_ForAStaticMainInsideADifferentlyNamedClass()
    {
        const string code = """
            System.Console.WriteLine("top-level");

            class NotTheWrapper
            {
                static void Main()
                {
                }
            }
            """;

        var diagnostic = Assert.Single(Cs7022DiagnosticsFor(code));
        Assert.False(CSharpScriptCompiler.IsSyntheticWrapperEntryPointWarning(diagnostic));
    }

    // The reserved-name preflight normally rejects this user declaration before compilation, but the
    // filter must still be safe on its own: a same-named class without RunPad's synthetic trampoline
    // is user code, not the generated wrapper.
    [Fact]
    public void IsSyntheticWrapperEntryPointWarning_Should_BeFalse_ForSameNamedUserClassWithoutTrampoline()
    {
        var code = $$"""
            System.Console.WriteLine("top-level");

            class {{RpadKindTransform.ProgramWrapperClassName}}
            {
                static void Main()
                {
                }
            }
            """;

        var diagnostic = Assert.Single(Cs7022DiagnosticsFor(code));
        Assert.False(CSharpScriptCompiler.IsSyntheticWrapperEntryPointWarning(diagnostic));
    }

    [Fact]
    public void IsSyntheticWrapperEntryPointWarning_Should_BeFalse_ForADifferentDiagnosticId()
    {
        var code = $$"""
            {{RpadKindTransform.ProgramWrapperClassName}}.__RunPadRun(args);

            class {{RpadKindTransform.ProgramWrapperClassName}}
            {
                static void Main()
                {
                    int unused = 1;
                }

                internal static void __RunPadRun(string[] args) { Main(); }
            }
            """;

        var unusedVariable = Assert.Single(DiagnosticsFor(code, "CS0219"));
        Assert.False(CSharpScriptCompiler.IsSyntheticWrapperEntryPointWarning(unusedVariable));
    }
}
