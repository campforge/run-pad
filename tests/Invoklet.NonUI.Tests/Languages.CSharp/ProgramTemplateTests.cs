using Invoklet.Format.MdRun;
using Invoklet.Languages.CSharp;
using Invoklet.Languages.VB;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Invoklet.NonUI.Tests.Languages.CSharp;

// What a script's code becomes when it switches to the Program kind (ILanguageHostFactory.CodeForKind).
public class ProgramTemplateTests
{
    private static readonly CSharpLanguageHostFactory CSharp = new(NullLoggerFactory.Instance);
    private static readonly VbLanguageHostFactory Vb = new(NullLoggerFactory.Instance);

    [Fact]
    public void CSharp_AnEmptyScript_Should_GetAMainSkeleton_WithTheCaretInside()
    {
        var code = CSharp.CodeForKind("", MdRunKind.Program);

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

        var result = CSharp.CodeForKind(code.Replace("\r\n", "\n"), MdRunKind.Program);

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
        var result = CSharp.CodeForKind("await Task.Delay(10);\n", MdRunKind.Program);

        Assert.StartsWith("async Task Main()\n{\n    ${0}await Task.Delay(10);\n}\n", result);
    }

    [Fact]
    public void CSharp_AnAwaitOnlyInsideALambda_Should_NotMakeMainAsync()
    {
        var result = CSharp.CodeForKind("Func<Task> f = async () => await Task.Delay(10);\n", MdRunKind.Program);

        Assert.StartsWith("void Main()\n", result);
    }

    [Fact]
    public void CSharp_OnlyDeclarations_Should_GetAnEmptyMainBeforeThem()
    {
        var result = CSharp.CodeForKind("class Helper { }\n", MdRunKind.Program);

        Assert.Equal("void Main()\n{\n    ${0}\n}\n\nclass Helper { }\n", result);
    }

    [Theory]
    [InlineData("void Main()\n{\n}\n", MdRunKind.Program)]       // already has Main
    [InlineData("static async Task Main() { }\n", MdRunKind.Program)]
    [InlineData("Console.WriteLine(1);\n", MdRunKind.Statements)] // only Program gets a template
    [InlineData("Console.WriteLine(1);\n", MdRunKind.Expression)]
    public void CSharp_CodeThatAlreadyFits_Should_StayAsItIs(string code, MdRunKind kind)
        => Assert.Null(CSharp.CodeForKind(code, kind));

    // The result is a valid Program script: the entry point is found where MdRunKindTransform looks.
    [Fact]
    public void CSharp_TheResult_Should_HaveAMainTheProgramTransformFinds()
    {
        var result = CSharp.CodeForKind("Console.WriteLine(1);\n", MdRunKind.Program)!.Replace("${0}", "");

        Assert.Null(CSharp.CodeForKind(result, MdRunKind.Program));
    }

    [Fact]
    public void Vb_AnEmptyScript_Should_GetAModuleWithSubMain()
    {
        var code = Vb.CodeForKind("", MdRunKind.Program);

        Assert.Equal(
            "Module Program\n    Sub Main()\n        ${0}\n    End Sub\n\n    ' Other methods and fields used by Main go here.\nEnd Module\n",
            code);
    }

    [Fact]
    public void Vb_Statements_Should_BeWrappedInSubMain()
    {
        var code = Vb.CodeForKind("Dim x = 1\nConsole.WriteLine(x)\n", MdRunKind.Program);

        Assert.StartsWith("Module Program\n    Sub Main()\n        ${0}Dim x = 1\n        Console.WriteLine(x)\n    End Sub\n", code);
    }

    [Theory]
    [InlineData("Module Program\nSub Main()\nEnd Sub\nEnd Module\n")]
    [InlineData("public module M\nEnd Module\n")]
    [InlineData("Class C\nEnd Class\n")]
    public void Vb_AModuleOrClass_Should_StayAsItIs(string code)
        => Assert.Null(Vb.CodeForKind(code, MdRunKind.Program));

    // A classic program already has its entry point; switching to Program must not wrap it in Main.
    [Fact]
    public void CSharp_AClassWithAStaticMain_Should_StayAsWritten()
    {
        Assert.Null(CSharp.CodeForKind("using System;\n\nclass Program\n{\n    static void Main() => Console.WriteLine(1);\n}\n", MdRunKind.Program));
    }

    // A class without a static Main is not an entry point: it still gets a Main skeleton.
    [Fact]
    public void CSharp_AClassWithoutAStaticMain_Should_StillGetMain()
    {
        Assert.Contains("void Main()", CSharp.CodeForKind("class Helper\n{\n    void Main() { }\n}\n", MdRunKind.Program));
    }

    // A Main under DEBUG already is the program's Main: switching to Program leaves the code alone.
    [Fact]
    public void CSharp_AMainUnderDebug_Should_StayAsWritten()
    {
        Assert.Null(CSharp.CodeForKind("#if DEBUG\nvoid Main() { }\n#endif\n", MdRunKind.Program));
    }

    // The template's own lines follow the editor's indentation; the script's lines keep theirs.
    [Theory]
    [InlineData("\t")]
    [InlineData("  ")]
    public void CSharp_Template_Should_UseTheEditorsIndentation(string indent)
    {
        Assert.Equal($"void Main()\n{{\n{indent}${{0}}\n}}\n\n// Other methods, fields and types used by Main go here.\n",
            CSharp.CodeForKind("", MdRunKind.Program, indent));
        Assert.StartsWith($"void Main()\n{{\n{indent}${{0}}if (true)\n{indent}{{\n{indent}    Console.WriteLine(1);\n{indent}}}\n}}\n",
            CSharp.CodeForKind("if (true)\n{\n    Console.WriteLine(1);\n}\n", MdRunKind.Program, indent));
    }

    [Fact]
    public void Vb_Template_Should_UseTheEditorsIndentation()
    {
        Assert.Equal("Module Program\n\tSub Main()\n\t\t${0}\n\tEnd Sub\n\n\t' Other methods and fields used by Main go here.\nEnd Module\n",
            Vb.CodeForKind("", MdRunKind.Program, "\t"));
        Assert.StartsWith("Module Program\n  Sub Main()\n    ${0}Dim x = 1\n    Console.WriteLine(x)\n  End Sub\n",
            Vb.CodeForKind("Dim x = 1\nConsole.WriteLine(x)\n", MdRunKind.Program, "  "));
    }
}
