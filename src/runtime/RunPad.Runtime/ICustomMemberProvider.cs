namespace RunPad.Runtime;

// Implement on a type to control how Dump() renders it, instead of the default reflection-over-
// public-properties behavior (ANALYSIS.md feature #20 / LINQPad-6).
public interface ICustomMemberProvider
{
    IEnumerable<string> GetNames();
    IEnumerable<object?> GetValues();
}
