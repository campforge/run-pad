using System.Text;

namespace DotPad.Format.RPad;

public static class RpadWriter
{
    /// <summary>Serializes the document back to .rpad file content.</summary>
    public static string Write(RpadDocument doc)
    {
        var yaml = SerializeFm(doc.FrontMatter);
        var code = doc.RunCode.EndsWith('\n') ? doc.RunCode : doc.RunCode + "\n";
        return $"---\n{yaml}\n---\n{doc.BodyBeforeCode}{code}{doc.BodyAfterCode}";
    }

    /// <summary>Generates the content for a brand-new .rpad file.</summary>
    public static string NewFile(string title = "Untitled", string targetFramework = "net10.0")
        => $"---\nversion: 1\nlanguage: csharp\ntargetFramework: {targetFramework}\nkind: Program\n---\n\n# {title}\n\n```csharp run\nConsole.WriteLine(\"Hello, RPad!\");\n```\n";

    private static string SerializeFm(RpadFrontMatter fm)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"version: {fm.Version}");
        sb.AppendLine($"language: {fm.Language}");
        sb.AppendLine($"targetFramework: {fm.TargetFramework}");
        sb.Append($"kind: {fm.Kind}");

        if (fm.Packages?.Count > 0)
        {
            sb.AppendLine();
            sb.Append("packages:");
            foreach (var (k, v) in fm.Packages)
            {
                sb.AppendLine();
                sb.Append($"  {k}: {v}");
            }
        }

        if (fm.References?.Count > 0)
        {
            sb.AppendLine();
            sb.Append("references:");
            foreach (var r in fm.References)
            {
                sb.AppendLine();
                sb.Append($"  - {r}");
            }
        }

        if (fm.Usings?.Count > 0)
        {
            sb.AppendLine();
            sb.Append("usings:");
            foreach (var u in fm.Usings)
            {
                sb.AppendLine();
                sb.Append($"  - {u}");
            }
        }

        return sb.ToString();
    }
}
