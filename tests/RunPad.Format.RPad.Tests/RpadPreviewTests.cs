using Markdig;
using Xunit;

namespace RunPad.Format.RPad.Tests;

public class RpadPreviewTests
{
    private const string File = """
        ---
        version: "1.0"
        language: csharp
        targetSdk: net10.0
        kind: Program
        packages:
          "Humanizer": "2.14.1"
        namespaces:
          - "System.Text"
        ---

        # Title

        Some *notes*.

        ```csharp run
        void Main() { }
        ```

        After.
        """;

    [Fact]
    public void ToMarkdown_Should_ShowTheFrontMatterAsATable_AndKeepTheBody()
    {
        var doc = RpadParser.TryParse(File.ReplaceLineEndings("\n"))!;

        var markdown = RpadPreview.ToMarkdown(doc, "void Main() { }\n");

        Assert.Equal("""
            | Setting | Value |
            | --- | --- |
            | Language | C# |
            | Kind | Program |
            | Target SDK | net10\.0 |
            | Packages | ` Humanizer 2.14.1 ` |
            | Namespaces | ` System.Text ` |


            # Title

            Some *notes*.

            ```csharp run
            void Main() { }
            ```

            After.
            """.ReplaceLineEndings("\n"), markdown);
        Assert.DoesNotContain("version:", markdown);
    }

    [Fact]
    public void ToMarkdown_Should_ShowTheEditorText_NotTheSavedCode()
    {
        var doc = RpadParser.TryParse(File.ReplaceLineEndings("\n"))!;

        var markdown = RpadPreview.ToMarkdown(doc, "Console.WriteLine(42);");

        Assert.Contains("```csharp run\nConsole.WriteLine(42);\n```", markdown);
        Assert.DoesNotContain("void Main", markdown);
    }

    [Fact]
    public void ToMarkdown_Should_FenceTheCode_OfADocumentWithoutABody()
    {
        var doc = new RpadDocument { FrontMatter = new RpadFrontMatter { Language = RpadLanguage.Vb } };

        var markdown = RpadPreview.ToMarkdown(doc, "Console.WriteLine(1)\n");

        Assert.EndsWith("\n```vb run\nConsole.WriteLine(1)\n```\n", markdown);
        Assert.Contains("| Language | Visual Basic |", markdown);
    }

    [Fact]
    public void ToMarkdown_Should_KeepTheTableIntact_WhenValuesHoldMarkdown()
    {
        var doc = new RpadDocument
        {
            FrontMatter = new RpadFrontMatter
            {
                TargetSdk = new TargetSdk("net|*x*"),
                References = ["a|b.dll", "``tick`.dll"],
                Args = ["two\nlines"],
            },
        };

        var html = Markdown.ToHtml(RpadPreview.ToMarkdown(doc, ""), new MarkdownPipelineBuilder().UsePipeTables().Build());

        Assert.Contains("<td>net|*x*</td>", html);
        Assert.Contains("<code>a|b.dll</code>", html);
        Assert.Contains("<code>``tick`.dll</code>", html);
        Assert.Contains("<code>two lines</code>", html);
        Assert.Equal(6, html.Split("<tr>").Length - 1); // header + Language, Kind, Target SDK, References, Arguments
    }
}
