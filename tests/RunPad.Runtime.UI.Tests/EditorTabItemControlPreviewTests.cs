using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Moq;
using RunPad.Avalonia.Themes;
using RunPad.Contracts;
using RunPad.Format.RPad;
using RunPad.Services;
using RunPad.Services.BuildCache;
using RunPad.Services.Preferences;
using RunPad.UserControls;
using RunPad.UserControls.Models;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// The Markdown preview beside the editor or in its place (EditorTabItemControl.Preview.cs).
public class EditorTabItemControlPreviewTests
{
    private static Mock<IPreferencesService> MakePrefs()
    {
        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Editor).Returns(new EditorSettings());
        prefs.Setup(p => p.CSharp).Returns(new CSharpLanguageSettings());
        prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
        prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
        prefs.Setup(p => p.Session).Returns(new SessionSettings());
        return prefs;
    }

    private static ActiveTheme Theme(TextMateSharp.Grammars.ThemeName textMate) => new(
        new InterfaceTheme("Test", ThemeVariant.Dark, true), new EditorTheme("Test", textMate, true));

    private static (EditorsTabItemControl Control, EditorTabItemViewModel Vm) CreateControl()
    {
        var prefs = MakePrefs();
        var vm = new EditorTabItemViewModel(
            null, [], prefs.Object, new Mock<IEditorTabActions>().Object, RpadLanguage.CSharp, RpadKind.Statements);
        var theme = new Mock<IThemeService>();
        theme.Setup(t => t.CurrentTheme).Returns(Theme(TextMateSharp.Grammars.ThemeName.DarkPlus));
        var ctrl = new EditorsTabItemControl(vm, theme.Object, new Mock<IWindowService>().Object,
            new Mock<ILanguageHostRegistry>().Object, prefs.Object, intelliSense: null, new Mock<IBuildCacheService>().Object);
        new Window { Content = ctrl }.Show();
        return (ctrl, vm);
    }

    private static Control Named(EditorsTabItemControl ctrl, string name) => ctrl.FindControl<Control>(name)!;

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.True(condition());
    }

    [AvaloniaFact]
    public void ANewTab_Should_ShowNoPreview()
    {
        var (ctrl, vm) = CreateControl();

        Assert.Equal(ScriptPreviewMode.Off, vm.PreviewMode);
        Assert.Null(ctrl.PreviewForTests);
        Assert.False(Named(ctrl, "PreviewHost").IsVisible);
        Assert.True(Named(ctrl, "EditorHost").IsVisible);
        ctrl.Dispose();
    }

    [AvaloniaFact]
    public void PreviewToTheSide_Should_ShowTheDocumentBesideTheEditor()
    {
        var (ctrl, vm) = CreateControl();
        ctrl.State.Editor.Text = "Console.WriteLine(42);";

        vm.TogglePreviewToSide();

        Assert.Equal(ScriptPreviewMode.Side, vm.PreviewMode);
        Assert.True(Named(ctrl, "EditorHost").IsVisible);
        Assert.True(Named(ctrl, "PreviewSplitter").IsVisible);
        Assert.True(Named(ctrl, "PreviewHost").IsVisible);
        var markdown = ctrl.PreviewForTests!.Markdown!;
        Assert.Contains("| Kind | Statements |", markdown);
        Assert.Contains("```csharp run\nConsole.WriteLine(42);\n```", markdown);
        ctrl.Dispose();
    }

    [AvaloniaFact]
    public void Preview_Should_TakeTheEditorsPlace_AndTheSameKey_ShouldBringTheEditorBack()
    {
        var (ctrl, vm) = CreateControl();

        vm.TogglePreview();

        Assert.Equal(ScriptPreviewMode.Full, vm.PreviewMode);
        Assert.False(Named(ctrl, "EditorHost").IsVisible);
        Assert.False(Named(ctrl, "PreviewSplitter").IsVisible);
        Assert.True(Named(ctrl, "PreviewHost").IsVisible);

        vm.TogglePreview();

        Assert.Equal(ScriptPreviewMode.Off, vm.PreviewMode);
        Assert.True(Named(ctrl, "EditorHost").IsVisible);
        Assert.False(Named(ctrl, "PreviewHost").IsVisible);
        ctrl.Dispose();
    }

    [AvaloniaFact]
    public void PreviewToTheSide_FromTheFullPreview_Should_ShowBoth()
    {
        var (ctrl, vm) = CreateControl();
        vm.TogglePreview();

        vm.TogglePreviewToSide();

        Assert.Equal(ScriptPreviewMode.Side, vm.PreviewMode);
        Assert.True(Named(ctrl, "EditorHost").IsVisible);
        ctrl.Dispose();
    }

    [AvaloniaFact]
    public async Task Typing_Should_UpdateThePreview_AfterAPause()
    {
        var (ctrl, vm) = CreateControl();
        vm.TogglePreviewToSide();
        var preview = ctrl.PreviewForTests!;

        ctrl.State.Editor.Text = "var answer = 42;";

        Assert.DoesNotContain("var answer = 42;", preview.Markdown);
        await WaitUntil(() => preview.Markdown!.Contains("var answer = 42;"));
        ctrl.Dispose();
    }

    [AvaloniaFact]
    public void ChangingTheKind_Should_UpdateThePreviewsTable()
    {
        var (ctrl, vm) = CreateControl();
        vm.TogglePreviewToSide();

        vm.SelectedKind = nameof(RpadKind.Expression);

        Assert.Contains("| Kind | Expression |", ctrl.PreviewForTests!.Markdown);
        ctrl.Dispose();
    }

    [AvaloniaFact]
    public void AHiddenPreview_Should_NotFollowTheText()
    {
        var (ctrl, vm) = CreateControl();
        vm.TogglePreviewToSide();
        vm.TogglePreviewToSide();
        var before = ctrl.PreviewForTests!.Markdown;

        ctrl.State.Editor.Text = "var hidden = 1;";
        vm.SelectedKind = nameof(RpadKind.Expression);

        Assert.Equal(before, ctrl.PreviewForTests.Markdown);
        vm.TogglePreviewToSide();
        Assert.Contains("var hidden = 1;", ctrl.PreviewForTests.Markdown);
        ctrl.Dispose();
    }

    [AvaloniaFact]
    public void AThemeChange_Should_RenderThePreviewAgain_WithTheNewCodeTheme()
    {
        var (ctrl, vm) = CreateControl();
        vm.TogglePreviewToSide();
        var preview = ctrl.PreviewForTests!;
        var extension = Assert.Single(preview.Extensions);

        ctrl.ApplyTheme(Theme(TextMateSharp.Grammars.ThemeName.Monokai));

        Assert.NotSame(extension, Assert.Single(preview.Extensions));
        Assert.Contains("| Kind |", preview.Markdown);
        ctrl.Dispose();
    }
}
