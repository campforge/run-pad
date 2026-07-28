namespace RunPad.Services.Setup;

// A single install step's progress: Message is always shown; PercentComplete drives the wizard's
// progress bar when it's actually knowable (byte-counted downloads, where the response gives us a
// Content-Length) and stays null everywhere it isn't (running the external dotnet-install script,
// local archive extraction) — the wizard falls back to an indeterminate bar for those.
public readonly record struct SetupProgress(string Message, double? PercentComplete = null);
