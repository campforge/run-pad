using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Invoklet.Contracts;
using Invoklet.Format.MdRun;
using Invoklet.Services.Preferences;
using Invoklet.UserControls.Models;
using Invoklet.Windows;
using Invoklet.Windows.Models;
using Moq;
using ReactiveUI;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

// Real DependenciesWindow + bindings, headless. Pure VM logic lives in
// Invoklet.NonUI.Tests/DependenciesWindowViewModelLogicTests.
public class DependenciesWindowViewModelTests
{
    private static DependenciesWindowViewModel CreateVm(
        IReadOnlyList<PackageSearchResult>? results = null, IReadOnlyList<string>? versions = null,
        IReadOnlyList<CachedPackageInfo>? cached = null, INuGetSourceStore? sources = null, PackageDetails? details = null)
    {
        var search = new Mock<IPackageSearchService>();
        search.Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<PackageSearchOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PackageSearchPage(results ?? [new PackageSearchResult("Newtonsoft.Json", "13.0.3", "desc", 100)], HasMore: false));
        search.Setup(s => s.GetVersionsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(versions ?? ["13.0.3", "13.0.1", "12.0.3"]);
        search.Setup(s => s.GetDetailsAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(details);
        var cache = new Mock<ILocalPackageCacheService>();
        cache.Setup(c => c.GetCachedPackages()).Returns(cached ?? []);
        return new DependenciesWindowViewModel(search.Object, cache.Object, sources ?? Sources().Object, TimeSpan.Zero);
    }

    private static Mock<INuGetSourceStore> Sources(params NuGetSource[] sources)
    {
        var store = new Mock<INuGetSourceStore>();
        store.Setup(s => s.Load()).Returns(sources.Length > 0
            ? sources
            : [new NuGetSource("nuget.org", "https://api.nuget.org/v3/index.json")]);
        return store;
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
        foreach (var owned in window.OwnedWindows.ToList())
            owned.Close();
        if (window.IsVisible) window.Close();
    }

    // The "save pending changes?" question Cancel/Escape/the close button raise.
    private static UnsavedChangesDialog SaveQuestion(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        return Assert.IsType<UnsavedChangesDialog>(Assert.Single(window.OwnedWindows));
    }

    private static void Choose(Window dialog, string button)
        => Click(dialog, dialog.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, button)));

    [AvaloniaFact]
    public async Task AddingASearchResult_Should_ChangeOnlyTheDraft_UntilOkIsClicked()
    {
        var rpad = MdRunParser.TryParse(MdRunWriter.NewFile("Console.WriteLine(\"Hello, RPad!\");\n", "Demo"))!;
        var vm = CreateVm();
        var window = new DependenciesWindow(vm);
        try
        {
            window.SetFrontMatter(rpad.FrontMatter);
            window.Show();

            Assert.True(Find<TextBox>(window, "SearchBox").IsFocused); // initial focus: online search
            window.KeyTextInput("Newtonsoft.Json");
            await WaitUntilAsync(() => vm.OnlinePackages.Count == 1);

            // Select through the real ListBox -> details/versions/buttons via bindings.
            Find<ListBox>(window, "OnlinePackageList").SelectedIndex = 0;
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
            Assert.Contains(vm.LocalPackages, p => p.Id == "Newtonsoft.Json" && p.IsInScript); // shown on the left
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
    public async Task ThePackageCard_Should_ShowDependenciesLicenseAndProjectPage_AndOpenItsLinks()
    {
        var details = new PackageDetails(
            [new PackageDependencyGroupInfo("net8.0", [new PackageDependencyInfo("System.Memory", ">= 4.5.5")])],
            "MIT", "https://licenses.nuget.org/MIT", "https://www.newtonsoft.com/json");
        var vm = CreateVm(details: details);
        var window = new DependenciesWindow(vm);
        var opened = new List<Uri>();
        vm.LinkLauncher = uri =>
        {
            opened.Add(uri);
            return Task.CompletedTask;
        };
        try
        {
            window.SetFrontMatter(MdRunParser.TryParse(MdRunWriter.NewFile("Console.WriteLine();\n", "Demo"))!.FrontMatter);
            window.Show();
            window.KeyTextInput("Newtonsoft.Json");
            await WaitUntilAsync(() => vm.OnlinePackages.Count == 1);
            Find<ListBox>(window, "OnlinePackageList").SelectedIndex = 0;
            await WaitUntilAsync(() => !vm.IsLoadingDetails && vm.HasDetailsDependencies);

            Assert.True(Find<StackPanel>(window, "DetailsLicensePanel").IsEffectivelyVisible);
            Assert.True(Find<StackPanel>(window, "DetailsProjectPanel").IsEffectivelyVisible);
            Assert.Equal(["net8.0", "System.Memory (>= 4.5.5)"],
                Find<ItemsControl>(window, "DetailsDependencyList").GetVisualDescendants().OfType<TextBlock>()
                    .Where(t => t.IsEffectivelyVisible).Select(t => t.Text));
            Assert.False(Find<TextBlock>(window, "NoDependenciesText").IsEffectivelyVisible);

            Click(window, Find<Button>(window, "ProjectLinkButton"));
            Click(window, Find<Button>(window, "LicenseLinkButton"));
            await WaitUntilAsync(() => opened.Count == 2);
            Assert.Equal(["https://www.newtonsoft.com/json", "https://licenses.nuget.org/MIT"], opened.Select(u => u.AbsoluteUri));
        }
        finally
        {
            CloseIfOpen(window);
        }
    }

    [AvaloniaFact]
    public async Task ThePackageCard_Should_HideTheDetailSections_WhenTheFeedSuppliesNone()
    {
        var vm = CreateVm();
        var window = new DependenciesWindow(vm);
        try
        {
            window.SetFrontMatter(MdRunParser.TryParse(MdRunWriter.NewFile("Console.WriteLine();\n", "Demo"))!.FrontMatter);
            window.Show();
            window.KeyTextInput("Newtonsoft.Json");
            await WaitUntilAsync(() => vm.OnlinePackages.Count == 1);
            Find<ListBox>(window, "OnlinePackageList").SelectedIndex = 0;
            await WaitUntilAsync(() => !vm.IsLoadingVersions && !vm.IsLoadingDetails && vm.AvailableVersions.Count == 3);

            Assert.False(Find<StackPanel>(window, "DetailsLicensePanel").IsEffectivelyVisible);
            Assert.False(Find<StackPanel>(window, "DetailsProjectPanel").IsEffectivelyVisible);
            Assert.False(Find<ItemsControl>(window, "DetailsDependencyList").IsEffectivelyVisible);
            Assert.False(Find<TextBlock>(window, "NoDependenciesText").IsEffectivelyVisible);
            // The package can be added as before.
            Assert.True(Find<Button>(window, "AddPackageButton").IsEffectivelyEnabled);
        }
        finally
        {
            CloseIfOpen(window);
        }
    }

    [AvaloniaFact]
    public async Task RowAddButton_Should_AddTheLatestVersion_FromTheOnlineList()
    {
        var rpad = MdRunParser.TryParse(MdRunWriter.NewFile("Console.WriteLine(\"Hello, RPad!\");\n", "Demo"))!;
        var vm = CreateVm();
        // The row's action is an IconButton, whose template the app registers (App.axaml).
        var iconButtons = new global::Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://Invoklet/"))
        {
            Source = new Uri("avares://Invoklet/TemplatedControls/IconButton.axaml"),
        };
        Application.Current!.Styles.Add(iconButtons);
        var window = new DependenciesWindow(vm);
        try
        {
            window.SetFrontMatter(rpad.FrontMatter);
            window.Show();
            vm.SearchText = "json";
            await WaitUntilAsync(() => vm.OnlinePackages.Count == 1);
            window.UpdateLayout();

            var rowAdd = Find<ListBox>(window, "OnlinePackageList").GetVisualDescendants().OfType<Button>()
                .Single(b => b.Classes.Contains("row") && b.IsVisible);
            // An icon button: what it does is in its accessible name and tooltip.
            Assert.Equal("Add version 13.0.3 to the script", global::Avalonia.Automation.AutomationProperties.GetName(rowAdd));
            Click(window, rowAdd);

            Assert.True(vm.OnlinePackages[0].IsInScript);
            Click(window, Find<Button>(window, "OkButton"));
            Assert.Equal("13.0.3", rpad.FrontMatter.Packages["Newtonsoft.Json"].Version);
        }
        finally
        {
            Application.Current.Styles.Remove(iconButtons);
            CloseIfOpen(window);
        }
    }

    [AvaloniaFact]
    public async Task EnterInTheNamespacesBox_Should_AddALine_InvalidLine_Should_DisableSave_AndEscape_Should_AskBeforeDiscarding()
    {
        var rpad = MdRunParser.TryParse(MdRunWriter.NewFile("Console.WriteLine(\"Hello, RPad!\");\n", "Demo"))!;
        var namespacesBefore = rpad.FrontMatter.Namespaces?.ToList() ?? [];
        var vm = CreateVm();
        var window = new DependenciesWindow(vm);
        try
        {
            window.SetFrontMatter(rpad.FrontMatter);
            window.Show();

            window.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = 2;
            Dispatcher.UIThread.RunJobs();
            var box = Find<TextBox>(window, "NamespacesBox");
            box.Focus();
            box.CaretIndex = box.Text?.Length ?? 0;
            window.KeyPress(Key.Return, RawInputModifiers.None, PhysicalKey.Enter, null);
            window.KeyTextInput("System.Linq");
            Dispatcher.UIThread.RunJobs();

            // Enter is a new line in the text area — it neither closes nor applies the dialog.
            Assert.True(window.IsVisible);
            Assert.Contains("System.Linq", vm.NamespacesText);
            Assert.True(vm.HasChanges);

            // Invalid line: inline message, OK disabled, still open.
            window.KeyPress(Key.Return, RawInputModifiers.None, PhysicalKey.Enter, null);
            window.KeyTextInput("not a namespace");
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.IsVisible);
            Assert.True(vm.HasNamespaceError);
            Assert.False(Find<Button>(window, "OkButton").IsEnabled);

            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Choose(SaveQuestion(window), "Don’t Save");
            await WaitUntilAsync(() => !window.IsVisible);

            Assert.Equal(namespacesBefore, rpad.FrontMatter.Namespaces ?? []);
        }
        finally
        {
            CloseIfOpen(window);

        }
    }

    [AvaloniaFact]
    public void Save_Should_StayDisabled_UntilSomethingChanges_AndCancelWithoutChanges_Should_CloseAtOnce()
    {
        var rpad = MdRunParser.TryParse(MdRunWriter.NewFile("Console.WriteLine(\"Hello, RPad!\");\n", "Demo"))!;
        var vm = CreateVm();
        var window = new DependenciesWindow(vm);
        try
        {
            window.SetFrontMatter(rpad.FrontMatter);
            window.Show();
            var save = Find<Button>(window, "OkButton");
            Assert.Equal("Save", save.Content);
            Assert.False(save.IsEffectivelyEnabled);

            Assert.True(vm.AddReference("lib/My.dll"));
            Dispatcher.UIThread.RunJobs();
            Assert.True(save.IsEffectivelyEnabled);
            ((System.Windows.Input.ICommand)vm.References.Single().RemoveCommand).Execute(null); // back to the original
            Dispatcher.UIThread.RunJobs();
            Assert.False(save.IsEffectivelyEnabled);

            Click(window, Find<Button>(window, "CancelButton"));

            Assert.False(window.IsVisible); // no question without changes
        }
        finally
        {
            CloseIfOpen(window);
        }
    }

    [AvaloniaTheory]
    [InlineData("Don’t Save", false, false)]
    [InlineData("Save", false, true)]
    [InlineData("Cancel", true, false)]
    public async Task CancelWithChanges_Should_AskToSave(string answer, bool staysOpen, bool applied)
    {
        var rpad = MdRunParser.TryParse(MdRunWriter.NewFile("Console.WriteLine(\"Hello, RPad!\");\n", "Demo"))!;
        var vm = CreateVm();
        var window = new DependenciesWindow(vm);
        try
        {
            window.SetFrontMatter(rpad.FrontMatter);
            window.Show();
            Assert.True(vm.AddReference("lib/My.dll"));

            Click(window, Find<Button>(window, "CancelButton"));
            Choose(SaveQuestion(window), answer);
            if (staysOpen)
                await WaitUntilAsync(() => window.OwnedWindows.Count == 0);
            else
                await WaitUntilAsync(() => !window.IsVisible);

            Assert.Equal(staysOpen, window.IsVisible);
            Assert.Equal(applied, rpad.FrontMatter.References is { Count: > 0 });
        }
        finally
        {
            CloseIfOpen(window);
        }
    }

    [AvaloniaFact]
    public async Task NamespacesBox_Should_FillTheTabHeight()
    {
        var vm = CreateVm();
        var window = new DependenciesWindow(vm) { Width = 1000, Height = 640 };
        try
        {
            window.Show();
            window.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = 2;
            await WaitUntilAsync(() => Find<TextBox>(window, "NamespacesBox").Bounds.Height > 0);
            window.UpdateLayout();

            Assert.True(Find<TextBox>(window, "NamespacesBox").Bounds.Height > window.ClientSize.Height / 2,
                $"NamespacesBox is only {Find<TextBox>(window, "NamespacesBox").Bounds.Height}px tall");
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
        var rpad = MdRunParser.TryParse(MdRunWriter.NewFile("Console.WriteLine(\"Hello, RPad!\");\n", "Demo"))!;
        var vm = CreateVm();
        var window = new DependenciesWindow(vm) { Width = width, Height = height };
        try
        {
            window.SetFrontMatter(rpad.FrontMatter);
            window.Show();
            vm.SearchText = "json";
            await WaitUntilAsync(() => vm.OnlinePackages.Count == 1);
            Find<ListBox>(window, "OnlinePackageList").SelectedIndex = 0;
            await WaitUntilAsync(() => !vm.IsLoadingVersions);
            window.UpdateLayout();

            var client = new Rect(window.ClientSize);
            Rect BoundsOf(Control c) => new(c.TranslatePoint(default, window)!.Value, c.Bounds.Size);

            foreach (var name in new[] { "OkButton", "CancelButton", "SearchBox", "LocalFilterBox", "PrereleaseBox", "SourceBox", "PreviousPageButton", "NextPageButton" })
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

    // A long Id, a long prerelease version and a long range stay inside their list at the minimum
    // width, and the row buttons stay visible instead of being pushed out.
    [AvaloniaFact]
    public async Task LongPackageRows_AtMinimumWidth_Should_StayInsideTheirLists()
    {
        const string id = "Contoso.Enterprise.Platform.Integration.Messaging.Transport.Extensions.VeryLongPackageName";
        const string version = "10.0.0-preview.20261006.1234567890+sha.0123456789abcdef0123456789abcdef";
        const string range = "[1.0.0-preview.1, 2.0.0-preview.999999)";
        var rpad = MdRunParser.TryParse(MdRunWriter.NewFile("Console.WriteLine(\"Hello, RPad!\");\n", "Demo"))!;
        rpad.FrontMatter.Packages[id] = new Package(range);
        var vm = CreateVm([new PackageSearchResult(id, version, "A long description " + new string('x', 400), 1)], [version],
            cached: [new CachedPackageInfo(id + ".Cached", version)]);
        var window = new DependenciesWindow(vm) { Width = 760, Height = 480 };
        try
        {
            window.SetFrontMatter(rpad.FrontMatter);
            window.Show();
            vm.SearchText = "contoso";
            await WaitUntilAsync(() => vm.OnlinePackages.Count == 1);
            window.UpdateLayout();

            Rect BoundsOf(Visual v) => new(v.TranslatePoint(default, window)!.Value, v.Bounds.Size);

            foreach (var listName in new[] { "OnlinePackageList", "LocalPackageList" })
            {
                var list = Find<ListBox>(window, listName);
                var listBounds = BoundsOf(list);
                var visible = list.GetVisualDescendants().OfType<Control>()
                    .Where(c => c is TextBlock or Button && c.IsEffectivelyVisible && c.Bounds.Width > 0)
                    .ToList();
                Assert.Contains(visible, c => c is Button);
                foreach (var control in visible)
                {
                    var bounds = BoundsOf(control);
                    Assert.True(bounds.X >= listBounds.X - 0.5 && bounds.Right <= listBounds.Right + 0.5,
                        $"{listName}: {control.GetType().Name} {bounds} outside {listBounds}");
                }
            }
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
            new Mock<ILocalPackageCacheService>().Object,
            Sources().Object);
        var window = new DependenciesWindow(vm);
        try
        {
            // Simulates WindowService.ShowDependenciesAsync's subscription (rather than a one-time snapshot).
            using var subscription = tab.WhenAnyValue(x => x.FrontMatter).Subscribe(window.SetFrontMatter);

            // Dialog opens before the tab's async load has set FrontMatter.
            Assert.False(vm.HasFrontMatter);

            // Act: the async tab load completes and assigns the real front matter moments later.
            var rpad = MdRunParser.TryParse(MdRunWriter.NewFile("Console.WriteLine(\"Hello, RPad!\");\n", "Demo"));
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

    // "(configure sources...)" is a command item: the selection snaps back to the current source and
    // the configurator opens as a dialog over the Dependencies window; saving reloads the sources.
    [AvaloniaFact]
    public async Task ChoosingConfigureSources_Should_OpenTheConfigurator_AndKeepTheCurrentSource()
    {
        var store = Sources(new NuGetSource("nuget.org", "https://api.nuget.org/v3/index.json"));
        var vm = CreateVm(sources: store.Object);
        var window = new DependenciesWindow(vm);
        try
        {
            window.Show();
            var sourceBox = Find<ComboBox>(window, "SourceBox");
            Assert.Same(PackageSourceOption.AllSources, sourceBox.SelectedItem);

            sourceBox.SelectedItem = PackageSourceOption.Configure;
            Dispatcher.UIThread.RunJobs();

            Assert.Same(PackageSourceOption.AllSources, sourceBox.SelectedItem);
            Assert.Same(PackageSourceOption.AllSources, vm.SelectedSourceOption);
            var configurator = Assert.IsType<NuGetSourcesWindow>(Assert.Single(window.OwnedWindows));

            // Save a second source; the Dependencies window offers it right away.
            store.Setup(s => s.Save(It.IsAny<IReadOnlyList<NuGetSource>>()))
                .Callback<IReadOnlyList<NuGetSource>>(saved => store.Setup(s => s.Load()).Returns(saved));
            var editor = (NuGetSourcesWindowViewModel)configurator.DataContext!;
            Click(configurator, Find<Button>(configurator, "AddButton"));
            editor.SelectedSource!.Url = "https://corp.example/nuget/v3/index.json";
            Click(configurator, Find<Button>(configurator, "OkButton"));
            await WaitUntilAsync(() => !configurator.IsVisible);
            await WaitUntilAsync(() => vm.SourceOptions.Any(o => o.SourceName == "Package source"));

            Assert.Equal(["All sources", "nuget.org", "Package source", "(configure sources...)"],
                vm.SourceOptions.Select(o => o.Label));
        }
        finally
        {
            foreach (var owned in window.OwnedWindows.ToList())
                owned.Close();
            CloseIfOpen(window);
        }
    }

    [AvaloniaFact]
    public void SourcesWindow_Should_EditReorderAndValidate_AndSaveOnlyOnOk()
    {
        var store = Sources(
            new NuGetSource("nuget.org", "https://api.nuget.org/v3/index.json"),
            new NuGetSource("artifactory", "https://artifactory.example/api/nuget/v3/index.json", IsEnabled: false,
                Username: "me", Password: "token"));
        var vm = new NuGetSourcesWindowViewModel(store.Object);
        var window = new NuGetSourcesWindow(vm);
        try
        {
            window.Show();
            Assert.Equal("nuget.org", vm.SelectedSource?.Name);
            Assert.True(Find<Button>(window, "RemoveButton").IsEffectivelyEnabled);

            // Move artifactory to the top through the real buttons.
            vm.SelectedSource = vm.Sources[1];
            Assert.True(vm.SelectedSource.UseBasicAuth);
            Click(window, window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Move to Top")));
            Assert.Equal(["artifactory", "nuget.org"], vm.Sources.Select(s => s.Name));

            // A duplicate name blocks OK with an inline message.
            vm.SelectedSource.Name = "NuGet.org";
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.HasValidationError);
            Assert.False(Find<Button>(window, "OkButton").IsEffectivelyEnabled);
            vm.SelectedSource.Name = "artifactory";
            Dispatcher.UIThread.RunJobs();
            Assert.True(Find<Button>(window, "OkButton").IsEffectivelyEnabled);

            IReadOnlyList<NuGetSource>? saved = null;
            store.Setup(s => s.Save(It.IsAny<IReadOnlyList<NuGetSource>>()))
                .Callback<IReadOnlyList<NuGetSource>>(s => saved = s);
            Click(window, Find<Button>(window, "OkButton"));

            Assert.False(window.IsVisible);
            Assert.Equal(
                [
                    new NuGetSource("artifactory", "https://artifactory.example/api/nuget/v3/index.json", false, "me", "token"),
                    new NuGetSource("nuget.org", "https://api.nuget.org/v3/index.json"),
                ],
                saved);
        }
        finally
        {
            CloseIfOpen(window);
        }
    }

    // The real dialog offers "Allow unsafe code" on a Compilation tab for C# scripts only, explains
    // that it isn't a sandbox, and the choice reaches the script's metadata on Save.
    [AvaloniaTheory]
    [InlineData(MdRunLanguage.CSharp, true)]
    [InlineData(MdRunLanguage.Vb, false)]
    public void TheCompilationTab_Should_OfferUnsafeCode_ForCSharpOnly(MdRunLanguage language, bool offered)
    {
        var rpad = MdRunParser.TryParse(MdRunWriter.NewFile("", "Demo", language: language))!;
        var vm = CreateVm();
        var window = new DependenciesWindow(vm);
        try
        {
            window.SetFrontMatter(rpad.FrontMatter);
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(offered, Find<TabItem>(window, "CompilationTab").IsVisible);
            if (!offered) return;
            Assert.Contains("not a sandbox", Find<TextBlock>(window, "AllowUnsafeHint").Text);
            var box = Find<CheckBox>(window, "AllowUnsafeBox");
            Assert.False(box.IsChecked);
            box.IsChecked = true;
            Assert.True(Find<Button>(window, "OkButton").IsEnabled);
            Assert.True(vm.ApplyChanges());
            Assert.True(rpad.FrontMatter.AllowUnsafe);
            Assert.Contains("allowUnsafe: true", MdRunWriter.Write(rpad));
        }
        finally
        {
            CloseIfOpen(window);
        }
    }
}
