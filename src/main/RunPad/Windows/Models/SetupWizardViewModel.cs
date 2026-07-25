using System.Reactive;
using System.Reactive.Linq;
using ReactiveUI;
using RunPad.Services.Preferences;
using RunPad.Services.Setup;

namespace RunPad.Windows.Models;

// First-run setup wizard (TODO.md, live report 2026-07-25) — shown instead of/before MainWindow when
// IPrerequisiteService finds a missing .NET SDK or debugger, so the user gets an actionable "install
// this for me" instead of a confusing crash (no SDK) or silently-broken debugging (empty DebuggerPath).
public sealed class SetupWizardViewModel : ReactiveObject
{
    private readonly ISetupInstallerService _installer;
    private readonly IPreferencesService _preferences;
    private readonly IPrerequisiteService _prerequisites;

    public bool HasSuitableSdk { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    public bool HasValidDebugger { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    public string StatusText { get; private set => this.RaiseAndSetIfChanged(ref field, value); } = "";
    public bool IsBusy { get; private set => this.RaiseAndSetIfChanged(ref field, value); }

    public ReactiveCommand<Unit, Unit> InstallSdkCommand { get; }
    public ReactiveCommand<Unit, Unit> InstallDebuggerCommand { get; }

    public SetupWizardViewModel(
        ISetupInstallerService installer, IPreferencesService preferences, IPrerequisiteService prerequisites)
    {
        _installer = installer;
        _preferences = preferences;
        _prerequisites = prerequisites;

        Refresh();

        var canRun = this.WhenAnyValue(x => x.IsBusy, isBusy => !isBusy);
        InstallSdkCommand = ReactiveCommand.CreateFromTask(InstallSdkAsync, canRun);
        InstallDebuggerCommand = ReactiveCommand.CreateFromTask(InstallDebuggerAsync, canRun);
    }

    private void Refresh()
    {
        var status = _prerequisites.Check();
        HasSuitableSdk = status.HasSuitableSdk;
        HasValidDebugger = status.HasValidDebugger;
    }

    private async Task InstallSdkAsync()
    {
        IsBusy = true;
        try
        {
            var progress = new Progress<string>(line => StatusText = line);
            await _installer.InstallDotnetSdkAsync(progress, CancellationToken.None);
            StatusText = ".NET SDK installed.";
        }
        catch (Exception ex)
        {
            StatusText = $"Couldn't install the .NET SDK: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    private async Task InstallDebuggerAsync()
    {
        IsBusy = true;
        try
        {
            var progress = new Progress<string>(line => StatusText = line);
            var debuggerPath = await _installer.InstallNetcoredbgAsync(progress, CancellationToken.None);
            _preferences.Debugger.DebuggerPath = debuggerPath;
            _preferences.Save();
            StatusText = "netcoredbg installed.";
        }
        catch (Exception ex)
        {
            StatusText = $"Couldn't install netcoredbg: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    // Live report 2026-07-26 — the wizard only ever offered "install for me"; a user who already has
    // .NET/netcoredbg somewhere not on PATH (or wants a specific one of several installed copies) had
    // no way to just point at it. Plain methods, not ReactiveCommands — the file-picker itself is a
    // Window/StorageProvider concern the code-behind's Browse button handlers own; this is just what
    // happens once a path comes back.
    public void SetDotnetPathManually(string path)
    {
        _preferences.Debugger.DotnetPath = path;
        _preferences.Save();
        StatusText = $"Using .NET at {path}.";
        Refresh();
    }

    public void SetDebuggerPathManually(string path)
    {
        _preferences.Debugger.DebuggerPath = path;
        _preferences.Save();
        StatusText = $"Using netcoredbg at {path}.";
        Refresh();
    }
}
