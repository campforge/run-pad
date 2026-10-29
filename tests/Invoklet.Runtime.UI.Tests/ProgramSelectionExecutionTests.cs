using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Invoklet.Avalonia.Themes;
using Invoklet.Contracts;
using Invoklet.Contracts.IntelliSense;
using Invoklet.Contracts.Models;
using Invoklet.Format.RPad;
using Invoklet.Languages.CSharp;
using Invoklet.Languages.DotnetCommon.Sdk;
using Invoklet.Languages.VB;
using Invoklet.Services;
using Invoklet.Services.BuildCache;
using Invoklet.Services.Preferences;
using Invoklet.UserControls;
using Invoklet.UserControls.Models;
using Invoklet.Windows;
using Invoklet.Windows.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

// Execute Selection in a Program script runs only the selected statements, as Statements: through
// the configured key in the real window, and with the real language hosts and a real process.
[Trait("Category", "Integration")]
[Trait("Category", "RealBuild")]
public sealed class ProgramSelectionExecutionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("Invoklet-program-selection").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static Mock<IPreferencesService> Prefs(KeyboardSettings? keyboard = null)
    {
        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Editor).Returns(new EditorSettings());
        prefs.Setup(p => p.CSharp).Returns(new CSharpLanguageSettings());
        prefs.Setup(p => p.Keyboard).Returns(keyboard ?? new KeyboardSettings());
        prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
        prefs.Setup(p => p.Session).Returns(new SessionSettings());
        prefs.Setup(p => p.Debugger).Returns(new DebuggerSettings());
        return prefs;
    }

    private static Mock<IThemeService> Theme()
    {
        var theme = new Mock<IThemeService>();
        theme.Setup(t => t.CurrentTheme).Returns(new ActiveTheme(
            new InterfaceTheme("Test", ThemeVariant.Dark, true),
            new EditorTheme("Test", TextMateSharp.Grammars.ThemeName.DarkPlus, true)));
        return theme;
    }

    private sealed class FixedSdks(DotnetSdk sdk) : ISdkProvider
    {
        public IReadOnlyList<DotnetSdk> Get(string? dotnetExecutableOverride = null) => [sdk];
    }

    // The configured Execute Selection key in the main window, on a Program tab: the language gets a
    // Statements request with only the selection (blanks keep its offsets), and nothing else runs.
    [AvaloniaFact]
    public async Task TheExecuteSelectionKey_Should_RunTheSelectedStatementsOfAProgram_AsStatements()
    {
        var keyboard = new KeyboardSettings();
        var prefs = Prefs(keyboard);
        var requests = new List<ExecutionRequest>();
        var scripts = new List<string>();
        var host = new Mock<ILanguageHost>();
        host.Setup(h => h.RunAsync(It.IsAny<string>(), It.IsAny<OutputReceivedHandler>(), It.IsAny<CancellationToken>()))
            .Returns((string code, OutputReceivedHandler _, CancellationToken _) => { scripts.Add(code); return Task.FromResult(RunResult.Exited(0)); });
        host.Setup(h => h.DisposeAsync()).Returns(ValueTask.CompletedTask);
        var registry = new Mock<ILanguageHostRegistry>();
        registry.Setup(r => r.Create(It.IsAny<string>(), It.IsAny<ExecutionRequest>()))
            .Callback<string, ExecutionRequest>((_, r) => requests.Add(r)).Returns(host.Object);
        var files = new Mock<IFileSystemService>();
        files.Setup(f => f.Folder).Returns(_root);
        files.Setup(f => f.GetFilesTreeAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var cache = new Mock<IBuildCacheService>();
        cache.Setup(b => b.EnsureOutputDirectory(It.IsAny<string>(), It.IsAny<string>())).Returns(_root);
        var sdk = new DotnetSdk(".NET", "net10.0", "10.0.0", ".NET 10.0");
        var shell = new MainWindowViewModel([new FixedSdks(sdk)], files.Object, new Mock<IWindowService>().Object, prefs.Object, new LanguageHostRegistry());
        var tabs = new EditorTabsControl(Theme().Object, new Mock<IWindowService>().Object,
            registry.WithCSharpIntelliSense(() => new Mock<IIntelliSenseService>().Object), prefs.Object, cache.Object);
        var window = new MainWindow(prefs.Object, tabs, shell);
        window.Show();
        try
        {
            shell.NewRpadFile(RpadLanguage.CSharp, RpadKind.Program);
            var tab = shell.ActiveTab!;
            await TestWait.WhileAsync(() => tabs.ControlForTests(tab) is null);
            var ctrl = tabs.ControlForTests(tab)!;
            const string code = "void Main()\n{\n    Console.WriteLine(\"first\");\n    Console.WriteLine(\"second\");\n}\n";
            ctrl.State.Editor.Text = code;
            var start = code.IndexOf("Console.WriteLine(\"second\");", StringComparison.Ordinal);
            ctrl.State.Editor.Select(start, "Console.WriteLine(\"second\");".Length);
            ctrl.State.Editor.TextArea.Focus();

            var gesture = keyboard.GestureFor(KeyCommands.ExecuteSelection)!;
            var modifiers = (gesture.KeyModifiers.HasFlag(KeyModifiers.Control) ? RawInputModifiers.Control : 0)
                | (gesture.KeyModifiers.HasFlag(KeyModifiers.Meta) ? RawInputModifiers.Meta : 0)
                | (gesture.KeyModifiers.HasFlag(KeyModifiers.Shift) ? RawInputModifiers.Shift : 0)
                | (gesture.KeyModifiers.HasFlag(KeyModifiers.Alt) ? RawInputModifiers.Alt : 0);
            window.KeyPressQwerty(PhysicalKey.E, modifiers);
            await TestWait.WhileAsync(() => scripts.Count == 0, TimeSpan.FromSeconds(5));

            var request = Assert.Single(requests);
            Assert.Equal(RpadKind.Statements, request.Kind);
            var script = Assert.Single(scripts);
            Assert.Equal("Console.WriteLine(\"second\");", script.Trim());
            Assert.Equal(start, script.IndexOf("Console", StringComparison.Ordinal));
            Assert.DoesNotContain("first", script);
        }
        finally { window.Close(); }
    }

    private (EditorsTabItemControl Ctrl, Window Host)? Real(RpadLanguage language, string code, IReadOnlyList<string>? args = null)
    {
        if (new DotnetSdkProvider().Get().FirstOrDefault() is null) return null; // no .NET SDK to compile with
        var path = Path.Combine(_root, language == RpadLanguage.Vb ? "program-vb.rpad" : "program.rpad");
        var document = RpadWriter.NewFile(code, "Demo", kind: RpadKind.Program, language: language);
        if (args is { Count: > 0 })
        {
            var parsed = RpadParser.Parse(document).Document!;
            parsed.FrontMatter.Arguments = [new() { Name = "values", Type = RpadArgumentType.StringList,
                Default = RpadValue.List(args.Select(RpadValue.Of)) }];
            document = RpadWriter.Write(parsed);
        }
        File.WriteAllText(path, document);
        var registry = new LanguageHostRegistry();
        registry.Register(new CSharpLanguageHostFactory(NullLoggerFactory.Instance));
        registry.Register(new VbLanguageHostFactory(NullLoggerFactory.Instance));
        var cache = new Mock<IBuildCacheService>();
        cache.Setup(b => b.EnsureOutputDirectory(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(() => Directory.CreateDirectory(Path.Combine(_root, "build-" + language)).FullName);
        var prefs = Prefs();
        var shell = new MainWindowViewModel([], new Mock<IFileSystemService>().Object, new Mock<IWindowService>().Object, prefs.Object, registry);
        var sdk = new DotnetSdkProvider().Get().First();
        var vm = new EditorTabItemViewModel(path, [sdk], prefs.Object, shell, language, RpadKind.Program);
        var ctrl = new EditorsTabItemControl(vm, Theme().Object, new Mock<IWindowService>().Object, registry, prefs.Object,
            intelliSense: null, cache.Object);
        var window = new Window { Content = ctrl };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (ctrl, window);
    }

    private static async Task RunAsync(EditorsTabItemControl ctrl, string selected)
    {
        var text = ctrl.State.Editor.Text;
        ctrl.State.Editor.Select(text.IndexOf(selected, StringComparison.Ordinal), selected.Length);
        var sdk = new DotnetSdkProvider().Get().First();
        await ctrl.RunSelectionAsync(sdk).WaitAsync(TimeSpan.FromMinutes(3));
        for (var i = 0; i < 20; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(25); }
    }

    // A real compile and process: the selected statement inside Main runs, with the script's args;
    // neither the rest of Main nor the helper method outside the selection runs.
    [AvaloniaFact]
    [Trait("Category", "RealBuild")]
    public async Task ASelectionInsideACSharpMain_Should_RunAlone_InARealProcess()
    {
        const string code = "void Main()\n{\n    Console.WriteLine(\"not selected\");\n    Console.WriteLine(\"selected:\" + string.Join(\",\", args));\n    Helper();\n}\n\nvoid Helper() => Console.WriteLine(\"helper\");\n";
        if (Real(RpadLanguage.CSharp, code, ["a1", "a2"]) is not var (ctrl, window)) return;
        try
        {
            var original = File.ReadAllBytes(Path.Combine(_root, "program.rpad"));
            await RunAsync(ctrl, "Console.WriteLine(\"selected:\" + string.Join(\",\", args));");

            var output = ctrl.State.Output.Content;
            Assert.True(output.Contains("selected:--values,a1,--values,a2", StringComparison.Ordinal), output);
            Assert.DoesNotContain("not selected", output);
            Assert.DoesNotContain("helper", output);
            Assert.Equal("Success", ctrl.State.ExecStatusText);
            Assert.Equal(original, File.ReadAllBytes(Path.Combine(_root, "program.rpad")));
        }
        finally { window.Close(); ctrl.Dispose(); }
    }

    // A selection that calls a method declared outside it fails to compile, with the compiler's own
    // error and a note on why; nothing runs and the whole program is not run instead.
    [AvaloniaFact]
    [Trait("Category", "RealBuild")]
    public async Task ASelectionUsingADeclarationOutsideIt_Should_ExplainTheCompileError()
    {
        const string code = "void Main()\n{\n    Helper();\n}\n\nvoid Helper() => Console.WriteLine(\"helper ran\");\n";
        if (Real(RpadLanguage.CSharp, code) is not var (ctrl, window)) return;
        try
        {
            await RunAsync(ctrl, "Helper();");

            var output = ctrl.State.Output.Content;
            Assert.Equal("Compilation failed", ctrl.State.ExecStatusText);
            Assert.Contains("CS0103", output);
            Assert.Contains("declared outside the selection are not available", output);
            Assert.DoesNotContain("helper ran", output);
        }
        finally { window.Close(); ctrl.Dispose(); }
    }

    // Visual Basic Program scripts too: the selected line inside Sub Main runs through VB's own
    // Statements wrapper.
    [AvaloniaFact]
    [Trait("Category", "RealBuild")]
    public async Task ASelectionInsideAVisualBasicSubMain_Should_RunAlone_InARealProcess()
    {
        const string code = "Module Program\n    Sub Main()\n        Console.WriteLine(\"vb not selected\")\n        Console.WriteLine(\"vb selected\")\n    End Sub\nEnd Module\n";
        if (Real(RpadLanguage.Vb, code) is not var (ctrl, window)) return;
        try
        {
            await RunAsync(ctrl, "Console.WriteLine(\"vb selected\")");

            var output = ctrl.State.Output.Content;
            Assert.True(output.Contains("vb selected", StringComparison.Ordinal), output);
            Assert.DoesNotContain("vb not selected", output);
            Assert.Equal("Success", ctrl.State.ExecStatusText);
        }
        finally { window.Close(); ctrl.Dispose(); }
    }
}
