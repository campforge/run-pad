using System.Runtime.CompilerServices;

// Local fork (TODO.md #61) — see AvaloniaEdit/Properties/InternalsVisibleTo.cs's own comment.
[assembly: InternalsVisibleTo("AvaloniaEdit.Tests")]
// TODO.md #112: DotPad.Runtime.UI.Tests exercises TextTransformation.GetFontStyle() directly (no
// italic comments) — the upstream NUnit suite above deliberately stays untouched by DotPad-specific
// regressions, per its own "kept as-is" convention (see docs/ARCHITECTURE.md).
[assembly: InternalsVisibleTo("DotPad.Runtime.UI.Tests")]
