using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Moq;
using RunPad.Avalonia.Themes;
using RunPad.Contracts;
using RunPad.Format.RPad;
using RunPad.Services;
using RunPad.Services.BuildCache;
using RunPad.Services.Preferences;
using RunPad.Services.Recovery;
using RunPad.UserControls;
using RunPad.UserControls.Models;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

public sealed class TabRecoveryTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("runpad-tab-recovery").FullName;
    private readonly List<(Window Window, EditorsTabItemControl Control)> _controls = [];
    private readonly Mock<IWindowService> _windows = new();

    private (EditorsTabItemControl Ctrl, EditorTabItemViewModel Vm) Open(string? path = null, RecoveryTab? snapshot = null)
    {
        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Editor).Returns(new EditorSettings());
        prefs.Setup(p => p.CSharp).Returns(new CSharpLanguageSettings());
        prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
        prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
        prefs.Setup(p => p.Session).Returns(new SessionSettings());
        var theme = new Mock<IThemeService>();
        theme.Setup(t => t.CurrentTheme).Returns(new ActiveTheme(new InterfaceTheme("Test", ThemeVariant.Dark, true),
            new EditorTheme("Test", TextMateSharp.Grammars.ThemeName.DarkPlus, true)));
        var vm = new EditorTabItemViewModel(path, [], prefs.Object, new Mock<IEditorTabActions>().Object,
            RpadLanguage.CSharp, RpadKind.Statements)
        { RestoredRecovery = snapshot };
        var ctrl = new EditorsTabItemControl(vm, theme.Object, _windows.Object, new Mock<ILanguageHostRegistry>().Object,
            prefs.Object, null, new Mock<IBuildCacheService>().Object);
        var window = new Window { Content = ctrl };
        window.Show();
        _controls.Add((window, ctrl));
        return (ctrl, vm);
    }

    [AvaloniaFact]
    public void UntitledSnapshot_Should_RestoreExactTextMetadataAndLiteralTemplateMarkers()
    {
        var (ctrl, vm) = Open();
        var text = "// ${0}\r\nПривет\n\r\n";
        ctrl.State.Editor.Text = text;
        ctrl.State.RpadDocument!.FrontMatter.Arguments = [new() { Name = "values", Type = RpadArgumentType.StringList, Default = RpadValue.List(new[] { "", "a b", "Ж" }.Select(RpadValue.Of)) }];
        ctrl.State.RpadDocument.FrontMatter.References = ["$(CurrentQueryPath)/a.dll"];
        ctrl.State.Editor.CaretOffset = 5;
        var snapshot = ctrl.CaptureRecovery();
        ctrl.State.RpadDocument.FrontMatter.Arguments[0].Default!.Items!.Add(RpadValue.Of("later"));
        var (restored, restoredVm) = Open(snapshot: snapshot);
        Assert.Equal(text, restored.State.Editor.Text);
        Assert.Equal(new[] { "", "a b", "Ж" }, restored.State.RpadDocument!.FrontMatter.Arguments[0].Default!.Items!.Select(i => i.Text));
        Assert.Equal(snapshot.Document!.FrontMatter.References, restored.State.RpadDocument.FrontMatter.References);
        Assert.True(restoredVm.IsDirty);
        Assert.Null(restoredVm.FilePath);
        Assert.Equal(5, restored.State.Editor.CaretOffset);
    }

    [AvaloniaFact]
    public async Task DirtyNamedSnapshot_Should_KeepOriginalDiskBaseline_AndAskBeforeOverwritingExternalChanges()
    {
        var path = Path.Combine(_root, "dirty.rpad");
        File.WriteAllText(path, RpadWriter.NewFile("old", "test"));
        var (ctrl, vm) = Open(path);
        ctrl.State.Editor.Text = "unsaved";
        var snapshot = ctrl.CaptureRecovery();
        var originalHash = vm.DiskState!.Hash;
        File.WriteAllText(path, RpadWriter.NewFile("external", "test"));
        var external = File.ReadAllBytes(path);
        var (restored, restoredVm) = Open(path, snapshot);
        _windows.Setup(w => w.ShowSaveConflictDialogAsync(It.IsAny<string>())).ReturnsAsync(SaveConflictChoice.Cancel);
        var result = await InPlaceSave.SaveAsync(restoredVm, _windows.Object, () => Task.CompletedTask);
        Assert.Equal(InPlaceSaveStatus.Cancelled, result.Status);
        Assert.Equal("unsaved", restored.State.Editor.Text);
        Assert.Equal(originalHash, restoredVm.DiskState!.Hash);
        Assert.Equal(external, File.ReadAllBytes(path));
    }

    [AvaloniaFact]
    public async Task UnknownRecoveredBaseline_Should_NotSilentlyAcceptTheCurrentFile()
    {
        var path = Path.Combine(_root, "unknown.rpad");
        File.WriteAllText(path, RpadWriter.NewFile("disk", "test"));
        var (ctrl, _) = Open();
        var frame = ctrl.CaptureRecovery() with { FilePath = path, BaselineKnown = false, IsDirty = true };
        var (_, vm) = Open(path, frame);
        _windows.Setup(w => w.ShowSaveConflictDialogAsync(It.IsAny<string>())).ReturnsAsync(SaveConflictChoice.Cancel);
        await InPlaceSave.SaveAsync(vm, _windows.Object, () => Task.CompletedTask);
        _windows.Verify(w => w.ShowSaveConflictDialogAsync(It.IsAny<string>()), Times.Once);
    }

    [AvaloniaFact]
    public void InvalidRawDocument_Should_RemainRawAndUnchanged()
    {
        var path = Path.Combine(_root, "invalid.rpad");
        var raw = "---\nlanguage: [broken\n---\nraw\r\n${0}";
        File.WriteAllText(path, raw);
        var (ctrl, _) = Open(path);
        var snapshot = ctrl.CaptureRecovery() with { IsDirty = true };
        File.WriteAllText(path, "external");
        var (restored, _) = Open(path, snapshot);
        Assert.Equal(raw, restored.State.Editor.Text);
        Assert.Null(restored.State.RpadDocument);
        Assert.NotNull(restored.State.DocumentParseError);
        Assert.Equal("external", File.ReadAllText(path));
    }

    [AvaloniaFact]
    public void MetadataOnlyChange_Should_BeCapturedAsDirty()
    {
        var (ctrl, vm) = Open();
        Assert.False(vm.IsDirty);
        ctrl.State.RpadDocument!.FrontMatter.Arguments = [new() { Name = "value", Default = RpadValue.Of("metadata-only") }];
        Assert.True(ctrl.CaptureRecovery().IsDirty);
    }

    public void Dispose()
    {
        foreach (var (window, ctrl) in _controls) { window.Close(); ctrl.Dispose(); }
        Directory.Delete(_root, true);
    }
}
