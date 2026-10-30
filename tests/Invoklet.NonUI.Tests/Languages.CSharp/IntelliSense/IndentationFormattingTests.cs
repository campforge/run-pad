using Invoklet.Format.MdRun;
using Invoklet.Languages.CSharp;
using Invoklet.Languages.CSharp.IntelliSense;
using Invoklet.Services.Preferences;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Invoklet.NonUI.Tests.Languages.CSharp.IntelliSense;

public class IndentationFormattingTests
{
    [Theory]
    [InlineData(false, 2, "  ")]
    [InlineData(false, 4, "    ")]
    [InlineData(true, 4, "\t")]
    public async Task Formatting_Should_UseTheSelectedIndentation_AndAllowLiveChanges(bool tabs, int width, string indent)
    {
        using var service = new CSharpIntelliSenseService([], NullLogger<CSharpIntelliSenseService>.Instance);
        service.UpdateIndentation(tabs, width);
        var result = await service.FormatDocumentAsync("if (true) {\nSystem.Console.WriteLine(1);\n}", TestContext.Current.CancellationToken);
        Assert.Contains("\n" + indent + "System.Console", result);
        service.UpdateIndentation(false, 3);
        result = await service.FormatDocumentAsync(result!, TestContext.Current.CancellationToken);
        Assert.Contains("\n   System.Console", result);
        Assert.DoesNotContain("\t", result);
    }

    [Fact]
    public async Task IndentationPreferences_Should_NotLeakIntoOtherEditorsOrCodeActions()
    {
        using var custom = new CSharpIntelliSenseService([], NullLogger<CSharpIntelliSenseService>.Instance);
        using var standard = new CSharpIntelliSenseService([], NullLogger<CSharpIntelliSenseService>.Instance);
        custom.UpdateIndentation(true, 3);
        const string code = "if (true) {\nSystem.Console.WriteLine(1);\n}";
        var customized = await custom.FormatDocumentAsync(code, TestContext.Current.CancellationToken);
        var unchanged = await standard.FormatDocumentAsync(code, TestContext.Current.CancellationToken);
        Assert.Contains("\n\tSystem.Console", customized);
        Assert.Contains("\n    System.Console", unchanged);
    }

    [Fact]
    public void Settings_Should_DefaultToFourSpaces_AndBoundInvalidWidths()
    {
        var saved = System.Text.Json.JsonSerializer.Serialize(new EditorSettings { UseTabs = true, IndentationSize = 2 });
        var restored = System.Text.Json.JsonSerializer.Deserialize<EditorSettings>(saved)!;
        Assert.True(restored.UseTabs);
        Assert.Equal(2, restored.IndentationSize);
        Assert.Equal("\t", restored.IndentationUnit);
        Assert.DoesNotContain("IndentationUnit", saved);
        var settings = new EditorSettings();
        Assert.False(settings.UseTabs);
        Assert.Equal(4, settings.IndentationSize);
        Assert.Equal("    ", settings.IndentationUnit);
        settings.IndentationSize = 0;
        Assert.Equal(1, settings.IndentationSize);
        settings.IndentationSize = int.MaxValue;
        Assert.Equal(16, settings.IndentationSize);
    }

    private static CSharpIntelliSenseService WithReferences(MdRunKind kind, bool tabs, int width)
    {
        var references = new CSharpReferenceService();
        var service = new CSharpIntelliSenseService(
            references.GetDefault().Append(references.GetExtensionReference()), NullLogger<CSharpIntelliSenseService>.Instance);
        service.UpdateKind(kind);
        service.UpdateIndentation(tabs, width);
        return service;
    }

    // The refactoring with this title, offered at the first position that has it, applied.
    private static async Task<string> RefactorAsync(CSharpIntelliSenseService service, string code, string title)
    {
        for (var position = 0; position < code.Length; position++)
        {
            var offered = await service.GetRefactoringsAsync(code, position, TestContext.Current.CancellationToken);
            if (offered?.FirstOrDefault(r => r.Title == title) is not { } match) continue;
            var fix = await service.MaterializeFixAsync(code, match.Key, TestContext.Current.CancellationToken);
            Assert.NotNull(fix);
            return fix.NewCode;
        }
        Assert.Fail($"'{title}' was not offered");
        return "";
    }

    // A code action formats what it writes with the editor's indentation too: it used to write four
    // spaces whatever the setting, and turned the touched line's own tab into spaces.
    [Theory]
    [InlineData(true, 4, "\t")]
    [InlineData(false, 2, "  ")]
    public async Task CodeActions_Should_IndentWithTheSelectedIndentation(bool tabs, int width, string indent)
    {
        using var service = WithReferences(MdRunKind.Statements, tabs, width);
        var code = $"if (true)\n{{\n{indent}Console.WriteLine(DateTime.Now);\n}}\n";

        var result = await RefactorAsync(service, code, "Introduce local for 'DateTime.Now'");

        Assert.Equal($"if (true)\n{{\n{indent}DateTime now = DateTime.Now;\n{indent}Console.WriteLine(now);\n}}\n", result);
    }

    [Fact]
    public async Task CodeActions_InTheProgramKind_Should_IndentWithTheSelectedIndentation()
    {
        using var service = WithReferences(MdRunKind.Program, tabs: false, width: 2);
        const string code = "void Main()\n{\n  if (true)\n  {\n    Console.WriteLine(DateTime.Now);\n  }\n}\n";

        var result = await RefactorAsync(service, code, "Introduce local for 'DateTime.Now'");

        Assert.Equal("void Main()\n{\n  if (true)\n  {\n    DateTime now = DateTime.Now;\n    Console.WriteLine(now);\n  }\n}\n", result);
    }

    // A wrapped line continues one level in, in the editor's indentation.
    [Fact]
    public async Task WrapExpression_Should_ContinueWithTheSelectedIndentation()
    {
        using var service = WithReferences(MdRunKind.Statements, tabs: true, width: 4);
        const string code = "if (true)\n{\n\tConsole.WriteLine(DateTime.Now.Year > 2000 && DateTime.Now.Month > 1);\n}\n";

        var result = await RefactorAsync(service, code, "Wrap expression");

        Assert.Equal("if (true)\n{\n\tConsole.WriteLine(DateTime.Now.Year > 2000\n\t\t&& DateTime.Now.Month > 1);\n}\n", result);
    }

    [Fact]
    public async Task CodeActions_Should_FollowALiveIndentationChange()
    {
        using var service = WithReferences(MdRunKind.Statements, tabs: false, width: 4);
        service.UpdateIndentation(true, 4);
        const string code = "if (true)\n{\n\tConsole.WriteLine(DateTime.Now);\n}\n";

        var result = await RefactorAsync(service, code, "Introduce local for 'DateTime.Now'");

        Assert.Contains("\n\tDateTime now = DateTime.Now;\n", result);
    }
}
