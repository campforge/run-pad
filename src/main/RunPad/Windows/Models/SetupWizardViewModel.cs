using System.Reactive;
using System.Reactive.Linq;
using ReactiveUI;
using RunPad.Services.Preferences;
using RunPad.Services.Setup;

namespace RunPad.Windows.Models;

// First-run setup wizard (live report 2026-07-25) — shown instead of/before MainWindow when
// IPrerequisiteService finds a missing .NET SDK or debugger, so the user gets an actionable "install
// this for me" instead of a confusing crash (no SDK) or silently-broken debugging (empty DebuggerPath).
public sealed class SetupWizardViewModel : ReactiveObject
{
    private readonly ISetupInstallerService _installer;
    private readonly IPreferencesService _preferences;
    private readonly IPrerequisiteService _prerequisites;

    // Live report 2026-07-27 — InstallSdkAsync/InstallDebuggerAsync used to pass CancellationToken.None
    // all the way down, so a stalled download or a wedged dotnet-install/netcoredbg-extract child
    // process left the wizard on IsBusy forever: no timeout to ever trip an error, and no Cancel button
    // to back out manually. _installCts bounds every install attempt with SetupConstants.InstallTimeout
    // AND lets CancelInstallCommand cut it short; _cancelledByUser only distinguishes the message shown
    // for the two cases (both surface as the same OperationCanceledException).
    private CancellationTokenSource? _installCts;
    private bool _cancelledByUser;

    public bool HasSuitableSdk { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    public bool HasValidDebugger { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    public string StatusText { get; private set => this.RaiseAndSetIfChanged(ref field, value); } = "";
    public bool IsStatusError { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    public bool IsBusy { get; private set => this.RaiseAndSetIfChanged(ref field, value); }

    // Live report 2026-07-27 — the wizard's progress bar never actually filled: it was permanently
    // IsIndeterminate="True" with nothing driving a real Value. Only the download steps can report a
    // real percentage (a Content-Length to measure bytes-read against); ProgressIsIndeterminate flips
    // to false only once a SetupProgress report actually carries one, and back whenever a step (running
    // dotnet-install, extracting) doesn't have one to give.
    public double ProgressPercent { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    public bool ProgressIsIndeterminate { get; private set => this.RaiseAndSetIfChanged(ref field, value); } = true;

    public ReactiveCommand<Unit, Unit> InstallSdkCommand { get; }
    public ReactiveCommand<Unit, Unit> InstallDebuggerCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelInstallCommand { get; }

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
        CancelInstallCommand = ReactiveCommand.Create(CancelInstall, this.WhenAnyValue(x => x.IsBusy));
    }

    private void Refresh()
    {
        var status = _prerequisites.Check();
        HasSuitableSdk = status.HasSuitableSdk;
        HasValidDebugger = status.HasValidDebugger;

        // Live report 2026-07-27 — the last install's status message ("netcoredbg installed.", a
        // Browse-picked path, etc.) used to sit there forever: once both rows are checked off there's
        // nothing left to do and the Install/Browse controls are already gone, but the leftover text
        // stayed, looking like something still needed attention. An error message means something DOES
        // still need attention, so that's left alone even once both prerequisites happen to read true.
        if (HasSuitableSdk && HasValidDebugger && !IsStatusError)
            StatusText = "";
    }

    private void CancelInstall()
    {
        _cancelledByUser = true;
        _installCts?.Cancel();
    }

    private Progress<SetupProgress> CreateProgressReporter() => new(p =>
    {
        StatusText = p.Message;
        if (p.PercentComplete is { } percent)
        {
            ProgressIsIndeterminate = false;
            ProgressPercent = percent;
        }
        else
        {
            ProgressIsIndeterminate = true;
        }
    });

    private async Task InstallSdkAsync()
    {
        IsBusy = true;
        IsStatusError = false;
        ProgressPercent = 0;
        ProgressIsIndeterminate = true;
        using var cts = new CancellationTokenSource(SetupConstants.InstallTimeout);
        _installCts = cts;
        try
        {
            var progress = CreateProgressReporter();
            await _installer.InstallDotnetSdkAsync(progress, cts.Token);
            StatusText = ".NET SDK installed.";
        }
        catch (OperationCanceledException)
        {
            StatusText = _cancelledByUser
                ? "Installing the .NET SDK was cancelled."
                : $"Installing the .NET SDK timed out after {SetupConstants.InstallTimeout.TotalMinutes:0} minutes.";
            IsStatusError = true;
        }
        catch (Exception ex)
        {
            StatusText = $"Couldn't install the .NET SDK: {ex.Message}";
            IsStatusError = true;
        }
        finally
        {
            _installCts = null;
            _cancelledByUser = false;
            IsBusy = false;
            Refresh();
        }
    }

    private async Task InstallDebuggerAsync()
    {
        IsBusy = true;
        IsStatusError = false;
        ProgressPercent = 0;
        ProgressIsIndeterminate = true;
        using var cts = new CancellationTokenSource(SetupConstants.InstallTimeout);
        _installCts = cts;
        try
        {
            var progress = CreateProgressReporter();
            var debuggerPath = await _installer.InstallNetcoredbgAsync(progress, cts.Token);
            _preferences.Debugger.DebuggerPath = debuggerPath;
            _preferences.Save();
            StatusText = "netcoredbg installed.";
        }
        catch (OperationCanceledException)
        {
            StatusText = _cancelledByUser
                ? "Installing netcoredbg was cancelled."
                : $"Installing netcoredbg timed out after {SetupConstants.InstallTimeout.TotalMinutes:0} minutes.";
            IsStatusError = true;
        }
        catch (Exception ex)
        {
            StatusText = $"Couldn't install netcoredbg: {ex.Message}";
            IsStatusError = true;
        }
        finally
        {
            _installCts = null;
            _cancelledByUser = false;
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
