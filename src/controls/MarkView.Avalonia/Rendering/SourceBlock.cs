// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Avalonia.Controls;

namespace MarkView.Avalonia.Rendering;

/// <summary>
/// A rendered top-level block and the source lines it came from, so that a scroll position in the
/// viewer can be mapped to a line of the Markdown and back.
/// </summary>
/// <param name="Control">The rendered control. For a code block it is the text of the code
/// itself, inside its border, whose lines all have the same height.</param>
/// <param name="StartLine">The zero-based first source line shown by <paramref name="Control"/>.</param>
/// <param name="EndLine">The zero-based line where the next block starts, so blank lines before it
/// count as this block's; for a code block, the line after its last line of code.</param>
public sealed record SourceBlock(Control Control, int StartLine, int EndLine);
