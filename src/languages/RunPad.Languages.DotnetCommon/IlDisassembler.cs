using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.Disassembler;
using ICSharpCode.Decompiler.Metadata;

namespace RunPad.Languages.DotnetCommon;

/// <summary>
/// Disassembles a compiled assembly to raw IL text — backs the "IL" output tab (TODO.md #62,
/// LINQPad has the same feature). Driven off <see cref="Contracts.ILanguageHost.Compiled"/>, so this
/// only ever runs against a binary that has already finished building.
/// </summary>
public static class IlDisassembler
{
    public static string Disassemble(string assemblyPath)
    {
        // PEFile.Dispose() releases the FileStream it opens on assemblyPath internally (PEReader ->
        // FileStream) — without this, the handle outlives the call on any OS that enforces exclusive
        // file locks (Windows; POSIX allows unlinking/rebuilding an open file, so this never surfaced
        // there — live Windows CI report, 2026-07-19). That alone only covers a SUCCESSFUL
        // construction, though — PEFile's convenience constructor opens the FileStream inline as part
        // of a chained constructor call, so a file that fails to parse as a valid PE image (this
        // method only ever runs against a binary the compiler just produced, so unlikely here in
        // practice — see CSharpDecompilerService.WithDecompiler's own copy of this comment for where
        // it actually got caught) would leak that stream regardless, since the exception happens
        // before `using var peFile = ...` ever completes its assignment. Opening the stream ourselves
        // first gives it its own `using`, independent of whether PEFile's own constructor throws.
        using var stream = new FileStream(assemblyPath, FileMode.Open, FileAccess.Read);
        using var peFile = new PEFile(assemblyPath, stream);
        var output = new PlainTextOutput();
        var disassembler = new ReflectionDisassembler(output, CancellationToken.None);
        disassembler.WriteModuleContents(peFile);
        return output.ToString();
    }

    /// <summary>
    /// The assembly explorer's IL view (ILSpy's language box) of one type or member, by metadata token,
    /// under the same header comments as the C# view: the assembly and, for a member, its declaring type.
    /// The caret line is the first line after the header. No syntax tree: F12 is C#-only.
    /// </summary>
    public static DecompiledMemberResult DisassembleEntity(string assemblyPath, int metadataToken, string title)
    {
        using var stream = new FileStream(assemblyPath, FileMode.Open, FileAccess.Read);
        using var peFile = new PEFile(assemblyPath, stream);
        var reader = peFile.Metadata;
        var output = new PlainTextOutput();
        var disassembler = new ReflectionDisassembler(output, CancellationToken.None) { DetectControlStructure = true };

        var handle = MetadataTokens.EntityHandle(metadataToken);
        output.WriteLine("// " + peFile.FullName);
        // properties and events have no declaring type of their own in metadata — their accessors do
        var declaringType = handle.Kind switch
        {
            HandleKind.MethodDefinition => DeclaringTypeOf((MethodDefinitionHandle)handle),
            HandleKind.FieldDefinition => reader.GetFieldDefinition((FieldDefinitionHandle)handle).GetDeclaringType(),
            HandleKind.PropertyDefinition => reader.GetPropertyDefinition((PropertyDefinitionHandle)handle).GetAccessors() is var p
                ? DeclaringTypeOf(p.Getter.IsNil ? p.Setter : p.Getter)
                : default,
            HandleKind.EventDefinition => DeclaringTypeOf(reader.GetEventDefinition((EventDefinitionHandle)handle).GetAccessors().Adder),
            _ => default,
        };
        if (!declaringType.IsNil)
            output.WriteLine("// " + declaringType.GetFullTypeName(reader).ReflectionName);
        output.WriteLine();
        var line = declaringType.IsNil ? 3 : 4;

        switch (handle.Kind)
        {
            case HandleKind.AssemblyDefinition: // the assembly row: its manifest, like ildasm's
                disassembler.WriteAssemblyReferences(reader);
                disassembler.WriteAssemblyHeader(peFile);
                output.WriteLine();
                disassembler.WriteModuleHeader(peFile, skipMVID: false);
                break;
            case HandleKind.TypeDefinition:
                disassembler.DisassembleType(peFile, (TypeDefinitionHandle)handle);
                break;
            case HandleKind.MethodDefinition:
                disassembler.DisassembleMethod(peFile, (MethodDefinitionHandle)handle);
                break;
            case HandleKind.FieldDefinition:
                disassembler.DisassembleField(peFile, (FieldDefinitionHandle)handle);
                break;
            case HandleKind.PropertyDefinition:
                disassembler.DisassembleProperty(peFile, (PropertyDefinitionHandle)handle);
                break;
            case HandleKind.EventDefinition:
                disassembler.DisassembleEvent(peFile, (EventDefinitionHandle)handle);
                break;
            default:
                output.WriteLine($"// Nothing to disassemble for token 0x{metadataToken:X8}.");
                break;
        }

        return new DecompiledMemberResult(output.ToString(), line, title);

        TypeDefinitionHandle DeclaringTypeOf(MethodDefinitionHandle method) =>
            reader.GetMethodDefinition(method).GetDeclaringType();
    }
}
