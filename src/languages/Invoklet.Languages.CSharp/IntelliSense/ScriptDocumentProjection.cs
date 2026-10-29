using Microsoft.CodeAnalysis.Text;

namespace Invoklet.Languages.CSharp.IntelliSense;

/// <summary>
/// Pure projection of user code into the synthetic Roslyn document.
/// Workspace ownership, locking and version checks deliberately remain in the service.
/// </summary>
internal sealed record ScriptDocumentProjection(string FullText)
{
    public static ScriptDocumentProjection Create(
        string preamble,
        string kindPrefix,
        string projectedCode,
        string kindSuffix,
        string trailingPad) => new(preamble + kindPrefix + projectedCode + kindSuffix + trailingPad);

    public static TextChange ComputeMinimalChange(string oldText, string newText)
    {
        var maxCommon = Math.Min(oldText.Length, newText.Length);
        var prefixLength = 0;
        while (prefixLength < maxCommon && oldText[prefixLength] == newText[prefixLength])
            prefixLength++;

        var maxSuffix = maxCommon - prefixLength;
        var suffixLength = 0;
        while (suffixLength < maxSuffix &&
               oldText[oldText.Length - 1 - suffixLength] == newText[newText.Length - 1 - suffixLength])
            suffixLength++;

        return new TextChange(
            new TextSpan(prefixLength, oldText.Length - prefixLength - suffixLength),
            newText.AsSpan(prefixLength, newText.Length - prefixLength - suffixLength).ToString());
    }
}
