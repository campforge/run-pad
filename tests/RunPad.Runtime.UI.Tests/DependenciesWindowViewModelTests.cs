using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Moq;
using ReactiveUI;
using RunPad.Contracts;
using RunPad.Format.RPad;
using RunPad.Services.Preferences;
using RunPad.UserControls.Models;
using RunPad.Windows;
using RunPad.Windows.Models;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// Real DependenciesWindow + bindings, headless. Pure VM logic lives in
// RunPad.NonUI.Tests/DependenciesWindowViewModelLogicTests.
public class DependenciesWindowViewModelTests
{
    private static DependenciesWindowViewModel CreateVm(
        IReadOnlyList<PackageSearchResult>? results = null, IReadOnlyList<string>? versions = null)
    {
        var search = new Mock<IPackageSearchService>();
        search.Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(results ?? [new PackageSearchResult("Newtonsoft.Json", "13.0.3", "desc", 100)]);
        search.Setup(s => s.GetVersionsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(versions ?? ["13.0.3", "13.0.1", "12.0.3"]);
        var cache = new Mock<ILocalPackageCacheService>();
        cache.Setup(c => c.GetCachedPackages()).Returns([]);
        return new DependenciesWindowViewModel(search.Object, cache.Object, TimeSpan.Zero);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 250 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
        Assert.True(condition());
    }

    private static void Click(Window window, Control control)
    {
        window.UpdateLayout();
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static T Find<T>(Window window, string name) where T : Control
        => window.FindControl<T>(name) ?? throw new InvalidOperationException($"{name} not found");

    private static void CloseIfOpen(Window window)
    {
        if (window.IsVisible) window.Close();
    }

    [AvaloniaFact]
    public async Task AddingASearchResult_Should_ChangeOnlyTheDraft_UntilOkIsClicked()
    {
        var rpad = RpadParser.TryParse(RpadWriter.NewFile("Demo"))!;
        var vm = CreateVm();
        var window = new DependenciesWindow(vm);
        try
        {
            window.SetFrontMatter(rpad.FrontMatter);
            window.Show();

            Assert.True(Find<TextBox>(window, "SearchBox").IsFocused); // initial focus
            window.KeyTextInput("Newtonsoft.Json");
            await WaitUntilAsync(() => vm.Packages.Count == 1);

            // Select through the real ListBox -> details/versions/buttons via bindings.
            Find<ListBox>(window, "PackageList").SelectedIndex = 0;
            await WaitUntilAsync(() => !vm.IsLoadingVersions && vm.AvailableVersions.Count == 3);
            Assert.Equal("Newtonsoft.Json", vm.SelectedPackage?.Id);
            Assert.Equal("13.0.3", vm.SelectedVersion);
            Assert.Equal("13.0.3", Find<ComboBox>(window, "VersionBox").SelectedItem);
            var add = Find<Button>(window, "AddPackageButton");
            Assert.True(add.IsEffectivelyEnabled);
            Assert.False(Find<Button>(window, "RemovePackageButton").IsEffectivelyEnabled);

            Find<ComboBox>(window, "VersionBox").SelectedItem = "13.0.1";
            Assert.Equal("13.0.1", vm.SelectedVersion);
            Click(window, add);

            Assert.True(vm.IsSelectedInScript);
            Assert.Empty(rpad.FrontMatter.Packages); // still only the draft

            Click(window, Find<Button>(window, "OkButton"));

            Assert.False(window.IsVisible);
            Assert.Equal("13.0.1", rpad.FrontMatter.Packages["Newtonsoft.Json"].Version);
        }
        finally
        {
            CloseIfOpen(window);
        }
    }

    [AvaloniaFact]
    public void EnterInTheNamespaceBox_Should_AddWithoutClosing_AndEscape_Should_DiscardTheDraft()
    {
        var rpad = RpadParser.TryParse(RpadWriter.NewFile("Demo"))!;
        var namespacesBefore = rpad.FrontMatter.Namespaces?.ToList() ?? [];
        var vm = CreateVm();
        var window = new DependenciesWindow(vm);
        try
        {
            window.SetFrontMatter(rpad.FrontMatter);
            window.Show();

            window.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = 2;
            Dispatcher.UIThread.RunJobs();
            Find<TextBox>(window, "NamespaceBox").Focus();
            window.KeyTextInput("System.Linq");
            window.KeyPress(Key.Return, RawInputModifiers.None, PhysicalKey.Enter, null);
            Dispatcher.UIThread.RunJobs();

            Assert.True(window.IsVisible);
            Assert.Contains(vm.Namespaces, n => n.Value == "System.Linq");
            Assert.Equal("", vm.NewNamespace);

            // Invalid input: inline message, nothing added, still open.
            window.KeyTextInput("using X");
            window.KeyPress(Key.Return, RawInputModifiers.None, PhysicalKey.Enter, null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.IsVisible);
            Assert.True(vm.HasNamespaceError);

            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Dispatcher.UIThread.RunJobs();

            Assert.False(window.IsVisible);
            Assert.Equal(namespacesBefore, rpad.FrontMatter.Namespaces ?? []);
        }
        finally
        {
            CloseIfOpen(window);
        }
    }

    [AvaloniaFact]
    public void CancelButton_Should_DiscardTheDraft()
    {
        var rpad = RpadParser.TryParse(RpadWriter.NewFile("Demo"))!;
        var vm = CreateVm();
        var window = new DependenciesWindow(vm);
        try
        {
            window.SetFrontMatter(rpad.FrontMatter);
            window.Show();
            Assert.True(vm.AddReference("lib/My.dll"));

            Click(window, Find<Button>(window, "CancelButton"));

            Assert.False(window.IsVisible);
            Assert.True(rpad.FrontMatter.References is null or { Count: 0 });
        }
        finally
        {
            CloseIfOpen(window);
        }
    }

    [AvaloniaTheory]
    [InlineData(760, 480)]
    [InlineData(1000, 640)]
    public async Task Layout_Should_KeepFooterAndPackageActionsInsideTheWindow(double width, double height)
    {
        var rpad = RpadParser.TryParse(RpadWriter.NewFile("Demo"))!;
        var vm = CreateVm();
        var window = new DependenciesWindow(vm) { Width = width, Height = height };
        try
        {
            window.SetFrontMatter(rpad.FrontMatter);
            window.Show();
            vm.SearchText = "json";
            await WaitUntilAsync(() => vm.Packages.Count == 1);
            Find<ListBox>(window, "PackageList").SelectedIndex = 0;
            await WaitUntilAsync(() => !vm.IsLoadingVersions);
            window.UpdateLayout();

            var client = new Rect(window.ClientSize);
            Rect BoundsOf(Control c) => new(c.TranslatePoint(default, window)!.Value, c.Bounds.Size);

            foreach (var name in new[] { "OkButton", "CancelButton", "SearchBox" })
            {
                var bounds = BoundsOf(Find<Control>(window, name));
                Assert.True(bounds.Width > 0 && client.Contains(bounds), $"{name} {bounds} outside {client}");
            }

            // Package actions never clip horizontally; vertically the details pane scrolls.
            var details = BoundsOf(Find<ScrollViewer>(window, "DetailsScroll"));
            foreach (var name in new[] { "AddPackageButton", "UpdatePackageButton", "RemovePackageButton", "VersionBox" })
            {
                var bounds = BoundsOf(Find<Control>(window, name));
                Assert.True(bounds.Width > 0, name);
                Assert.True(bounds.X >= details.X && bounds.Right <= details.Right + 0.5, $"{name} {bounds} outside {details}");
            }
        }
        finally
        {
            CloseIfOpen(window);
        }
    }

    // Review pass 1, finding 3: a long Id, a long prerelease version and a long range badge must
    // stay inside the list at the minimum width, without overlapping each other.
    [AvaloniaFact]
    public async Task LongPackageRow_AtMinimumWidth_Should_TrimInsideTheList_WithoutOverlap()
    {
        const string id = "Contoso.Enterprise.Platform.Integration.Messaging.Transport.Extensions.VeryLongPackageName";
        const string version = "10.0.0-preview.20261006.1234567890+sha.0123456789abcdef0123456789abcdef";
        const string range = "[1.0.0-preview.1, 2.0.0-preview.999999)";
        var rpad = RpadParser.TryParse(RpadWriter.NewFile("Demo"))!;
        rpad.FrontMatter.Packages[id] = new Package(range);
        var vm = CreateVm([new PackageSearchResult(id, version, "A long description " + new string('x', 400), 1)], [version]);
        var window = new DependenciesWindow(vm) { Width = 760, Height = 480 };
        try
        {
            window.SetFrontMatter(rpad.FrontMatter);
            window.Show();
            vm.SearchText = "contoso";
            await WaitUntilAsync(() => vm.Packages.Count == 1);
            window.UpdateLayout();

            Rect BoundsOf(Visual v) => new(v.TranslatePoint(default, window)!.Value, v.Bounds.Size);
            Control Row(string cls) => window.GetVisualDescendants().OfType<Control>().Single(c => c.Classes.Contains(cls));

            var list = BoundsOf(Find<ListBox>(window, "PackageList"));
            var idBlock = BoundsOf(Row("package-id"));
            var versionBlock = BoundsOf(Row("package-version"));
            var badge = BoundsOf(Row("package-badge"));

            foreach (var (name, bounds) in new[] { ("id", idBlock), ("version", versionBlock), ("badge", badge) })
            {
                Assert.True(bounds.Width > 0, name);
                Assert.True(bounds.X >= list.X && bounds.Right <= list.Right + 0.5, $"{name} {bounds} outside {list}");
            }
            Assert.True(idBlock.Right <= versionBlock.X + 0.5, $"id {idBlock} overlaps version {versionBlock}");
            Assert.True(versionBlock.Right <= badge.X + 0.5, $"version {versionBlock} overlaps badge {badge}");
            Assert.Equal(id, ToolTip.GetTip(Row("package-id")));
            Assert.Equal(version, ToolTip.GetTip(Row("package-version")));
            Assert.Equal($"In script: {range}", ToolTip.GetTip(Row("package-badge")));
        }
        finally
        {
            CloseIfOpen(window);
        }
    }

    [AvaloniaFact]
    public void HasFrontMatter_Should_BecomeTrue_When_ActiveTabFinishesLoadingAfterDialogOpens()
    {
        // Arrange: EditorTabsControl loads the Roslyn workspace/BCL metadata for a newly-opened tab
        // off the UI thread (EditorTabsControl.CreateControlAsync), so EditorTabItemViewModel.FrontMatter
        // can still be null at the instant the Dependencies window is opened for that (already-active)
        // tab. WindowService.ShowDependenciesAsync subscribes to the tab's FrontMatter stream instead of
        // taking a one-time snapshot specifically so this doesn't leave the dialog stuck disabled.
        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
        prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
        var tab = new EditorTabItemViewModel(
            filePath: "demo.rpad", sdks: [], prefs: prefs.Object, actions: new Mock<IEditorTabActions>().Object);

        var vm = new DependenciesWindowViewModel(
            new Mock<IPackageSearchService>().Object,
            new Mock<ILocalPackageCacheService>().Object);
        var window = new DependenciesWindow(vm);
        try
        {
            // Simulates WindowService.ShowDependenciesAsync's subscription (rather than a one-time snapshot).
            using var subscription = tab.WhenAnyValue(x => x.FrontMatter).Subscribe(window.SetFrontMatter);

            // Dialog opens before the tab's async load has set FrontMatter.
            Assert.False(vm.HasFrontMatter);

            // Act: the async tab load completes and assigns the real front matter moments later.
            var rpad = RpadParser.TryParse(RpadWriter.NewFile("Demo"));
            tab.FrontMatter = rpad!.FrontMatter;

            // Assert
            Assert.True(vm.HasFrontMatter);
        }
        finally
        {
            vm.Dispose();
            tab.Dispose();
        }
    }
}
