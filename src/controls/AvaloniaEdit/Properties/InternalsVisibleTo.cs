using System.Runtime.CompilerServices;

// Local fork (TODO.md #61) — upstream signed this with its own .snk (public key above), which we
// don't have; AvaloniaEdit.Tests isn't strong-named in this solution, so grant it access by name
// only, same as every other InternalsVisibleTo in this repo (e.g. RunPad's own *.csproj entries).
[assembly: InternalsVisibleTo("AvaloniaEdit.Tests")]
