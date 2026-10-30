namespace Invoklet.Runtime;

// Implement on a type to control how Dump() renders it, instead of the default reflection-over-
// public-properties behavior. LINQPad-compatible
// ICustomMemberProvider (signature verified against the real LINQPad.Runtime.dll — GetTypes() is a
// real member Invoklet originally shipped without; see DumpRenderer.RenderCustomMemberProvider for how
// it's used, to render a null value's declared type instead of a bare "null").
public interface ICustomMemberProvider
{
    IEnumerable<string> GetNames();
    IEnumerable<Type> GetTypes();
    IEnumerable<object?> GetValues();
}
