using System.Runtime.CompilerServices;

// ChildProcess.StartInfoForTests (internal, not public) needs a real ProcessStartInfo
// to inspect in tests without actually starting a process; RunPad.Build.Core has no dedicated test
// project of its own (see docs/ARCHITECTURE.md's list of five), so this project's own tests live directly in
// RunPad.Runtime.UI.Tests, same as several other internal-only-tested projects in this solution.
[assembly: InternalsVisibleTo("RunPad.Runtime.UI.Tests")]
[assembly: InternalsVisibleTo("RunPad.NonUI.Tests")]
