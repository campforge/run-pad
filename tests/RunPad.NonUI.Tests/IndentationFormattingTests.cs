using Microsoft.Extensions.Logging.Abstractions;
using RunPad.Languages.CSharp.IntelliSense;
using RunPad.Services.Preferences;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

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
        var settings = new EditorSettings();
        Assert.False(settings.UseTabs);
        Assert.Equal(4, settings.IndentationSize);
        settings.IndentationSize = 0;
        Assert.Equal(1, settings.IndentationSize);
        settings.IndentationSize = int.MaxValue;
        Assert.Equal(16, settings.IndentationSize);
    }
}
