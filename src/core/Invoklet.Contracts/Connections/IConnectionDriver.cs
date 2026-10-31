using Invoklet.Format.MdRun;

namespace Invoklet.Contracts.Connections;

/// <summary>What a connection reaches; groups connections in Invoklet's lists.</summary>
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

    /// <summary>Names and texts, such as HTTP headers; texts may hold placeholders.</summary>
    Map,

    /// <summary>A list of texts.</summary>
    List,

    /// <summary>
    /// A server's host and port, as people write them; the form asks for the protocol with a checkbox and the file keeps all of it as one text,
    /// <c>https://host:port</c> or <c>http://host:port</c>. A text with no protocol is read as secure.
    /// </summary>
    Endpoint,

    /// <summary>The path of a file, as text with placeholders allowed; the form can browse for it.</summary>
    File,
}

/// <summary>
/// One parameter a driver understands under a connection's <c>params:</c>. Invoklet builds the
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
    bool Advanced = false);

/// <summary>The outcome of <see cref="IConnectionDriver.TestAsync"/>, in words for the user.</summary>
public sealed record ConnectionTestResult(bool Succeeded, string Message);

/// <summary>
/// One kind of connection (a database engine, HTTP, gRPC): what its parameters are, how they are
/// checked and how Invoklet tests a connection. Registered in <see cref="ConnectionDriverRegistry"/>;
/// drivers are part of Invoklet for now and meant to become plugins, so this contract has no user
/// interface or Invoklet internals in it.
/// </summary>
public interface IConnectionDriver
{
    /// <summary>The <c>driver:</c> value: <c>http</c>, <c>postgres</c>…</summary>
    string Id { get; }

    string DisplayName { get; }

    ConnectionCategory Category { get; }

    IReadOnlyList<ConnectionParam> Params { get; }

    /// <summary>
    /// The NuGet packages (id → version) the script's client needs. They are ordinary packages of the
    /// script: adding a connection in the Dependencies window adds them to its <c>packages:</c>, where
    /// they are restored, shown and updated like any other, and a build without them fails.
    /// </summary>
    IReadOnlyDictionary<string, string> Packages { get; }

    /// <summary>
    /// The packages with the versions that fit a script's target framework (<c>net8.0</c>, <c>latest</c>); a driver whose packages work on every
    /// framework gives <see cref="Packages"/>.
    /// </summary>
    IReadOnlyDictionary<string, string> PackagesFor(string? targetFramework) => Packages;

    /// <summary>
    /// Problems with the connection's parameters, one message each; empty when it is fine. Text
    /// with placeholders is checked only as far as the placeholders allow (their values are
    /// unknown until the script runs).
    /// </summary>
    IReadOnlyList<string> Validate(MdRunConnection connection);

    /// <summary>
    /// Tries the connection with its placeholders already replaced (see
    /// <see cref="ConnectionPlaceholderResolver"/>), for the Test button. Never throws for an
    /// unreachable or refusing endpoint: that is a failed result with the reason.
    /// </summary>
    Task<ConnectionTestResult> TestAsync(IReadOnlyDictionary<string, MdRunValue> resolvedParams, CancellationToken cancellationToken);

    /// <summary>
    /// The script's client class for a valid connection: C# for a public sealed class named
    /// <see cref="ConnectionCodeContext.ClassName"/> with a parameterless internal constructor that
    /// sets the client up. Texts become expressions through <see cref="ConnectionCodeContext.Text"/>,
    /// so secrets and environment variables are read when the script runs, never put in the code.
    /// </summary>
    ConnectionClientCode CreateClient(MdRunConnection connection, ConnectionCodeContext context);
}

/// <summary>A connection's client class; the packages it uses are the driver's <see cref="IConnectionDriver.Packages"/>.</summary>
public sealed record ConnectionClientCode(string ClassSource);
