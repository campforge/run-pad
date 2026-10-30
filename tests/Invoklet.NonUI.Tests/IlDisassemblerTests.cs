using Invoklet.Languages.DotnetCommon;
using Xunit;

namespace Invoklet.NonUI.Tests;

// The "IL" output tab (mirrors LINQPad's IL view). Real-Roslyn integration test (no
// mocking of the compiler): the thing under test is whether ICSharpCode.Decompiler can actually read
// a REAL compiled binary, which pure-logic unit testing can't stand in for. Compiled via
// InProcessFixtureCompiler (in-process Roslyn CSharpCompilation.Emit, see its own doc comment) rather
// than a real `dotnet build` child process — same real IL bytes, no MSBuild/dotnet CLI involved.
public class IlDisassemblerTests
{
    [Fact]
    public void Disassemble_Should_ProduceIlText_ContainingACompiledMethodByName()
    {
        const string code = "IlDisassemblerProbe.ThisNameMustAppearInTheIl();\n"
                             + "class IlDisassemblerProbe { public static void ThisNameMustAppearInTheIl() { System.Console.WriteLine(1); } }\n";
        var binary = InProcessFixtureCompiler.CompileToTempDll(code, "IlDisassemblerFixture");
        try
        {
            var il = IlDisassembler.Disassemble(binary);

            Assert.Contains("ThisNameMustAppearInTheIl", il);
            Assert.Contains("IlDisassemblerProbe", il);
        }
        finally { File.Delete(binary); }
    }
}
