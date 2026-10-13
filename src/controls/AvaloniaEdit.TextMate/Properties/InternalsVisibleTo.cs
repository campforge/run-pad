using System.Runtime.CompilerServices;

// Local fork — see AvaloniaEdit/Properties/InternalsVisibleTo.cs's own comment.
[assembly: InternalsVisibleTo("AvaloniaEdit.Tests")]
// RunPad.Runtime.UI.Tests exercises TextTransformation.GetFontStyle() directly (no
// italic comments) — the upstream NUnit suite above deliberately stays untouched by RunPad-specific
// regressions, per its own "kept as-is" convention (see docs/ARCHITECTURE.md).
[assembly: InternalsVisibleTo("RunPad.Runtime.UI.Tests")]
[assembly: InternalsVisibleTo("RunPad.NonUI.Tests")]
