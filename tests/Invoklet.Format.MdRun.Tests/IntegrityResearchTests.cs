using System.Text;
using Xunit;

namespace Invoklet.Format.MdRun.Tests;

public class IntegrityResearchTests
{
    [Theory]
    [InlineData("\n", "", false)]
    [InlineData("\r\n", "", true)]
    [InlineData("\n", "Console.WriteLine(\"Привет 🌍 é\");", true)]
    [InlineData("\r\n", "Console.WriteLine(\"日本語 🌍\");", false)]
    public void FileRoundTrip_PreservesTheUnicodeBody(string nl, string code, bool bom)
    {
        var root = Directory.CreateTempSubdirectory("Invoklet-integrity-probe").FullName;
        try
        {
            var path = Path.Combine(root, "probe.rpad");
            var body = nl + "# Заметки 🌍" + nl + "Before 日本語" + nl + "```csharp run" + nl
                + (code.Length == 0 ? "" : code + nl) + "```" + nl + "After é" + nl;
            var header = "---" + nl + "language: csharp" + nl + "---" + nl;
            File.WriteAllText(path, header + body, new UTF8Encoding(bom));
            var parsed = MdRunParser.Parse(File.ReadAllText(path));
            Assert.NotNull(parsed.Document);
            var serialized = MdRunWriter.Write(parsed.Document);
            Assert.EndsWith(body, serialized, StringComparison.Ordinal);
            Assert.Equal(code.Length == 0 ? "" : code + nl, MdRunParser.TryParse(serialized)!.RunCode);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("[lib]/日本語.dll")]
    [InlineData("- helper.dll")]
    [InlineData("? helper.dll")]
    [InlineData(" leading.dll ")]
    [InlineData("normal.dll")]
    [InlineData("dir/日本語.dll")]
    public void ReferencePath_SurvivesWriteAndParse(string reference)
    {
        var source = "---\nlanguage: csharp\nreferences:\n  - '" + reference.Replace("'", "''")
            + "'\n---\n```csharp run\n1.Dump();\n```\n";
        var doc = MdRunParser.TryParse(source);
        Assert.NotNull(doc);
        Assert.Equal(reference, Assert.Single(doc.FrontMatter.References!));
        var result = MdRunParser.Parse(MdRunWriter.Write(doc));
        Assert.NotNull(result.Document);
        Assert.Equal(reference, Assert.Single(result.Document.FrontMatter.References!));
    }
}
