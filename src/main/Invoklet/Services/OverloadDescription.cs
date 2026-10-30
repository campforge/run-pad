using Invoklet.Contracts.IntelliSense;

namespace Invoklet.Services;

internal static class OverloadDescription
{
    /// <summary>
    /// The text under the signature of a parameter-info popup. The signature already shows the active parameter in bold, so
    /// only the descriptions remain: the method's own summary, then the active parameter's own documentation as
    /// "name: summary", separated by a blank line. Null when there is neither.
    /// </summary>
    public static string? Of(string? methodSummary, ISignatureHelpParameter? activeParameter)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(methodSummary)) parts.Add(methodSummary);
        if (!string.IsNullOrEmpty(activeParameter?.Summary)) parts.Add($"{activeParameter.Name}: {activeParameter.Summary}");
        return parts.Count == 0 ? null : string.Join("\n\n", parts);
    }
}
