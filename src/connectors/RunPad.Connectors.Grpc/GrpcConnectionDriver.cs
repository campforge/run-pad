using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Reflection.V1Alpha;
using RunPad.Contracts.Connections;
using RunPad.Format.RPad;

namespace RunPad.Connectors.Grpc;

/// <summary>
/// A gRPC server that offers server reflection. RunPad asks it for its services, has protoc (from
/// the Grpc.Tools package) write their message and client classes, and gives the script a client
/// with a property per service: <c>Connections.Inventory.Stock.GetItemAsync(…)</c>. Metadata
/// (an <c>authorization</c> entry with <c>${secret:…}</c>, for instance) is sent with every call.
/// </summary>
public sealed class GrpcConnectionDriver : IConnectionSchemaDriver
{
    public const string DriverId = "grpc";

    // The versions the script's client is built with; protoc of the same gRPC release writes it.
    public const string GrpcVersion = "2.84.0";
    public const string ProtobufVersion = "3.36.2";

    private const string Address = "address";
    private const string Metadata = "metadata";
    private const string SkipCertificateCheck = "skipCertificateCheck";

    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(30);

    private readonly Func<HttpMessageHandler>? _handler;

    /// <param name="handler">What RunPad's own reflection calls are sent through; a <see cref="SocketsHttpHandler"/> unless replaced.</param>
    public GrpcConnectionDriver(Func<HttpMessageHandler>? handler = null) => _handler = handler;

    public string Id => DriverId;

    public string DisplayName => "gRPC";

    public ConnectionCategory Category => ConnectionCategory.Grpc;

    public IReadOnlyDictionary<string, string> Packages { get; } = new Dictionary<string, string>
    {
        ["Grpc.Net.Client"] = GrpcVersion,
        ["Google.Protobuf"] = ProtobufVersion,
    };

    public IReadOnlyList<ConnectionParam> Params { get; } =
    [
        new(Address, "Address", Required: true, Example: "https://localhost:5001",
            Description: "The server's http:// or https:// address. It must offer gRPC server reflection."),
        new(Metadata, "Metadata", ConnectionParamType.Map,
            Description: "Sent with every call, for example authorization: Bearer ${secret:api-token}."),
        new(SkipCertificateCheck, "Skip the certificate check", ConnectionParamType.Boolean, Default: "false", Advanced: true,
            Description: "Accepts any server certificate, such as a development one. Never for a real server."),
    ];

    public IReadOnlyList<string> Validate(RpadConnection connection)
    {
        var errors = new List<string>();
        var parameters = connection.Params;
        if (!parameters.TryGetValue(Address, out var address) || string.IsNullOrWhiteSpace(address.Text))
            errors.Add("The address is missing.");
        else if (!HasPlaceholder(address.Text) && !IsHttpAddress(address.Text))
            errors.Add($"The address '{address.Text}' is not an http:// or https:// address.");

        if (parameters.TryGetValue(Metadata, out var metadata) && metadata.Text is not { Length: 0 })
        {
            if (metadata.Fields is not { } fields || fields.Values.Any(v => v.Text is null))
                errors.Add("Metadata must be names with texts.");
            else
                errors.AddRange(fields.Keys.Where(k => !IsMetadataKey(k)).Select(k =>
                    $"'{k}' can't be a metadata name: use letters, digits, '-', '_' and '.', and no '-bin' ending."));
        }

        if (parameters.TryGetValue(SkipCertificateCheck, out var skip) && skip.Text is { Length: > 0 } flag
            && !bool.TryParse(flag, out _))
            errors.Add($"Skip the certificate check must be true or false, not '{flag}'.");
        return errors;
    }

    public async Task<ConnectionTestResult> TestAsync(IReadOnlyDictionary<string, RpadValue> resolvedParams, CancellationToken cancellationToken)
    {
        try
        {
            var services = await WithServerAsync(resolvedParams, (reflection, ct) => reflection.ListServicesAsync(ct), cancellationToken)
                .ConfigureAwait(false);
            return new ConnectionTestResult(true, Summary(services));
        }
        catch (GrpcConnectionException ex)
        {
            return new ConnectionTestResult(false, ex.Message);
        }
    }

    public async Task<ConnectionSchema> FetchSchemaAsync(IReadOnlyDictionary<string, RpadValue> resolvedParams, ConnectionTools tools,
        CancellationToken cancellationToken)
    {
        var (services, files) = await WithServerAsync(resolvedParams, async (reflection, ct) =>
        {
            var names = await reflection.ListServicesAsync(ct).ConfigureAwait(false);
            return (names, await reflection.FilesForAsync(names, ct).ConfigureAwait(false));
        }, cancellationToken).ConfigureAwait(false);
        if (services.Count == 0) throw new GrpcConnectionException("The server offers no services besides reflection.");

        var toolsFolder = await tools.GetPackageFolderAsync("Grpc.Tools", GrpcVersion, cancellationToken).ConfigureAwait(false);
        var generated = await ProtocAsync(toolsFolder, files, cancellationToken).ConfigureAwait(false);
        var clients = GrpcClientParser.Find(generated, services);
        var missing = services.Where(s => clients.All(c => c.Service != s)).ToList();
        if (missing.Count > 0)
            throw new GrpcConnectionException($"protoc wrote no client for {string.Join(", ", missing)}.");
        return new ConnectionSchema(Summary(services), generated, JsonSerializer.Serialize(GrpcClientParser.WithProperties(clients)));
    }

    // A client with the channel, an invoker that adds the metadata to every call, and a client per
    // service, created on first use.
    public ConnectionClientCode CreateClient(RpadConnection connection, ConnectionCodeContext context)
    {
        var parameters = connection.Params;
        var address = parameters[Address].Text!;
        var clients = context.Schema is { } schema ? JsonSerializer.Deserialize<List<GrpcServiceClient>>(schema.Data) ?? [] : [];
        clients = GrpcClientParser.WithProperties(clients, context.ClassName);
        var metadata = parameters.TryGetValue(Metadata, out var m) ? m.Fields ?? [] : [];
        var skip = parameters.TryGetValue(SkipCertificateCheck, out var s) && bool.TryParse(s.Text, out var flag) && flag;

        var sb = new StringBuilder()
            .Append("/// <summary>The gRPC connection '").Append(ConnectionCodeContext.Doc(connection.Name)).Append("' to ")
            .Append(ConnectionCodeContext.Doc(address)).Append(": a client for each of its services.</summary>\n")
            .Append("public sealed class ").Append(context.ClassName).Append('\n').Append("{\n")
            .Append("    internal ").Append(context.ClassName).Append("()\n    {\n")
            .Append("        var handler = new global::System.Net.Http.SocketsHttpHandler { EnableMultipleHttp2Connections = true };\n");
        if (skip)
            sb.Append("        handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;\n");
        sb.Append("        Channel = global::Grpc.Net.Client.GrpcChannel.ForAddress(").Append(context.Text(address))
            .Append(", new global::Grpc.Net.Client.GrpcChannelOptions { HttpHandler = handler });\n");
        if (metadata.Count == 0)
            sb.Append("        CallInvoker = Channel.CreateCallInvoker();\n");
        else
        {
            sb.Append("        var metadata = new global::Grpc.Core.Metadata();\n");
            foreach (var (name, value) in metadata)
                sb.Append("        metadata.Add(").Append(ConnectionCodeContext.Literal(name)).Append(", ").Append(context.Text(value.Text ?? "")).Append(");\n");
            // A new Metadata per call: adding to the caller's own would grow it on every call it is reused for.
            sb.Append("        CallInvoker = global::Grpc.Core.Interceptors.ChannelExtensions.Intercept(Channel, headers =>\n")
                .Append("        {\n")
                .Append("            var all = new global::Grpc.Core.Metadata();\n")
                .Append("            if (headers is not null) foreach (var entry in headers) all.Add(entry);\n")
                .Append("            foreach (var entry in metadata) all.Add(entry);\n")
                .Append("            return all;\n")
                .Append("        });\n");
        }
        sb.Append("    }\n\n")
            .Append("    /// <summary>The channel to the server; dispose of it to close the connection.</summary>\n")
            .Append("    public global::Grpc.Net.Client.GrpcChannel Channel { get; }\n\n")
            .Append("    /// <summary>Sends calls with the connection's metadata, for clients RunPad didn't create.</summary>\n")
            .Append("    public global::Grpc.Core.CallInvoker CallInvoker { get; }\n");
        foreach (var client in clients)
        {
            var field = "__" + char.ToLowerInvariant(client.Property[0]) + client.Property[1..];
            sb.Append('\n')
                .Append("    private ").Append(client.ClientType).Append("? ").Append(field).Append(";\n")
                .Append("    /// <summary>The service ").Append(ConnectionCodeContext.Doc(client.Service)).Append(".</summary>\n")
                .Append("    public ").Append(client.ClientType).Append(' ').Append(client.Property).Append(" => ").Append(field)
                .Append(" ??= new ").Append(client.ClientType).Append("(CallInvoker);\n");
        }
        sb.Append('}');
        return new ConnectionClientCode(sb.ToString());
    }

    private static string Summary(IReadOnlyList<string> services) => services.Count switch
    {
        0 => "The server answered, but offers no services besides reflection.",
        1 => $"1 service: {services[0]}.",
        _ => $"{services.Count} services: {string.Join(", ", services)}.",
    };

    // Opens a channel as the parameters say and runs the reflection calls, turning what can go wrong
    // into a message for the user.
    private async Task<T> WithServerAsync<T>(IReadOnlyDictionary<string, RpadValue> resolvedParams,
        Func<GrpcReflection, CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        if (!resolvedParams.TryGetValue(Address, out var addressValue) || addressValue.Text is not { } address || !IsHttpAddress(address))
            throw new GrpcConnectionException("The address is not an http:// or https:// address.");
        var skip = resolvedParams.TryGetValue(SkipCertificateCheck, out var s) && bool.TryParse(s.Text, out var flag) && flag;
        // Before the channel: the parameters reach here unchecked, and Metadata refuses an invalid name.
        var headers = new Metadata();
        foreach (var (name, value) in resolvedParams.TryGetValue(Metadata, out var m) ? m.Fields ?? [] : [])
        {
            try
            {
                if (value.Text is { } text) headers.Add(name, text);
            }
            catch (ArgumentException)
            {
                throw new GrpcConnectionException($"'{name}' can't be a metadata name: use letters, digits, '-', '_' and '.', and no '-bin' ending.");
            }
        }

        HttpMessageHandler handler;
        if (_handler is not null) handler = _handler();
        else
        {
            var sockets = new SocketsHttpHandler();
            if (skip) sockets.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            handler = sockets;
        }
        using var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions { HttpHandler = handler, DisposeHttpClient = true });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(MaxWait);
        try
        {
            return await action(new GrpcReflection(channel.CreateCallInvoker(), headers), timeout.Token).ConfigureAwait(false);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unimplemented)
        {
            throw new GrpcConnectionException("The server doesn't offer gRPC server reflection, which RunPad reads its services with.");
        }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.Unauthenticated or StatusCode.PermissionDenied)
        {
            throw new GrpcConnectionException($"The server refused the call ({ex.StatusCode}): check the credentials in the metadata.");
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.DeadlineExceeded
                                      || (ex.StatusCode == StatusCode.Cancelled && !cancellationToken.IsCancellationRequested))
        {
            throw new GrpcConnectionException($"The server did not answer within {MaxWait.TotalSeconds:0} seconds.");
        }
        catch (RpcException ex)
        {
            throw new GrpcConnectionException($"Could not reach the server: {ex.Status.Detail} ({ex.StatusCode}).");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GrpcConnectionException($"The server did not answer within {MaxWait.TotalSeconds:0} seconds.");
        }
        catch (HttpRequestException ex)
        {
            throw new GrpcConnectionException($"Could not reach the server: {ex.Message}");
        }
    }

    // protoc writes the messages and clients of every file the server described, except the
    // well-known types, which Google.Protobuf has already.
    internal static async Task<IReadOnlyList<GeneratedSourceFile>> ProtocAsync(string toolsPackage, IReadOnlyList<FileDescriptorProto> files,
        CancellationToken cancellationToken)
    {
        var (protoc, plugin) = ToolPaths(toolsPackage);
        var work = Directory.CreateTempSubdirectory("runpad-grpc-");
        try
        {
            var set = new FileDescriptorSet();
            set.File.AddRange(files);
            var setPath = Path.Combine(work.FullName, "schema.pb");
            await File.WriteAllBytesAsync(setPath, set.ToByteArray(), cancellationToken).ConfigureAwait(false);
            var output = Directory.CreateDirectory(Path.Combine(work.FullName, "out")).FullName;

            var start = new ProcessStartInfo(protoc)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("--descriptor_set_in=" + setPath);
            // A server that leaves out a well-known type its files import: protoc has them as files.
            if (files.SelectMany(f => f.Dependency).Any(d => files.All(f => f.Name != d)))
                start.ArgumentList.Add("--proto_path=" + Path.Combine(toolsPackage, "build", "native", "include"));
            start.ArgumentList.Add("--plugin=protoc-gen-grpc=" + plugin);
            start.ArgumentList.Add("--csharp_out=" + output);
            start.ArgumentList.Add("--grpc_out=" + output);
            // A folder per namespace: files of the same name in different packages (v1/service.proto
            // and v2/service.proto) would otherwise both be written as Service.cs.
            start.ArgumentList.Add("--csharp_opt=base_namespace=");
            start.ArgumentList.Add("--grpc_opt=no_server,base_namespace=");
            foreach (var file in files.Where(f => !IsWellKnown(f.Name)))
                start.ArgumentList.Add(GrpcReflection.IsSafeProtoPath(file.Name) ? file.Name
                    : throw new GrpcConnectionException($"The server described a file with a name RunPad doesn't accept: '{file.Name}'."));

            using var process = Process.Start(start) ?? throw new GrpcConnectionException("protoc couldn't be started.");
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                throw;
            }
            await stdout.ConfigureAwait(false);
            var errors = (await stderr.ConfigureAwait(false)).Trim();
            if (process.ExitCode != 0)
                throw new GrpcConnectionException($"protoc couldn't write the server's classes: {errors}");

            return [.. Directory.EnumerateFiles(output, "*.cs", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)
                .Select(path => new GeneratedSourceFile(Path.GetRelativePath(output, path).Replace('\\', '/'), File.ReadAllText(path)))];
        }
        finally
        {
            try { work.Delete(true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static (string Protoc, string Plugin) ToolPaths(string toolsPackage)
    {
        var os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macosx" : "linux";
        var arch = os == "macosx" ? "universal" : RuntimeInformation.OSArchitecture switch
        {
            Architecture.X86 => "x86",
            Architecture.Arm64 when os == "linux" => "arm64",
            _ => "x64", // Windows on Arm runs the x64 tools.
        };
        var folder = Path.Combine(toolsPackage, "tools", $"{os}_{arch}");
        var exe = OperatingSystem.IsWindows() ? ".exe" : "";
        var protoc = Path.Combine(folder, "protoc" + exe);
        var plugin = Path.Combine(folder, "grpc_csharp_plugin" + exe);
        if (!File.Exists(protoc) || !File.Exists(plugin))
            throw new GrpcConnectionException($"Grpc.Tools has no protoc for this computer ({os}_{arch}).");
        // NuGet doesn't keep the executable bit of a package's files. Only set it when missing: the
        // package folder may be read-only once it is.
        if (!OperatingSystem.IsWindows())
            foreach (var tool in new[] { protoc, plugin })
                if ((File.GetUnixFileMode(tool) & UnixFileMode.UserExecute) == 0)
                    File.SetUnixFileMode(tool, File.GetUnixFileMode(tool) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        return (protoc, plugin);
    }

    private static bool IsWellKnown(string file) => file.StartsWith("google/protobuf/", StringComparison.Ordinal);

    private static bool HasPlaceholder(string text) => RpadPlaceholders.Parse(text).Parts.Any(p => p.Kind != RpadPlaceholders.Kind.Text);

    private static bool IsHttpAddress(string text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    // ASCII metadata as gRPC allows it; '-bin' entries carry bytes, which a text can't give.
    private static bool IsMetadataKey(string key) =>
        key.Length > 0 && !key.EndsWith("-bin", StringComparison.OrdinalIgnoreCase)
        && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}

/// <summary>A failure of the server or the tools, in words for the user.</summary>
internal sealed class GrpcConnectionException(string message) : Exception(message);

/// <summary>A service of the server and the class protoc wrote for calling it.</summary>
/// <param name="Service">Its full name: <c>inventory.v1.Stock</c>.</param>
/// <param name="ClientType">The client class: <c>global::Inventory.V1.Stock.StockClient</c>.</param>
/// <param name="Property">The connection's property for it: <c>Stock</c>.</param>
internal sealed record GrpcServiceClient(string Service, string ClientType, string Property);

/// <summary>Server reflection, in the v1 protocol or the older v1alpha one the server may offer instead.</summary>
internal sealed partial class GrpcReflection(CallInvoker invoker, Metadata headers)
{
    private static readonly Method<ServerReflectionRequest, ServerReflectionResponse> V1 = new(
        MethodType.DuplexStreaming, "grpc.reflection.v1.ServerReflection", "ServerReflectionInfo",
        Marshallers.Create(r => r.ToByteArray(), ServerReflectionRequest.Parser.ParseFrom),
        Marshallers.Create(r => r.ToByteArray(), ServerReflectionResponse.Parser.ParseFrom));

    private static readonly Method<ServerReflectionRequest, ServerReflectionResponse> V1Alpha = new(
        MethodType.DuplexStreaming, "grpc.reflection.v1alpha.ServerReflection", "ServerReflectionInfo",
        V1.RequestMarshaller, V1.ResponseMarshaller);

    private Method<ServerReflectionRequest, ServerReflectionResponse>? _method;

    public async Task<IReadOnlyList<string>> ListServicesAsync(CancellationToken ct)
    {
        var response = await AskAsync(new ServerReflectionRequest { ListServices = "" }, ct).ConfigureAwait(false);
        return [.. response.ListServicesResponse.Service.Select(s => s.Name)
            .Where(n => !n.StartsWith("grpc.reflection.", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)];
    }

    // The files that define the services, and every file they import, in any order.
    public async Task<IReadOnlyList<FileDescriptorProto>> FilesForAsync(IReadOnlyList<string> services, CancellationToken ct)
    {
        var files = new Dictionary<string, FileDescriptorProto?>(StringComparer.Ordinal);
        foreach (var service in services)
            Add(await AskAsync(new ServerReflectionRequest { FileContainingSymbol = service }, ct).ConfigureAwait(false));
        // A server answers with a file and maybe its imports; ask for the ones it left out.
        for (var missing = Missing(); missing.Count > 0; missing = Missing())
            foreach (var name in missing)
            {
                var response = await AskAsync(new ServerReflectionRequest { FileByFilename = name }, ct, allowNotFound: true).ConfigureAwait(false);
                Add(response);
                // Not sent, or sent under another name: never asked again, and left to protoc's own
                // well-known files (protoc names it if it isn't one).
                files.TryAdd(name, null);
            }
        return [.. files.Values.OfType<FileDescriptorProto>()];

        void Add(ServerReflectionResponse? response)
        {
            foreach (var bytes in response?.FileDescriptorResponse?.FileDescriptorProto ?? [])
            {
                var file = FileDescriptorProto.Parser.ParseFrom(bytes);
                // The names go on protoc's command line: a server must not be able to pass options.
                foreach (var name in file.Dependency.Prepend(file.Name))
                    if (!IsSafeProtoPath(name))
                        throw new GrpcConnectionException($"The server described a file with a name RunPad doesn't accept: '{name}'.");
                files.TryAdd(file.Name, file);
            }
        }

        List<string> Missing() => [.. files.Values.OfType<FileDescriptorProto>().SelectMany(f => f.Dependency).Distinct()
            .Where(d => !files.ContainsKey(d))];
    }

    // A relative path of a .proto file, as protoc names them: no option (-…), response file (@…),
    // absolute path or parent folder.
    internal static bool IsSafeProtoPath(string name) =>
        SafeProtoPath().IsMatch(name) && !name.Split('/').Any(part => part is "" or "." or "..");

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_./-]*\.proto$")]
    private static partial Regex SafeProtoPath();

    private async Task<ServerReflectionResponse> AskAsync(ServerReflectionRequest request, CancellationToken ct) =>
        (await AskAsync(request, ct, allowNotFound: false).ConfigureAwait(false))!;

    private async Task<ServerReflectionResponse?> AskAsync(ServerReflectionRequest request, CancellationToken ct, bool allowNotFound)
    {
        if (_method is null)
        {
            try
            {
                var first = await CallAsync(V1, request, ct).ConfigureAwait(false);
                _method = V1;
                return Check(first);
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Unimplemented)
            {
                _method = V1Alpha;
            }
        }
        return Check(await CallAsync(_method, request, ct).ConfigureAwait(false));

        ServerReflectionResponse? Check(ServerReflectionResponse response)
        {
            if (response.MessageResponseCase != ServerReflectionResponse.MessageResponseOneofCase.ErrorResponse) return response;
            if (allowNotFound && response.ErrorResponse.ErrorCode == (int)StatusCode.NotFound) return null;
            throw new GrpcConnectionException(
                $"The server couldn't describe '{request.FileContainingSymbol}{request.FileByFilename}': {response.ErrorResponse.ErrorMessage}");
        }
    }

    // One request per call keeps it simple; the server's answers are small.
    private async Task<ServerReflectionResponse> CallAsync(Method<ServerReflectionRequest, ServerReflectionResponse> method,
        ServerReflectionRequest request, CancellationToken ct)
    {
        using var call = invoker.AsyncDuplexStreamingCall(method, null, new CallOptions(headers, cancellationToken: ct));
        await call.RequestStream.WriteAsync(request, ct).ConfigureAwait(false);
        await call.RequestStream.CompleteAsync().ConfigureAwait(false);
        if (!await call.ResponseStream.MoveNext(ct).ConfigureAwait(false))
            throw new GrpcConnectionException("The server's reflection service sent no answer.");
        return call.ResponseStream.Current;
    }
}
