namespace DotPad.Services.Setup;

// TODO.md — live report 2026-07-25: DotnetSdkProvider.Get() returning empty just makes callers throw
// an unhelpful exception, and DebuggerSettings.DebuggerPath defaults to "" (debugging silently doesn't
// work) — neither is ever surfaced to the user as "you're missing a dependency". This is the check the
// first-run setup wizard (SetupWizardWindow) uses to decide whether to show itself at all.
public interface IPrerequisiteService
{
    PrerequisiteStatus Check();
}

public sealed record PrerequisiteStatus(bool HasSuitableSdk, bool HasValidDebugger)
{
    public bool IsComplete => HasSuitableSdk && HasValidDebugger;
}
