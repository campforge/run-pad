using System.Runtime.CompilerServices;

// Local fork — see AvaloniaEdit/Properties/InternalsVisibleTo.cs's own comment.
[assembly: InternalsVisibleTo("AvaloniaEdit.Tests")]
// Invoklet.Runtime.UI.Tests exercises TextTransformation.GetFontStyle() directly (no
// italic comments) — the upstream NUnit suite above deliberately stays untouched by Invoklet-specific
// regressions.
[assembly: InternalsVisibleTo("Invoklet.Runtime.UI.Tests")]
[assembly: InternalsVisibleTo("Invoklet.NonUI.Tests")]
