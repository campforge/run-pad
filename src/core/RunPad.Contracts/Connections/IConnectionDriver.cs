using RunPad.Format.RPad;

namespace RunPad.Contracts.Connections;

/// <summary>What a connection reaches; groups connections in RunPad's lists.</summary>
public enum ConnectionCategory
{
    Database,
    Http,
    Grpc,
}

/// <summary>How a parameter is edited and what its text means.</summary>
public enum ConnectionParamType
{
    /// <summary>One line of text, placeholders allowed.</summary>
    Text,

    /// <summary>A number, as text.</summary>
    Number,

    /// <summary><c>true</c> or <c>false</c>, as text.</summary>
    Boolean,

    /// <summary>One of <see cref="ConnectionParam.Choices"/>.</summary>
    Choice,

    /// <summary>Names and texts, such as HTTP headers; texts may hold placeholders.</summary>
    Map,

    /// <summary>A list of texts.</summary>
    List,
}

/// <summary>
/// One parameter a driver understands under a connection's <c>params:</c>. RunPad builds the
/// connection form from these, so a driver needs no user interface of its own.
/// </summary>
/// <param name="Key">The key under <c>params:</c>.</param>
/// <param name="Label">The form's label.</param>
/// <param name="Example">Shown in an empty field.</param>
/// <param name="Description">A line of help under the field.</param>
/// <param name="Advanced">Shown after the others, for settings most connections leave alone.</param>
public sealed record ConnectionParam(
    string Key,
    string Label,
    ConnectionParamType Type = ConnectionParamType.Text,
    bool Required = false,
    string? Default = null,
    string? Example = null,
    string? Description = null,
    IReadOnlyList<string>? Choices = null,
    bool Advanced = false);

/// <summary>The outcome of <see cref="IConnectionDriver.TestAsync"/>, in words for the user.</summary>
public sealed record ConnectionTestResult(bool Succeeded, string Message);

/// <summary>
/// One kind of connection (a database engine, HTTP, gRPC): what its parameters are, how they are
/// checked and how RunPad tests a connection. Registered in <see cref="ConnectionDriverRegistry"/>;
/// drivers are part of RunPad for now and meant to become plugins, so this contract has no user
/// interface or RunPad internals in it.
/// </summary>
public interface IConnectionDriver
{
    /// <summary>The <c>driver:</c> value: <c>http</c>, <c>postgres</c>…</summary>
    string Id { get; }

    string DisplayName { get; }

    ConnectionCategory Category { get; }

    IReadOnlyList<ConnectionParam> Params { get; }

    /// <summary>
    /// Problems with the connection's parameters, one message each; empty when it is fine. Text
    /// with placeholders is checked only as far as the placeholders allow (their values are
    /// unknown until the script runs).
    /// </summary>
    IReadOnlyList<string> Validate(RpadConnection connection);

    /// <summary>
    /// Tries the connection with its placeholders already replaced (see
    /// <see cref="ConnectionPlaceholderResolver"/>), for the Test button. Never throws for an
    /// unreachable or refusing endpoint: that is a failed result with the reason.
    /// </summary>
    Task<ConnectionTestResult> TestAsync(IReadOnlyDictionary<string, RpadValue> resolvedParams, CancellationToken cancellationToken);
}
