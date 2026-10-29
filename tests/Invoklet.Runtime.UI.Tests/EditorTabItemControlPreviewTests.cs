using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Invoklet.Avalonia.Themes;
using Invoklet.Contracts;
using Invoklet.Format.MdRun;
using Invoklet.Services;
using Invoklet.Services.BuildCache;
using Invoklet.Services.Preferences;
using Invoklet.UserControls;
using Invoklet.UserControls.Models;
using Moq;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

// The Markdown preview beside the editor or in its place (EditorTabItemControl.Preview.cs).
public class EditorTabItemControlPreviewTests
{
    [AvaloniaFact]
    public void Tabs_Should_SharePreviewModeAndSplitterPositions_IncludingNewTabs()
    {
        var prefs = MakePrefs();
        EditorTabItemViewModel Tab() => new(null, [], prefs.Object, new Mock<IEditorTabActions>().Object);
        using var first = Tab();
        using var second = Tab();
        first.TogglePreviewToSide();
        Assert.Equal(ScriptPreviewMode.Side, second.PreviewMode);
        first.Layout.EditorWidth = new GridLength(3, GridUnitType.Star);
        first.Layout.PreviewWidth = GridLength.Star;
        first.OutputPanel.Size = new GridLength(310);
        first.DebugPanel.Layout!.StackWidth = new GridLength(400);
        first.DebugPanel.Layout.ThreadsHeight = new GridLength(110);
        using var later = Tab();
        Assert.Equal(first.Layout.EditorWidth, second.Layout.EditorWidth);
        Assert.Equal(first.Layout.PreviewWidth, later.Layout.PreviewWidth);
        Assert.Equal(310, second.OutputPanel.Size.Value);
        Assert.Equal(400, later.DebugPanel.Layout!.StackWidth.Value);
        Assert.Equal(110, second.DebugPanel.Layout!.ThreadsHeight.Value);
        second.TogglePreview();
        Assert.Equal(ScriptPreviewMode.Full, first.PreviewMode);
        Assert.Equal(ScriptPreviewMode.Full, later.PreviewMode);
        later.TogglePreview();
        Assert.Equal(ScriptPreviewMode.Off, first.PreviewMode);
        first.TogglePreviewToSide();
        Assert.Equal(3, later.Layout.EditorWidth.Value);
    }

    [AvaloniaFact]
    public void SharedPreview_Should_KeepBoundWidthsAcrossModeChanges()
    {
        var prefs = MakePrefs();
        var (first, a) = CreateControl(out var one, prefs);
        var (second, b) = CreateControl(out var two, prefs);
        try
        {
            a.TogglePreviewToSide();
            var grid = first.FindControl<Grid>("EditorArea")!;
            grid.ColumnDefinitions[0].Width = new GridLength(3, GridUnitType.Star);
            grid.ColumnDefinitions[2].Width = GridLength.Star;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(ScriptPreviewMode.Side, b.PreviewMode);
            Assert.True(second.FindControl<Control>("PreviewHost")!.IsVisible);
            Assert.Equal(3, second.FindControl<Grid>("EditorArea")!.ColumnDefinitions[0].Width.Value);
            b.TogglePreviewToSide();
            a.TogglePreviewToSide();
            one.UpdateLayout();
            two.UpdateLayout();
            Assert.Equal(3, grid.ColumnDefinitions[0].Width.Value);
            Assert.True(first.FindControl<Control>("EditorHost")!.Bounds.Width > first.FindControl<Control>("PreviewHost")!.Bounds.Width);
        }
        finally
        {
            one.Close(); two.Close(); first.Dispose(); second.Dispose(); a.Dispose(); b.Dispose();
        }
    }

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

    private static (EditorsTabItemControl Control, EditorTabItemViewModel Vm) CreateControl() => CreateControl(out _);

    private static (EditorsTabItemControl Control, EditorTabItemViewModel Vm) CreateControl(out Window window, Mock<IPreferencesService>? sharedPrefs = null)
    {
        var prefs = sharedPrefs ?? MakePrefs();
        var vm = new EditorTabItemViewModel(
            null, [], prefs.Object, new Mock<IEditorTabActions>().Object, MdRunLanguage.CSharp, MdRunKind.Statements);
        var theme = new Mock<IThemeService>();
        theme.Setup(t => t.CurrentTheme).Returns(Theme(TextMateSharp.Grammars.ThemeName.DarkPlus));
        // The real theme gives the preview a background, which also makes its gaps between blocks
        // take the mouse wheel.
        theme.Setup(t => t.GetBrush(It.IsAny<string>())).Returns(Brushes.Black);
        var ctrl = new EditorsTabItemControl(vm, theme.Object, new Mock<IWindowService>().Object,
            new Mock<ILanguageHostRegistry>().Object, prefs.Object, intelliSense: null, new Mock<IBuildCacheService>().Object);
        window = new Window { Width = 1000, Height = 400, Content = ctrl };
        window.Show();
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

        vm.SelectedKind = nameof(MdRunKind.Expression);

        Assert.Contains("| Kind | Expression |", ctrl.PreviewForTests!.Markdown);
        ctrl.Dispose();
    }

    [AvaloniaFact]
    public void SelectingAnArgumentPreset_Should_UpdateTheNextRunContract()
    {
        var (ctrl, vm) = CreateControl(out var window);
        try
        {
            var fm = ctrl.State.MdRunDocument!.FrontMatter;
            fm.Arguments = [new() { Name = "value", Type = MdRunArgumentType.Int }];
            fm.ArgumentSets["dev"] = new() { ["value"] = MdRunValue.Of("3") };
            fm.ArgumentSets["prod"] = new() { ["value"] = MdRunValue.Of("7") };
            fm.SelectedArgumentSet = "dev";
            ctrl.OnDependenciesEdited();
            Dispatcher.UIThread.RunJobs();

            var selector = ctrl.FindControl<Invoklet.TemplatedControls.ToolbarComboBox>("ArgumentSetSelector")!;
            Assert.Equal("dev", selector.SelectedItem);
            selector.SelectedItem = "prod";
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("prod", vm.SelectedArgumentSet);
            Assert.Equal("prod", fm.SelectedArgumentSet);
            var request = Invoklet.Contracts.Models.ExecutionRequest.For(fm, null);
            Assert.Equal(["--value", "7"], request.Arguments);
            Assert.Equal(["7"], MdRunArgumentContract.FromJson(request.ArgumentContractJson).Members.Single().Values);

            selector.SelectedItem = "dev";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("dev", fm.SelectedArgumentSet);
            selector.SelectedItem = "Defaults";
            Dispatcher.UIThread.RunJobs();
            Assert.Null(fm.SelectedArgumentSet);
            Assert.Empty(MdRunArgumentContract.Build(fm, out _).Members.Single().Values);
        }
        finally
        {
            window.Close(); ctrl.Dispose(); vm.Dispose();
        }
    }

    [AvaloniaFact]
    public void AHiddenPreview_Should_NotFollowTheText()
    {
        var (ctrl, vm) = CreateControl();
        vm.TogglePreviewToSide();
        vm.TogglePreviewToSide();
        var before = ctrl.PreviewForTests!.Markdown;

        ctrl.State.Editor.Text = "var hidden = 1;";
        vm.SelectedKind = nameof(MdRunKind.Expression);

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

    [AvaloniaFact]
    public void ThePreviewButton_Should_OpenThePreviewToTheSide_AndCloseIt()
    {
        var (ctrl, vm) = CreateControl();
        var button = ctrl.FindControl<ToggleButton>("PreviewButton")!;

        Assert.False(button.IsChecked);
        button.IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(ScriptPreviewMode.Side, vm.PreviewMode);
        Assert.True(Named(ctrl, "PreviewHost").IsVisible);

        button.IsChecked = false;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(ScriptPreviewMode.Off, vm.PreviewMode);
        ctrl.Dispose();
    }

    [AvaloniaFact]
    public void ThePreviewButton_Should_ShowAnyPreview_AndCloseThePreviewAlone()
    {
        var (ctrl, vm) = CreateControl();
        var button = ctrl.FindControl<ToggleButton>("PreviewButton")!;

        vm.TogglePreview();
        Dispatcher.UIThread.RunJobs();
        Assert.True(button.IsChecked);

        button.IsChecked = false;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(ScriptPreviewMode.Off, vm.PreviewMode);
        Assert.True(Named(ctrl, "EditorHost").IsVisible);
        ctrl.Dispose();
    }

    // ── Scrolling together ────────────────────────────────────────────────────

    private static readonly string LongCode = string.Concat(Enumerable.Range(0, 200).Select(i => $"Console.WriteLine({i});\n"));

    // Beside the editor, with a long script, laid out.
    private static (EditorsTabItemControl Control, EditorTabItemViewModel Vm, Window Window) CreateSideBySide()
    {
        var (ctrl, vm) = CreateControl(out var window);
        ctrl.State.Editor.Text = LongCode;
        vm.TogglePreviewToSide();
        Dispatcher.UIThread.RunJobs();
        return (ctrl, vm, window);
    }

    private static int CodeLine(EditorsTabItemControl ctrl)
    {
        MdRunPreview.ToMarkdown(ctrl.State.MdRunDocument!, ctrl.State.Editor.Text, out var codeLine);
        return codeLine;
    }

    private static double EditorTopLine(EditorsTabItemControl ctrl)
    {
        // The scroll viewer's offset: the text view only takes it on at the next layout pass.
        return ctrl.State.Editor.VerticalOffset / ctrl.State.Editor.TextArea.TextView.DefaultLineHeight;
    }

    private static void ScrollEditorToLine(EditorsTabItemControl ctrl, int line)
    {
        ctrl.State.Editor.ScrollToVerticalOffset(line * ctrl.State.Editor.TextArea.TextView.DefaultLineHeight);
        ctrl.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task ScrollingTheEditor_Should_ScrollThePreviewToTheSameLine()
    {
        var (ctrl, _, _) = CreateSideBySide();

        ScrollEditorToLine(ctrl, 100);

        await WaitUntil(() => Math.Abs(ctrl.PreviewForTests!.GetSourceLineAtTop()!.Value - (CodeLine(ctrl) + 100)) < 0.5);
        ctrl.Dispose();
    }

    [AvaloniaFact]
    public void TheEditorAtTheTop_Should_ShowTheTopOfTheDocument()
    {
        var (ctrl, _, _) = CreateSideBySide();
        ScrollEditorToLine(ctrl, 100);

        ScrollEditorToLine(ctrl, 0);

        Assert.Equal(0, ctrl.PreviewForTests!.GetSourceLineAtTop());
        ctrl.Dispose();
    }

    [AvaloniaFact]
    public async Task ScrollingThePreview_Should_ScrollTheEditor()
    {
        var (ctrl, _, window) = CreateSideBySide();
        var preview = ctrl.PreviewForTests!;
        var overPreview = preview.TranslatePoint(new Point(preview.Bounds.Width / 2, preview.Bounds.Height / 2), window)!.Value;

        for (var i = 0; i < 20; i++)
        {
            window.MouseWheel(overPreview, new Vector(0, -1));
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
        Dispatcher.UIThread.RunJobs();

        var previewLine = preview.GetSourceLineAtTop()!.Value;
        Assert.True(previewLine > CodeLine(ctrl) + 10, $"the preview did not scroll far enough ({previewLine})");
        await WaitUntil(() => Math.Abs(EditorTopLine(ctrl) - (previewLine - CodeLine(ctrl))) < 0.5);
        ctrl.Dispose();
    }

    [AvaloniaFact]
    public async Task Typing_Should_KeepThePreviewAtTheEditorsLine()
    {
        var (ctrl, _, _) = CreateSideBySide();
        ScrollEditorToLine(ctrl, 100);
        var before = ctrl.PreviewForTests!.Markdown;

        ctrl.State.Editor.Document.Insert(ctrl.State.Editor.Document.GetOffset(150, 1), "// note\n");

        await WaitUntil(() => ctrl.PreviewForTests!.Markdown != before);
        Assert.Equal(CodeLine(ctrl) + 100, ctrl.PreviewForTests!.GetSourceLineAtTop()!.Value, 0.5);
        ctrl.Dispose();
    }

    [AvaloniaFact]
    public void LeavingThePreviewAlone_Should_OpenTheEditorWhereThePreviewWas()
    {
        var (ctrl, vm) = CreateControl();
        ctrl.State.Editor.Text = LongCode;
        vm.TogglePreview();
        Dispatcher.UIThread.RunJobs();
        ctrl.PreviewForTests!.ScrollToSourceLine(CodeLine(ctrl) + 60);
        Dispatcher.UIThread.RunJobs();

        vm.TogglePreviewToSide();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(60, EditorTopLine(ctrl), 0.5);
        Assert.Equal(CodeLine(ctrl) + 60, ctrl.PreviewForTests!.GetSourceLineAtTop()!.Value, 0.5);
        ctrl.Dispose();
    }
}
