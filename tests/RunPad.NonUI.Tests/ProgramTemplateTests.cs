using Microsoft.Extensions.Logging.Abstractions;
using RunPad.Format.RPad;
using RunPad.Languages.CSharp;
using RunPad.Languages.VB;
using Xunit;

namespace RunPad.NonUI.Tests;

// What a script's code becomes when it switches to the Program kind (ILanguageHostFactory.CodeForKind).
public class ProgramTemplateTests
{
    private static readonly CSharpLanguageHostFactory CSharp = new(NullLoggerFactory.Instance);
    private static readonly VbLanguageHostFactory Vb = new(NullLoggerFactory.Instance);

    [Fact]
    public void CSharp_AnEmptyScript_Should_GetAMainSkeleton_WithTheCaretInside()
    {
        var code = CSharp.CodeForKind("", RpadKind.Program);

        Assert.Equal(
            "void Main()\n{\n    ${0}\n}\n\n// Other methods, fields and types used by Main go here.\n",
            code);
    }

    [Fact]
    public void CSharp_Statements_Should_BeWrappedInMain_AndDeclarationsStayOutside()
    {
        const string code = """
            using System.Text;

            var sb = new StringBuilder();
            sb.Append(Twice(21));

            Console.WriteLine(sb);

            int Twice(int x) => x * 2;

            record Point(int X, int Y);
            """;

        var result = CSharp.CodeForKind(code.Replace("\r\n", "\n"), RpadKind.Program);

        Assert.Equal("""
            using System.Text;

            void Main()
            {
                ${0}var sb = new StringBuilder();
                sb.Append(Twice(21));

                Console.WriteLine(sb);
            }

            int Twice(int x) => x * 2;

            record Point(int X, int Y);

            """.Replace("\r\n", "\n"), result);
    }

    [Fact]
    public void CSharp_AnAwaitInTheStatements_Should_MakeMainAsync()
    {
        var result = CSharp.CodeForKind("await Task.Delay(10);\n", RpadKind.Program);

        Assert.StartsWith("async Task Main()\n{\n    ${0}await Task.Delay(10);\n}\n", result);
    }

    [Fact]
    public void CSharp_AnAwaitOnlyInsideALambda_Should_NotMakeMainAsync()
    {
        var result = CSharp.CodeForKind("Func<Task> f = async () => await Task.Delay(10);\n", RpadKind.Program);

        Assert.StartsWith("void Main()\n", result);
    }

    [Fact]
    public void CSharp_OnlyDeclarations_Should_GetAnEmptyMainBeforeThem()
    {
        var result = CSharp.CodeForKind("class Helper { }\n", RpadKind.Program);

        Assert.Equal("void Main()\n{\n    ${0}\n}\n\nclass Helper { }\n", result);
    }

    [Theory]
    [InlineData("void Main()\n{\n}\n", RpadKind.Program)]       // already has Main
    [InlineData("static async Task Main() { }\n", RpadKind.Program)]
    [InlineData("Console.WriteLine(1);\n", RpadKind.Statements)] // only Program gets a template
    [InlineData("Console.WriteLine(1);\n", RpadKind.Expression)]
    public void CSharp_CodeThatAlreadyFits_Should_StayAsItIs(string code, RpadKind kind)
        => Assert.Null(CSharp.CodeForKind(code, kind));

    // The result is a valid Program script: the entry point is found where RpadKindTransform looks.
    [Fact]
    public void CSharp_TheResult_Should_HaveAMainTheProgramTransformFinds()
    {
        var result = CSharp.CodeForKind("Console.WriteLine(1);\n", RpadKind.Program)!.Replace("${0}", "");

        Assert.Null(CSharp.CodeForKind(result, RpadKind.Program));
    }

    [Fact]
    public void Vb_AnEmptyScript_Should_GetAModuleWithSubMain()
    {
        var code = Vb.CodeForKind("", RpadKind.Program);

        Assert.Equal(
            "Module Program\n    Sub Main()\n        ${0}\n    End Sub\n\n    ' Other methods and fields used by Main go here.\nEnd Module\n",
            code);
    }

    [Fact]
    public void Vb_Statements_Should_BeWrappedInSubMain()
    {
        var code = Vb.CodeForKind("Dim x = 1\nConsole.WriteLine(x)\n", RpadKind.Program);

        Assert.StartsWith("Module Program\n    Sub Main()\n        ${0}Dim x = 1\n        Console.WriteLine(x)\n    End Sub\n", code);
    }

    [Theory]
    [InlineData("Module Program\nSub Main()\nEnd Sub\nEnd Module\n")]
    [InlineData("public module M\nEnd Module\n")]
    [InlineData("Class C\nEnd Class\n")]
    public void Vb_AModuleOrClass_Should_StayAsItIs(string code)
        => Assert.Null(Vb.CodeForKind(code, RpadKind.Program));

    // A classic program already has its entry point; switching to Program must not wrap it in Main.
    [Fact]
    public void CSharp_AClassWithAStaticMain_Should_StayAsWritten()
    {
        Assert.Null(CSharp.CodeForKind("using System;\n\nclass Program\n{\n    static void Main() => Console.WriteLine(1);\n}\n", RpadKind.Program));
    }

    // A class without a static Main is not an entry point: it still gets a Main skeleton.
    [Fact]
    public void CSharp_AClassWithoutAStaticMain_Should_StillGetMain()
    {
        Assert.Contains("void Main()", CSharp.CodeForKind("class Helper\n{\n    void Main() { }\n}\n", RpadKind.Program));
    }
}
