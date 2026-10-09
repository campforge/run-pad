using ICSharpCode.Decompiler.CSharp;

namespace RunPad.Windows.Models;

/// <summary>What <c>AssemblyExplorerWindow</c>'s code pane shows: ILSpy's language box.</summary>
public enum DecompilerLanguage
{
    CSharp,
    IL,
}

/// <summary>The language and (for C#) the version the code pane renders with.</summary>
public sealed record DecompilerView(DecompilerLanguage Language, LanguageVersion Version)
{
    public static DecompilerView Default { get; } = new(DecompilerLanguage.CSharp, LanguageVersion.Latest);
}

/// <summary>
/// What an explorer page shows, so it can be rendered again in another <see cref="DecompilerView"/>:
/// the type or member <see cref="Token"/> of <see cref="AssemblyPath"/>, and for a type page reached by
/// F12 the member to put the caret on (<see cref="FocusToken"/>).
/// </summary>
public sealed record PageTarget(string AssemblyPath, int Token, string Title, int FocusToken = 0);
