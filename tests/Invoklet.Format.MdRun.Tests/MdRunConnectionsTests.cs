using System.Text.Json;
using Xunit;

namespace Invoklet.Format.MdRun.Tests;

public class MdRunConnectionsTests
{
    // LF, whatever line breaks this source file is saved with.
    private static readonly string Example = """
        ---
        version: "1.1"
        language: csharp
        targetSdk: latest
        kind: Statements
        connections:
          - name: orders
            driver: postgres
            params:
              connectionString: Host=localhost;Database=orders;Username=app;Password=${secret:orders-db}
              commandTimeout: 30
          - name: billing
            driver: http
            params:
              baseAddress: https://billing.local/api/
              headers:
                Authorization: Bearer ${secret:billing-token}
                Accept: application/json
          - name: inventory
            driver: grpc
            params:
              address: https://inventory.local:5001
              services: [inventory.v1.Stock, inventory.v1.Orders]
          - name: local
            driver: sqlite
        ---

        ```csharp run
        1.Dump();
        ```

        """.ReplaceLineEndings("\n");

    private static MdRunDocument Parse(string text)
    {
        var result = MdRunParser.Parse(text);
        Assert.True(result.Document is not null, result.Error);
        return result.Document!;
    }

    [Fact]
    public void Connections_Should_ParseNameDriverAndParams_AsText()
    {
        var connections = Parse(Example).FrontMatter.Connections;
        Assert.Equal(["orders", "billing", "inventory", "local"], connections.Select(c => c.Name));
        Assert.Equal(["postgres", "http", "grpc", "sqlite"], connections.Select(c => c.Driver));

        var orders = connections[0].Params;
        Assert.Equal(["connectionString", "commandTimeout"], orders.Keys);
        Assert.Equal("Host=localhost;Database=orders;Username=app;Password=${secret:orders-db}", orders["connectionString"].Text);
        Assert.Equal("30", orders["commandTimeout"].Text);

        var headers = connections[1].Params["headers"].Fields!;
        Assert.Equal(["Authorization", "Accept"], headers.Keys);
        Assert.Equal("Bearer ${secret:billing-token}", headers["Authorization"].Text);
        Assert.Equal(["inventory.v1.Stock", "inventory.v1.Orders"], connections[2].Params["services"].Items!.Select(i => i.Text));
        Assert.Empty(connections[3].Params);
    }

    [Fact]
    public void WrittenConnections_Should_ReadBackTheSame_AndStayStable()
    {
        var written = MdRunWriter.Write(Parse(Example));
        Assert.Equal(written, MdRunWriter.Write(Parse(written)));
        Assert.Contains("""
            connections:
              - name: orders
                driver: postgres
                params:
                  connectionString: "Host=localhost;Database=orders;Username=app;Password=${secret:orders-db}"
                  commandTimeout: 30
              - name: billing
                driver: http
                params:
                  baseAddress: "https://billing.local/api/"
                  headers:
                    Authorization: "Bearer ${secret:billing-token}"
                    Accept: "application/json"
              - name: inventory
                driver: grpc
                params:
                  address: "https://inventory.local:5001"
                  services:
                    - inventory.v1.Stock
                    - inventory.v1.Orders
              - name: local
                driver: sqlite
            ---
            """.ReplaceLineEndings("\n"), written);
    }

    [Fact]
    public void ADocumentWithConnections_Should_BeWrittenAsVersion11()
    {
        var doc = new MdRunDocument
        {
            FrontMatter = new MdRunFrontMatter { Connections = [new MdRunConnection { Name = "local", Driver = "sqlite" }] },
        };
        Assert.StartsWith("---\nversion: 1.1\n", MdRunWriter.Write(doc));
        Assert.StartsWith("---\nversion: 1.0\n", MdRunWriter.Write(new MdRunDocument { FrontMatter = new MdRunFrontMatter() }));
    }

    [Fact]
    public void NestedValues_Should_RoundTrip_IncludingNonAsciiAndEmptyCollections()
    {
        var doc = new MdRunDocument
        {
            FrontMatter = new MdRunFrontMatter
            {
                Connections =
                [
                    new MdRunConnection
                    {
                        Name = "api",
                        Driver = "http",
                        Params = new(StringComparer.Ordinal)
                        {
                            ["title"] = MdRunValue.Of("Заказы: \"main\"\nline"),
                            ["empty"] = MdRunValue.Of(""),
                            ["nothing"] = MdRunValue.Of("null"),
                            ["none"] = MdRunValue.List([]),
                            ["noFields"] = new MdRunValue { Fields = new(StringComparer.Ordinal) },
                            ["mixed"] = MdRunValue.List([MdRunValue.Map([new("a", MdRunValue.Of("1"))]), MdRunValue.List([MdRunValue.Of("x")])]),
                        },
                    },
                ],
            },
        };
        var parsed = Parse(MdRunWriter.Write(doc)).FrontMatter.Connections.Single().Params;
        Assert.Equal("Заказы: \"main\"\nline", parsed["title"].Text);
        Assert.Equal("", parsed["empty"].Text);
        Assert.Equal("null", parsed["nothing"].Text);
        Assert.Empty(parsed["none"].Items!);
        Assert.Empty(parsed["noFields"].Fields!);
        Assert.Equal("1", parsed["mixed"].Items![0].Fields!["a"].Text);
        Assert.Equal("x", parsed["mixed"].Items![1].Items![0].Text);
        Assert.Contains("Заказы", MdRunWriter.Write(doc));
    }

    [Fact]
    public void KeysANewerInvokletAdds_Should_SurviveASave()
    {
        var text = Example
            .Replace("kind: Statements\n", "kind: Statements\nfutureFlag: on\nfutureList:\n  - a\n  - b\n# about the next key\nfutureMap:\n  x: 1\n")
            .Replace("    driver: sqlite\n", "    driver: sqlite\n    description: Local cache\n")
            .ReplaceLineEndings("\r\n");
        var doc = Parse(text);
        Assert.Equal(["futureFlag: on", "futureList:\n  - a\n  - b\n# about the next key", "futureMap:\n  x: 1"], doc.FrontMatter.UnknownEntries);
        Assert.Equal("Local cache", doc.FrontMatter.Connections[3].Extra!["description"].Text);

        var written = MdRunWriter.Write(doc);
        var again = Parse(written);
        Assert.Equal(doc.FrontMatter.UnknownEntries, again.FrontMatter.UnknownEntries);
        Assert.Equal("Local cache", again.FrontMatter.Connections[3].Extra!["description"].Text);
        Assert.Contains("futureMap:\n  x: 1\n---", written);
    }

    // A repeated key is what a hand edit or a merge leaves behind; the deserializer takes its last
    // value, and collecting the unknown keys must not turn that into a parse error.
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void ARepeatedKnownKey_Should_ParseWithItsLastValue(string newline)
    {
        var text = "---\nversion: \"1.0\"\nkind: Statements\nfutureFlag: on\nkind: Program\n---\n```csharp run\nvoid Main() { }\n```\n"
            .ReplaceLineEndings(newline);
        var doc = Parse(text);
        Assert.Equal(MdRunKind.Program, doc.FrontMatter.Kind);
        Assert.Equal(["futureFlag: on"], doc.FrontMatter.UnknownEntries);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void ARepeatedUnknownKey_Should_KeepOnlyItsLastEntry(string newline)
    {
        var text = "---\nfuture: 1\nkind: Statements\nfutureList:\n  - a\nfuture:\n  x: 2\n---\n```csharp run\n```\n"
            .ReplaceLineEndings(newline);
        var doc = Parse(text);
        Assert.Equal(["futureList:\n  - a", "future:\n  x: 2"], doc.FrontMatter.UnknownEntries);

        var again = Parse(MdRunWriter.Write(doc));
        Assert.Equal(doc.FrontMatter.UnknownEntries, again.FrontMatter.UnknownEntries);
    }

    [Fact]
    public void UnknownKeysOfAFlowStyleFrontMatter_Should_SurviveASave()
    {
        var doc = Parse("---\r\n{version: '1.0', future: 3, nested: {a: [1, 2]}, kind: Program}\r\n---\r\n```csharp run\r\nvoid Main() { }\r\n```\r\n");
        Assert.Equal(MdRunKind.Program, doc.FrontMatter.Kind);
        Assert.Equal(["future: 3", "nested: {a: [1, 2]}"], doc.FrontMatter.UnknownEntries);

        var again = Parse(MdRunWriter.Write(doc));
        Assert.Equal(MdRunKind.Program, again.FrontMatter.Kind);
        Assert.Equal(doc.FrontMatter.UnknownEntries, again.FrontMatter.UnknownEntries);
    }

    // A value of the front matter as YAML reads it (aliases resolved), to compare a file before and
    // after a save by meaning rather than by text.
    private static object? YamlValue(string document, string key)
    {
        var yaml = document.ReplaceLineEndings("\n").Split("---\n")[1];
        return new YamlDotNet.Serialization.Deserializer().Deserialize<Dictionary<string, object?>>(yaml).GetValueOrDefault(key);
    }

    // "|+" keeps the value's final line breaks; trimming the entry like any other dropped them, in
    // the middle of the front matter and at its end alike.
    [Theory]
    [InlineData("---\nversion: \"1.0\"\nfuture: |+\n  text\n\n\nkind: Statements\n---\n```csharp run\n1.Dump();\n```\n")]
    [InlineData("---\nversion: \"1.0\"\nkind: Statements\nfuture: >+ # folded, kept\n  text\n\n---\n```csharp run\n1.Dump();\n```\n")]
    [InlineData("---\r\nversion: \"1.0\"\r\nkind: Statements\r\nfuture: |+\r\n  text\r\n\r\n\r\n---\r\n```csharp run\r\n1.Dump();\r\n```\r\n")]
    public void AnUnknownBlockScalarThatKeepsLineBreaks_Should_KeepThemThroughSaves(string text)
    {
        var written = MdRunWriter.Write(Parse(text));

        Assert.Equal(YamlValue(text, "future"), YamlValue(written, "future"));
        Assert.Equal(written, MdRunWriter.Write(Parse(written)));
    }

    // MdRunWriter writes the known keys from the model, without their anchors: an unknown entry's
    // alias of one would point at nothing. It is saved as the value it stood for.
    [Fact]
    public void AnUnknownKeysAliasOfAKnownKeysAnchor_Should_BeSavedAsItsValue()
    {
        var text = "---\nversion: &v \"1.0\"\nkind: Statements\nfuture: [*v, 2]\n---\n```csharp run\n1.Dump();\n```\n";
        var written = MdRunWriter.Write(Parse(text));

        Assert.DoesNotContain("*v", written);
        Assert.Equal(YamlValue(text, "future"), YamlValue(written, "future"));
        Assert.Equal(written, MdRunWriter.Write(Parse(written)));
    }

    // The deserializer skips unknown keys, anchors and all: a known key's alias of an anchor in one
    // stopped the file opening. So did any alias in a connection; both now read as their values.
    [Fact]
    public void AKnownKeysAliasOfAnUnknownKeysAnchor_AndAConnectionsAlias_Should_Load()
    {
        var text = "---\nversion: \"1.0\"\nfuture: &k Program\nkind: *k\nhost: &h https://a.example/\n"
            + "connections:\n  - name: a\n    driver: http\n    params:\n      baseAddress: *h\n"
            + "---\n```csharp run\nvoid Main() { }\n```\n";
        var doc = Parse(text);

        Assert.Equal(MdRunKind.Program, doc.FrontMatter.Kind);
        Assert.Equal("https://a.example/", doc.FrontMatter.Connections.Single().Params["baseAddress"].Text);
        var again = Parse(MdRunWriter.Write(doc));
        Assert.Equal(MdRunKind.Program, again.FrontMatter.Kind);
        Assert.Equal(doc.FrontMatter.UnknownEntries, again.FrontMatter.UnknownEntries);
    }

    // Without an alias the front matter is read as written, comments in unknown entries included.
    [Fact]
    public void AnchorsWithoutAliases_Should_LeaveUnknownEntriesAsWritten()
    {
        var doc = Parse("---\nversion: \"1.0\"\nfuture: &f # note\n  x: 1\nkind: Statements\n---\n```csharp run\n1.Dump();\n```\n");
        Assert.Equal(["future: &f # note\n  x: 1"], doc.FrontMatter.UnknownEntries);
    }

    [Theory]
    [InlineData("connections:\n  - orders\n")]
    [InlineData("connections:\n  - name: a\n    driver: http\n    params: text\n")]
    [InlineData("connections:\n  - name: [a]\n    driver: http\n")]
    public void MalformedConnections_Should_FailToParse(string yaml)
    {
        var result = MdRunParser.Parse($"---\n{yaml}---\n```csharp run\n```\n");
        Assert.Null(result.Document);
        Assert.Contains("Invalid YAML front matter", result.Error);
    }

    [Fact]
    public void Placeholders_Should_SplitTextSecretsAndVariables()
    {
        var result = MdRunPlaceholders.Parse("Host=${env:DB_HOST};Password=${secret: orders-db };Literal=$${not:this}");
        Assert.Null(result.Error);
        Assert.Equal(
            [
                new MdRunPlaceholders.Part(MdRunPlaceholders.Kind.Text, "Host="),
                new MdRunPlaceholders.Part(MdRunPlaceholders.Kind.Environment, "DB_HOST"),
                new MdRunPlaceholders.Part(MdRunPlaceholders.Kind.Text, ";Password="),
                new MdRunPlaceholders.Part(MdRunPlaceholders.Kind.Secret, "orders-db"),
                new MdRunPlaceholders.Part(MdRunPlaceholders.Kind.Text, ";Literal=${not:this}"),
            ],
            result.Parts);
        Assert.Equal([new MdRunPlaceholders.Part(MdRunPlaceholders.Kind.Text, "a $ b {c}")], MdRunPlaceholders.Parse("a $ b {c}").Parts);
    }

    [Theory]
    [InlineData("Password=${secret:db", "not closed")]
    [InlineData("${vault:db}", "is not a placeholder")]
    [InlineData("${secret}", "is not a placeholder")]
    [InlineData("${secret: }", "names no secret")]
    [InlineData("${env:}", "names no variable")]
    public void MalformedPlaceholders_Should_SayWhatIsWrong(string text, string message)
        => Assert.Contains(message, MdRunPlaceholders.Parse(text).Error);

    [Theory]
    [InlineData("orders", "Orders")]
    [InlineData("orders-db", "OrdersDb")]
    [InlineData("my_api.v2", "MyApiV2")]
    [InlineData("billingAPI", "BillingAPI")]
    public void CodeName_Should_BePascalCase(string name, string expected)
        => Assert.Equal(expected, MdRunConnection.CodeName(name));

    [Fact]
    public void Validate_Should_ReportNamesDriversAndPlaceholders()
    {
        static MdRunConnection C(string name, string driver = "http", string? text = null) => new()
        {
            Name = name,
            Driver = driver,
            Params = text is null ? new(StringComparer.Ordinal) : new(StringComparer.Ordinal) { ["headers"] = MdRunValue.Map([new("Authorization", MdRunValue.Of(text))]) },
        };
        Assert.Empty(MdRunConnection.Validate([C("orders"), C("billing-api", text: "Bearer ${secret:token}")]));

        var errors = MdRunConnection.Validate([C(""), C("1st"), C("orders"), C("Orders"), C("orders-db"), C("ordersDb"), C("nodriver", ""), C("bad", text: "${secret:x")]);
        Assert.Equal(
            [
                "A connection has no name.",
                "'1st' is not a valid connection name: start with a letter, then use letters, digits, '-', '_' or '.'.",
                "Two connections are named 'Orders'.",
                "Connections 'orders-db' and 'ordersDb' would have the same name in code, OrdersDb.",
                "Connection 'nodriver' has no driver.",
                "Connection 'bad', headers: '${' at position 1 is not closed by '}' (write '$${' for a literal '${').",
            ],
            errors);
    }

    // The generated class holding every connection is named Connections, so no connection can be.
    [Theory]
    [InlineData("connections")]
    [InlineData("Connections")]
    public void Validate_Should_RejectTheReservedCodeName(string name)
    {
        Assert.Equal(
            [$"Connection '{name}': the code name Connections is reserved."],
            MdRunConnection.Validate([new MdRunConnection { Name = name, Driver = "http" }]));
        Assert.Empty(MdRunConnection.Validate([new MdRunConnection { Name = "connections-db", Driver = "http" }]));
    }

    // Draft recovery keeps documents as JSON (MdRunDocumentSnapshot): connections must come back whole.
    [Fact]
    public void ASnapshot_Should_KeepConnectionsAndUnknownKeys_ThroughJson()
    {
        var doc = Parse(Example.Replace("kind: Statements\n", "kind: Statements\nfutureFlag: on\n"));
        var json = JsonSerializer.Serialize(doc.CaptureSnapshot());
        var restored = MdRunDocument.FromSnapshot(JsonSerializer.Deserialize<MdRunDocumentSnapshot>(json)!);
        Assert.Equal(MdRunWriter.Write(doc), MdRunWriter.Write(restored));

        // The snapshot is a copy: editing the document doesn't change it.
        var snapshot = doc.CaptureSnapshot();
        doc.FrontMatter.Connections[1].Params["headers"].Fields!["Accept"] = MdRunValue.Of("text/plain");
        Assert.Equal("application/json", snapshot.FrontMatter.Connections[1].Params["headers"].Fields!["Accept"].Text);
    }

    [Fact]
    public void ThePreview_Should_ListTheConnections()
        => Assert.Contains("| Connections | ` orders ` (postgres), ` billing ` (http), ` inventory ` (grpc), ` local ` (sqlite) |",
            MdRunPreview.ToMarkdown(Parse(Example), "1.Dump();\n"));
}
