using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Moq;
using RunPad.Avalonia.Themes;
using RunPad.Contracts;
using RunPad.Contracts.IntelliSense;
using RunPad.Contracts.Models;
using RunPad.Format.RPad;
using RunPad.Languages.CSharp;
using RunPad.Languages.DotnetCommon;
using RunPad.Services;
using RunPad.Services.BuildCache;
using RunPad.Services.Preferences;
using RunPad.UserControls;
using RunPad.UserControls.Models;
using RunPad.Windows.Models;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// A real tab on a temporary script with relative references: its run options and IntelliSense get
// the files next to the script (not next to the process), Save As into another folder rewrites the
// relative references so they still reach the same files, and a Save As that doesn't happen —
// picker cancelled, write failed, conflict cancelled — leaves the tab exactly as it was.
public class ReferencePathTabTests : IDisposable
{
    private static readonly DotnetSdk Sdk = new("10.0.100", "net10.0", "10.0.0", ".NET 10.0");

    private readonly string _root = Directory.CreateTempSubdirectory("runpad-reference-tab").FullName;
    private readonly string _oldDir;
    private readonly string _newDir;
    private readonly string _script;
    private readonly string _library;
    private readonly Mock<IWindowService> _windows = new();
    private readonly Mock<IIntelliSenseService> _intelliSense = new();
    private ExecutionRequest? _runRequest;

    private static readonly string[] References =
        ["lib/Greeter.dll", "$(CurrentQueryPath)/lib/Greeter.dll", "$(MyDocuments)/Shared/Other.dll"];

    public ReferencePathTabTests()
    {
        _intelliSense.ForwardingExecutionContext();
        _oldDir = Directory.CreateDirectory(Path.Combine(_root, "old")).FullName;
        _newDir = Directory.CreateDirectory(Path.Combine(_root, "new", "deeper")).FullName;
        _library = Path.Combine(Directory.CreateDirectory(Path.Combine(_oldDir, "lib")).FullName, "Greeter.dll");
        File.WriteAllText(_library, "");
        _script = Path.Combine(_oldDir, "query.rpad");
        var text = RpadWriter.NewFile("Console.WriteLine(1);\n", "Demo");
        var document = RpadParser.TryParse(text)!;
        document.FrontMatter.References = [.. References];
        File.WriteAllText(_script, RpadWriter.Write(document));
    }

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows())
            foreach (var dir in Directory.GetDirectories(_root, "*", SearchOption.AllDirectories))
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Directory.Delete(_root, recursive: true);
    }

    private (EditorsTabItemControl Ctrl, EditorTabItemViewModel Vm, MainWindowViewModel Shell, Window Host) Open(bool untitled = false)
    {
        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Editor).Returns(new EditorSettings());
        prefs.Setup(p => p.CSharp).Returns(new CSharpLanguageSettings());
        prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
        prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
        prefs.Setup(p => p.Session).Returns(new SessionSettings());
        prefs.Setup(p => p.Debugger).Returns(new DebuggerSettings());
        var theme = new Mock<IThemeService>();
        theme.Setup(t => t.CurrentTheme).Returns(new ActiveTheme(
            new InterfaceTheme("Test", ThemeVariant.Dark, true),
            new EditorTheme("Test", TextMateSharp.Grammars.ThemeName.DarkPlus, true)));
        var fileSystem = new Mock<IFileSystemService>();
        fileSystem.Setup(f => f.Folder).Returns(_root);
        fileSystem.Setup(f => f.GetFilesTreeAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var shell = new MainWindowViewModel([], fileSystem.Object, _windows.Object, prefs.Object, new LanguageHostRegistry());

        var host = new Mock<ILanguageHost>();
        host.Setup(h => h.RunAsync(It.IsAny<string>(), It.IsAny<OutputReceivedHandler>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RunResult.Exited(0));
        host.Setup(h => h.DisposeAsync()).Returns(ValueTask.CompletedTask);
        var registry = new Mock<ILanguageHostRegistry>();
        registry.Setup(r => r.Create(It.IsAny<string>(), It.IsAny<ExecutionRequest>()))
            .Callback<string, ExecutionRequest>((_, request) => _runRequest = request).Returns(host.Object);

        var vm = new EditorTabItemViewModel(untitled ? null : _script, [Sdk], prefs.Object, shell, RpadLanguage.CSharp, RpadKind.Statements);
        var buildCache = new Mock<IBuildCacheService>();
        var output = Path.Combine(_root, "build");
        buildCache.Setup(b => b.EnsureOutputDirectory(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(() => Directory.CreateDirectory(output).FullName);
        var ctrl = new EditorsTabItemControl(vm, theme.Object, _windows.Object, registry.Object, prefs.Object,
            _intelliSense.Object, buildCache.Object);
        var window = new Window { Content = ctrl };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (ctrl, vm, shell, window);
    }

    private static List<string> ReferencesIn(string path) => RpadParser.TryParse(File.ReadAllText(path))!.FrontMatter.References!;

    [AvaloniaFact]
    public async Task RunOptions_AndIntelliSense_Should_GetTheReferenceNextToTheScript()
    {
        var (ctrl, _, _, window) = Open();
        try
        {
            Assert.NotEqual(_oldDir, Directory.GetCurrentDirectory());
            await TestWait.EventuallyAsync(() => _intelliSense.Verify(s => s.UpdatePackageReferences(It.IsAny<IReadOnlyDictionary<string, Package>>(),
                It.Is<PackageReferenceContext>(c => c.AssemblyReferences != null
                    && c.AssemblyReferences.SequenceEqual(new[] { _library, _library }))), Times.AtLeastOnce()));

            await ctrl.RunAsync(debug: false, Sdk);

            // The options the backend builds from the tab's request, as the run gets them.
            var options = CompiledDotnetLanguageHostOptions.From(Assert.IsType<ExecutionRequest>(_runRequest), "RunPad.Runtime.dll");
            Assert.Equal([_library, _library], options.AssemblyReferences);
            Assert.Empty(options.ReferenceErrors);
        }
        finally { window.Close(); ctrl.Dispose(); }
    }

    [AvaloniaFact]
    public async Task SaveAs_IntoAnotherFolder_Should_RebaseRelativeReferences_InTheNewFileAndTheTab()
    {
        var (ctrl, vm, _, window) = Open();
        try
        {
            await TestWait.EventuallyAsync(() => _intelliSense.Verify(s => s.UpdatePackageReferences(
                It.IsAny<IReadOnlyDictionary<string, Package>>(),
                It.Is<PackageReferenceContext>(c => c.AssemblyReferences != null
                    && c.AssemblyReferences.SequenceEqual(new[] { _library, _library }))), Times.AtLeastOnce()));

            var newScript = Path.Combine(_newDir, "query.rpad");
            _windows.Setup(w => w.ShowSaveFileDialogAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(newScript);
            var original = File.ReadAllBytes(_script);

            await vm.SaveAsCommand.Execute();
            Dispatcher.UIThread.RunJobs();

            string[] expected = ["../../old/lib/Greeter.dll", "$(CurrentQueryPath)/../../old/lib/Greeter.dll", "$(MyDocuments)/Shared/Other.dll"];
            Assert.Equal(expected, ReferencesIn(newScript));
            Assert.Equal(original, File.ReadAllBytes(_script));
            Assert.Equal(newScript, vm.FilePath);
            Assert.Equal(expected, ctrl.State.RpadDocument!.FrontMatter.References);
            Assert.False(vm.IsDirty);
            // Still the same file, now from the new folder.
            Assert.Equal([_library, _library], ReferencePathResolver.Resolve(ctrl.State.RpadDocument.FrontMatter.References, vm.FilePath).Assemblies);
            await TestWait.EventuallyAsync(() => _intelliSense.Verify(s => s.UpdatePackageReferences(It.IsAny<IReadOnlyDictionary<string, Package>>(),
                It.Is<PackageReferenceContext>(c => c.AssemblyReferences != null && c.AssemblyReferences.Count == 2)), Times.AtLeast(2)));

            // A plain save afterwards keeps them as they are now.
            ctrl.SaveIfNeeded();
            Assert.Equal(expected, ReferencesIn(newScript));
        }
        finally { window.Close(); ctrl.Dispose(); }
    }

    [AvaloniaFact]
    public async Task SaveAs_Cancelled_Should_LeaveTheTabAndTheFile()
    {
        var (ctrl, vm, _, window) = Open();
        try
        {
            vm.IsDirty = true;
            _windows.Setup(w => w.ShowSaveFileDialogAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync((string?)null);
            var original = File.ReadAllBytes(_script);

            await vm.SaveAsCommand.Execute();

            Assert.Equal(_script, vm.FilePath);
            Assert.Equal(References, ctrl.State.RpadDocument!.FrontMatter.References);
            Assert.True(vm.IsDirty);
            Assert.Equal(original, File.ReadAllBytes(_script));
        }
        finally { window.Close(); ctrl.Dispose(); }
    }

    [AvaloniaFact]
    public async Task SaveAs_WithAFailedWrite_Should_LeaveTheReferencesAndThePath()
    {
        if (OperatingSystem.IsWindows()) return; // read-only folder through Unix file modes
        var (ctrl, vm, _, window) = Open();
        try
        {
            vm.IsDirty = true;
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_newDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            _windows.Setup(w => w.ShowSaveFileDialogAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(Path.Combine(_newDir, "query.rpad"));

            await vm.SaveAsCommand.Execute();

            _windows.Verify(w => w.ShowErrorAsync("Save Failed", It.IsAny<string>()), Times.Once);
            Assert.Equal(_script, vm.FilePath);
            Assert.Equal(References, ctrl.State.RpadDocument!.FrontMatter.References);
            Assert.True(vm.IsDirty);
        }
        finally { window.Close(); ctrl.Dispose(); }
    }

    // Save As onto its own file, changed by another program, then Cancel in the conflict question.
    [AvaloniaFact]
    public async Task SaveAs_ToItsOwnChangedFile_WithTheConflictCancelled_Should_LeaveEverything()
    {
        var (ctrl, vm, _, window) = Open();
        try
        {
            File.AppendAllText(_script, "\nchanged by another program\n");
            var changed = File.ReadAllBytes(_script);
            _windows.Setup(w => w.ShowSaveFileDialogAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(_script);
            _windows.Setup(w => w.ShowSaveConflictDialogAsync(It.IsAny<string>())).ReturnsAsync(SaveConflictChoice.Cancel);

            await vm.SaveAsCommand.Execute();

            Assert.Equal(changed, File.ReadAllBytes(_script));
            Assert.Equal(_script, vm.FilePath);
            Assert.Equal(References, ctrl.State.RpadDocument!.FrontMatter.References);
            Assert.True(vm.IsDirty);
        }
        finally { window.Close(); ctrl.Dispose(); }
    }

    // An untitled tab's relative references have no folder until its first Save As: once saved, the
    // editor must check the script against the files next to it straight away, without an edit.
    [AvaloniaFact]
    public async Task TheFirstSaveAs_OfAnUntitledTab_Should_GiveIntelliSenseItsReferences_AndReanalyse()
    {
        var calls = new System.Collections.Concurrent.ConcurrentQueue<string>();
        _intelliSense.Setup(s => s.UpdatePackageReferences(It.IsAny<IReadOnlyDictionary<string, Package>>(), It.IsAny<PackageReferenceContext>()))
            .Callback<IReadOnlyDictionary<string, Package>, PackageReferenceContext>((_, c) =>
                calls.Enqueue("context " + string.Join(",", c.AssemblyReferences ?? [])));
        _intelliSense.Setup(s => s.NotifyTextChanged(It.IsAny<string>())).Callback<string>(_ => calls.Enqueue("analyse"));
        var (ctrl, vm, _, window) = Open(untitled: true);
        try
        {
            ctrl.State.RpadDocument!.FrontMatter.References = ["lib/Greeter.dll", "$(CurrentQueryPath)/lib/Greeter.dll"];
            Assert.Empty(ReferencePathResolver.Resolve(ctrl.State.RpadDocument.FrontMatter.References, vm.FilePath).Assemblies);
            _windows.Setup(w => w.ShowSaveFileDialogAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(_script);
            calls.Clear();

            await vm.SaveAsCommand.Execute();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(_script, vm.FilePath);
            // Nothing to rebase from: the references are written as they are, now relative to this folder.
            Assert.Equal(["lib/Greeter.dll", "$(CurrentQueryPath)/lib/Greeter.dll"], ReferencesIn(_script));
            await TestWait.EventuallyAsync(() =>
            {
                var seen = calls.ToList();
                var context = seen.FindLastIndex(c => c == $"context {_library},{_library}");
                Assert.True(context >= 0, string.Join(" | ", seen));
                Assert.Contains("analyse", seen.Skip(context + 1));
            });
        }
        finally { window.Close(); ctrl.Dispose(); }
    }

    [AvaloniaFact]
    public async Task ACancelledFirstSaveAs_Should_NotGiveIntelliSenseAnyReference()
    {
        var contexts = new List<PackageReferenceContext>();
        _intelliSense.Setup(s => s.UpdatePackageReferences(It.IsAny<IReadOnlyDictionary<string, Package>>(), It.IsAny<PackageReferenceContext>()))
            .Callback<IReadOnlyDictionary<string, Package>, PackageReferenceContext>((_, c) => contexts.Add(c));
        var (ctrl, vm, _, window) = Open(untitled: true);
        try
        {
            ctrl.State.RpadDocument!.FrontMatter.References = ["lib/Greeter.dll"];
            _windows.Setup(w => w.ShowSaveFileDialogAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync((string?)null);
            contexts.Clear();

            await vm.SaveAsCommand.Execute();
            Dispatcher.UIThread.RunJobs();

            Assert.Null(vm.FilePath);
            Assert.DoesNotContain(contexts, c => c.AssemblyReferences?.Count > 0);
            Assert.Equal(["lib/Greeter.dll"], ctrl.State.RpadDocument.FrontMatter.References);
        }
        finally { window.Close(); ctrl.Dispose(); }
    }

    // Duplicate writes the copy next to the original, so its relative references stay as written.
    [AvaloniaFact]
    public void Duplicate_Should_KeepTheReferences_AsTheCopyIsInTheSameFolder()
    {
        var (ctrl, vm, shell, window) = Open();
        try
        {
            ((IEditorTabActions)shell).Duplicate(vm);

            var copy = Path.Combine(_oldDir, "query copy.rpad");
            Assert.Equal(References, ReferencesIn(copy));
            Assert.Equal(_script, vm.FilePath);
            Assert.Equal(References, ctrl.State.RpadDocument!.FrontMatter.References);
        }
        finally { window.Close(); ctrl.Dispose(); }
    }
}
