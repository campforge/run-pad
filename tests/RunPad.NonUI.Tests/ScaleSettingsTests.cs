using System.Text.Json;
using RunPad.Services.Preferences;
using RunPad.Services.Scaling;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// The interface scale and the code zoom are kept in settings.json. A file from an older version
// has neither, and a hand-edited one may hold anything.
public class ScaleSettingsTests
{
    [Fact]
    public void AnOlderSettingsFile_Should_LoadBothAt100Percent_AndKeepItsOtherEditorSettings()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(
            """{ "Editor": { "FontSize": 15, "FontFamily": "Menlo", "WordWrap": true } }""")!;

        Assert.Equal(100, settings.Appearance.InterfaceScalePercent);
        Assert.Equal(100, settings.Editor.ZoomPercent);
        Assert.Equal(15, settings.Editor.FontSize);
        Assert.Equal("Menlo", settings.Editor.FontFamily);
        Assert.True(settings.Editor.WordWrap);
        Assert.True(settings.Editor.ResultsWordWrap);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheWrapSettings_Should_SurviveSaveAndLoad_Independently(bool results)
    {
        var saved = new AppSettings();
        saved.Editor.ResultsWordWrap = results;
        saved.Editor.WordWrap = !results;

        var loaded = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(saved))!;

        Assert.Equal(results, loaded.Editor.ResultsWordWrap);
        Assert.Equal(!results, loaded.Editor.WordWrap);
    }

    [Fact]
    public void BothValues_Should_SurviveSaveAndLoad()
    {
        var saved = new AppSettings();
        saved.Appearance.InterfaceScalePercent = 125;
        saved.Editor.ZoomPercent = 110;

        var loaded = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(saved))!;

        Assert.Equal(125, loaded.Appearance.InterfaceScalePercent);
        Assert.Equal(110, loaded.Editor.ZoomPercent);
    }

    [Theory]
    [InlineData(999, 0, 200, 50)]
    [InlineData(-5, 100000, 75, 300)]
    [InlineData(10, 49.99, 75, 50)]
    [InlineData(124.96, 110.04, 125, 110)]
    public void OutOfRangeValues_InTheFile_Should_BeBroughtIntoRange(
        double interfaceInFile, double zoomInFile, double expectedInterface, double expectedZoom)
    {
        var json = $$"""{ "Appearance": { "InterfaceScalePercent": {{interfaceInFile.ToString(System.Globalization.CultureInfo.InvariantCulture)}} }, "Editor": { "ZoomPercent": {{zoomInFile.ToString(System.Globalization.CultureInfo.InvariantCulture)}} } }""";

        var settings = JsonSerializer.Deserialize<AppSettings>(json)!;

        Assert.Equal(expectedInterface, settings.Appearance.InterfaceScalePercent);
        Assert.Equal(expectedZoom, settings.Editor.ZoomPercent);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void ANonNumber_Should_FallBackTo100Percent(double value)
    {
        Assert.Equal(100, ScalePercent.NormalizeInterface(value));
        Assert.Equal(100, ScalePercent.NormalizeCodeZoom(value));
        Assert.Equal(100, new AppearanceSettings { InterfaceScalePercent = value }.InterfaceScalePercent);
        Assert.Equal(100, new EditorSettings { ZoomPercent = value }.ZoomPercent);
    }

    [Fact]
    public void ThePresets_Should_IncludeTheCommonScales_AndStayInRange()
    {
        Assert.Subset(new HashSet<int>(ScalePercent.InterfacePresets), new HashSet<int> { 100, 110, 125, 150 });
        Assert.All(ScalePercent.InterfacePresets, p => Assert.InRange(p, ScalePercent.InterfaceMin, ScalePercent.InterfaceMax));
    }
}
