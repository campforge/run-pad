namespace RunPad.Windows;

/// <summary>
/// A window the app opens at startup that needs more than its first layout before it looks ready.
/// The splash screen stays up, and the window hidden, until <see cref="Ready"/> completes.
/// </summary>
internal interface IStartupWindow
{
    Task Ready { get; }
}
