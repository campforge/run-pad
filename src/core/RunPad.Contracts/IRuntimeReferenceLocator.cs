namespace RunPad.Contracts;

/// <summary>
/// Finds the RunPad.Runtime library (<c>.Dump()</c>, <c>Util</c>) that every compiled script
/// references, whatever its language: the run and the editor's IntelliSense both need its path.
/// </summary>
public interface IRuntimeReferenceLocator
{
    /// <summary>The full path of the RunPad.Runtime assembly.</summary>
    string GetRuntimeAssemblyPath();
}
