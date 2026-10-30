using Microsoft.CodeAnalysis;

namespace Invoklet.Languages.CSharp.IntelliSense;

// Locates the XML documentation for a referenced assembly, so Roslyn's GetDocumentationCommentXml()
// actually returns something for BCL/NuGet symbols — without a
// DocumentationProvider it comes back empty for every metadata symbol, no matter how well-documented
// the assembly is, so completion/signature-help/quick-info Summary was silently null for anything
// except symbols the user's own script defines (Roslyn reads those straight off the syntax tree).
// This only needs a same-directory check: CSharpReferenceService.GetDefault() prefers the SDK's
// reference-assembly pack precisely because those ship an .xml next to every .dll, and NuGet
// packages/Invoklet.Runtime itself (see its GenerateDocumentationFile) do the same.
internal static class ReferenceDocumentation
{
    public static DocumentationProvider? Find(string assemblyPath)
    {
        var xmlPath = Path.ChangeExtension(assemblyPath, ".xml");
        return File.Exists(xmlPath) ? XmlDocumentationProvider.CreateFromFile(xmlPath) : null;
    }
}
