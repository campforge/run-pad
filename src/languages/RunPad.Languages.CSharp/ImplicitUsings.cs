namespace DotPad.Languages.CSharp;

// The .NET SDK's own <ImplicitUsings>enable</ImplicitUsings> set (verified against a real `dotnet
// build` output — obj/<config>/<tfm>/<assembly>.GlobalUsings.g.cs — for a plain Exe project; no
// System.Text). CodeAnalysisProcessCompiler and CSharpIntelliSenseService both need this exact list
// so a script with no packages compiles the same way it's shown in the editor — see ANALYSIS.md #27
// for what happens when they disagree.
internal static class ImplicitUsings
{
    public static readonly string[] Default =
    [
        "System", "System.Collections.Generic", "System.IO", "System.Linq",
        "System.Net.Http", "System.Threading", "System.Threading.Tasks",
    ];
}
