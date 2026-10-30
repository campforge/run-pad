using System.Diagnostics.CodeAnalysis;

namespace Invoklet.Format.MdRun;

/// <summary>
/// Outcome of <see cref="MdRunParser.Parse"/> — exactly one of <see cref="Document"/>/<see cref="Error"/>
/// is non-null. An invalid document (no front matter, malformed YAML, no run block for the declared
/// language) is reported as an <see cref="Error"/> rather than silently replaced with default
/// metadata, so callers can keep the original text intact and refuse to run it.
/// </summary>
public sealed class MdRunParseResult
{
    private MdRunParseResult(MdRunDocument? document, string? error)
    {
        Document = document;
        Error = error;
    }

    public MdRunDocument? Document { get; }

    /// <summary>Human-readable diagnostic describing why the content isn't a valid .inv document.</summary>
    public string? Error { get; }

    [MemberNotNullWhen(true, nameof(Document))]
    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => Document is not null;

    internal static MdRunParseResult Success(MdRunDocument document) => new(document, null);

    internal static MdRunParseResult Failure(string error) => new(null, error);
}
