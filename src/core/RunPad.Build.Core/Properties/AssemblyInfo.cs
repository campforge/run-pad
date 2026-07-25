using System.Runtime.CompilerServices;

// TODO.md #111 — ChildProcess.StartInfoForTests (internal, not public) needs a real ProcessStartInfo
// to inspect in tests without actually starting a process; DotPad.Build.Core has no dedicated test
// project of its own (see docs/ARCHITECTURE.md's list of five), so this project's own tests live directly in
// DotPad.Runtime.UI.Tests, same as several other internal-only-tested projects in this solution.
[assembly: InternalsVisibleTo("DotPad.Runtime.UI.Tests")]
[assembly: InternalsVisibleTo("DotPad.NonUI.Tests")]
