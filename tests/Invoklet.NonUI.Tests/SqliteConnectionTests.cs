using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Invoklet.Connectors.Sqlite;
using Invoklet.Contracts.Connections;
using Invoklet.Contracts.Models;
using Invoklet.Format.RPad;
using Invoklet.Languages.CSharp;
using Invoklet.Languages.CSharp.IntelliSense;
using Invoklet.Languages.DotnetCommon;
using Invoklet.Languages.DotnetCommon.Sdk;
using Invoklet.Windows.Models;
using Xunit;

namespace Invoklet.NonUI.Tests;

// A SQLite connection reads the database's tables and views, and the script gets an EF Core context
// with a set per table. The file here is a real one next to a script, named by a relative path.
public sealed class SqliteConnectionTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("Invoklet-sqlite-connection").FullName;

    public SqliteConnectionTests()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "scripts", "data"));
        using var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE customers (id INTEGER PRIMARY KEY, name TEXT NOT NULL, city TEXT, created DATETIME);
            CREATE TABLE order_items (order_id INTEGER NOT NULL, line INTEGER NOT NULL, qty INT NOT NULL, price DECIMAL(10, 2), PRIMARY KEY (order_id, line));
            CREATE TABLE log (message TEXT);
            CREATE VIEW big_customers AS SELECT id, name FROM customers WHERE city = 'Oslo';
            INSERT INTO customers (name, city, created) VALUES ('Ann', 'Oslo', '2026-01-02 03:04:05'), ('Bob', 'Paris', NULL), ('Cy', 'Oslo', NULL);
            INSERT INTO order_items VALUES (1, 1, 2, 9.5), (1, 2, 3, 1.25);
            INSERT INTO log VALUES ('started');
            """;
        command.ExecuteNonQuery();
    }

    public void Dispose() => Directory.Delete(_folder, true);

    private string ScriptDirectory => Path.Combine(_folder, "scripts");

    private string DatabasePath => Path.Combine(ScriptDirectory, "data", "shop.db");

    private static RpadConnection Shop(string file = "data/shop.db", string? readOnly = null) => new()
    {
        Name = "shop",
        Driver = "sqlite",
        Params = readOnly is null
            ? new(StringComparer.Ordinal) { ["file"] = RpadValue.Of(file) }
            : new(StringComparer.Ordinal) { ["file"] = RpadValue.Of(file), ["readOnly"] = RpadValue.Of(readOnly) },
    };

    private ConnectionSchemas Schemas()
    {
        var drivers = new ConnectionDriverRegistry();
        drivers.Register(new SqliteConnectionDriver());
        return new ConnectionSchemas(drivers, Path.Combine(_folder, "schemas"), DotnetConnectionTools.Default, _ => null);
    }

    private static Dictionary<string, RpadValue> Resolved(RpadConnection connection) =>
        ConnectionPlaceholderResolver.Resolve(connection.Params, _ => null, _ => null).Params.ToDictionary();

    [Fact]
    public void Validate_Should_NameWhatIsWrong()
    {
        var driver = new SqliteConnectionDriver();
        Assert.Empty(driver.Validate(Shop()));
        Assert.Equal(["The database file is missing."], driver.Validate(new RpadConnection { Name = "a", Driver = "sqlite" }));
        Assert.Equal(
            ["An in-memory database has no tables to read: give a database file.", "Read only must be true or false, not 'yes'."],
            driver.Validate(Shop(":memory:", "yes")));
        Assert.True(driver.UsesScriptDirectory(Shop()));
        Assert.True(driver.UsesScriptDirectory(Shop("${env:SHOP_DB}")));
        Assert.False(driver.UsesScriptDirectory(Shop(Path.GetFullPath("/data/shop.db"))));
    }

    [Fact]
    public async Task Test_Should_ListTheTables_OrSayWhyNot()
    {
        var driver = new SqliteConnectionDriver();
        var ok = await driver.TestAsync(Resolved(Shop(DatabasePath)), TestContext.Current.CancellationToken);
        Assert.True(ok.Succeeded, ok.Message);
        Assert.Equal("3 tables and 1 view: big_customers, customers, log, order_items.", ok.Message);

        var relative = await driver.TestAsync(Resolved(Shop()), TestContext.Current.CancellationToken);
        Assert.False(relative.Succeeded);
        Assert.Equal("'data/shop.db' is relative to the script's folder: save the script first, or give a full path.", relative.Message);

        var missing = Path.Combine(_folder, "nope.db");
        var absent = await driver.TestAsync(Resolved(Shop(missing)), TestContext.Current.CancellationToken);
        Assert.Equal($"The database file {missing} doesn't exist.", absent.Message);
        Assert.False(File.Exists(missing)); // never created
    }

    [Fact]
    public void Names_And_Types_Should_ReadAsCSharp()
    {
        Assert.Equal("OrderItems", SqliteModel.Pascal("order_items", "Table"));
        Assert.Equal("OrderItems", SqliteModel.Pascal("Order Items", "Table"));
        Assert.Equal("Table2020", SqliteModel.Pascal("2020", "Table"));
        Assert.Equal("Column", SqliteModel.Pascal("$", "Column"));
        Assert.Equal(["Category", "Address", "Box", "Status", "Data"],
            new[] { "Categories", "Addresses", "Boxes", "Status", "Data" }.Select(SqliteModel.Singular));

        Assert.Equal(("long", true), SqliteModel.ClrType("INTEGER"));
        Assert.Equal(("string", false), SqliteModel.ClrType("varchar(20)"));
        Assert.Equal(("string", false), SqliteModel.ClrType(""));
        Assert.Equal(("byte[]", false), SqliteModel.ClrType("BLOB"));
        Assert.Equal(("double", true), SqliteModel.ClrType("REAL"));
        Assert.Equal(("decimal", true), SqliteModel.ClrType("DECIMAL(10,2)"));
        Assert.Equal(("bool", true), SqliteModel.ClrType("BOOLEAN"));
        Assert.Equal(("global::System.DateTime", true), SqliteModel.ClrType("datetime"));
        Assert.Equal(("global::System.DateOnly", true), SqliteModel.ClrType("DATE"));
        Assert.Equal(("global::System.Guid", true), SqliteModel.ClrType("UUID"));

        // A set never takes a DbContext member's name, and a row class never its set's.
        var model = SqliteModel.Build(
        [
            new SqliteTable("model", false, [new SqliteColumn("model", "TEXT", false, 0)]),
            new SqliteTable("data", false, []),
        ], "ShopClient");
        Assert.Equal(["ModelTable", "Data"], model.Select(e => e.SetName));
        Assert.Equal(["ModelRow", "DataRow"], model.Select(e => e.ClassName));
        Assert.Equal("Model", model[0].Columns[0].PropertyName);
    }

    [Fact]
    [Trait("Category", "RealBuild")]
    public async Task AScript_Should_QueryAndChangeTheDatabase_ThroughItsContext()
    {
        if (!new DotnetSdkProvider().Get().Any()) return;
        var connection = Shop();
        var schemas = Schemas();
        // Relative to the script's folder: read for that folder only.
        Assert.True(await schemas.EnsureAsync([connection], ScriptDirectory, ConnectionSchemaRefresh.Missing, TestContext.Current.CancellationToken));
        Assert.Null(schemas.Cache.ErrorFor(connection, ScriptDirectory));
        Assert.Equal("3 tables and 1 view: big_customers, customers, log, order_items.", schemas.Cache.Get(connection, ScriptDirectory)!.Summary);
        Assert.Null(schemas.Cache.Get(connection, _folder));

        var packages = new SqliteConnectionDriver().Packages;
        var generated = GeneratedConnections.For([connection], RpadLanguage.CSharp, packages.Keys, schemas.Drivers, schemas.Cache, ScriptDirectory)!;
        Assert.Empty(generated.Errors);
        Assert.Contains("public global::Microsoft.EntityFrameworkCore.DbSet<Customer> Customers => Set<Customer>();", generated.Source);
        Assert.Contains("e.HasKey(x => new { x.OrderId, x.Line });", generated.Source);
        Assert.Contains("public sealed class LogRow", generated.Source);

        // The editor knows the sets and the rows' columns before any run.
        var referenceService = new CSharpReferenceService();
        using (var service = new CSharpIntelliSenseService(
                   referenceService.GetDefault().Append(referenceService.GetExtensionReference()), NullLogger<CSharpIntelliSenseService>.Instance))
        {
            service.UpdateExecutionContext(new ExecutionRequest { Packages = packages, Connections = generated });
            const string code = "Connections.Shop.";
            var members = await service.GetCompletionsAsync(code, code.Length, ct: TestContext.Current.CancellationToken);
            Assert.Contains(members!.Items, i => i.DisplayText == "Customers");
            Assert.Contains(members.Items, i => i.DisplayText == "OrderItems");
        }

        var outputDir = Path.Combine(_folder, "build");
        Directory.CreateDirectory(outputDir);
        var options = CompiledDotnetLanguageHostOptions.From(new ExecutionRequest
        {
            Platform = new DotnetSdkProvider().Get().First(),
            OutputDirectory = outputDir,
            WorkingDirectory = outputDir,
            CurrentQueryPath = Path.Combine(ScriptDirectory, "shop.rpad"),
            Packages = packages,
            Connections = generated,
        }, new CSharpReferenceService().GetExtensionReference());
        await using var host = new CSharpLanguageHost(options, NullLoggerFactory.Instance);
        var output = new ConcurrentQueue<string>();
        var errors = new ConcurrentQueue<string>();
        const string script = """
            var shop = Connections.Shop;
            Console.WriteLine(string.Join(",", shop.Customers.Where(c => c.City == "Oslo").OrderBy(c => c.Id).Select(c => c.Name)));
            Console.WriteLine(shop.Customers.Single(c => c.Name == "Ann").Created?.ToString("yyyy-MM-dd HH:mm:ss"));
            Console.WriteLine(shop.OrderItems.Sum(i => i.Qty));
            Console.WriteLine(shop.Log.Single().Message + " " + shop.BigCustomers.Count());
            shop.Customers.Add(new Connections.ShopClient.Customer { Name = "Eve", City = "Bergen" });
            shop.SaveChanges();
            Console.WriteLine(shop.Customers.Count());
            """;
        var result = await host.RunAsync(script, (data, kind) => (kind == OutputKind.StandardError ? errors : output).Enqueue(data),
                TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromMinutes(3), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, string.Join('\n', errors));
        Assert.Equal(["Ann,Cy", "2026-01-02 03:04:05", "5", "started 2", "4"], output.TakeLast(5));
        await using var check = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
        await check.OpenAsync(TestContext.Current.CancellationToken);
        await using var count = check.CreateCommand();
        count.CommandText = "SELECT name FROM customers WHERE id = 4";
        Assert.Equal("Eve", await count.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    // A connection that can't be read is a build error with the reason, here and on the Connections tab.
    [Fact]
    public async Task AMissingFile_Should_BeABuildError_WithTheReason()
    {
        var connection = Shop("data/missing.db");
        var schemas = Schemas();
        await schemas.EnsureAsync([connection], ScriptDirectory, ConnectionSchemaRefresh.Missing, TestContext.Current.CancellationToken);
        var reason = $"The database file {Path.Combine(ScriptDirectory, "data", "missing.db")} doesn't exist.";
        var packages = new SqliteConnectionDriver().Packages.Keys;
        Assert.Equal([$"Connection 'shop': its description couldn't be read: {reason}"],
            GeneratedConnections.For([connection], RpadLanguage.CSharp, packages, schemas.Drivers, schemas.Cache, ScriptDirectory)!.Errors);

        var tab = new ScriptConnectionsViewModel(null, null, schemas.Drivers, schemas);
        tab.UsePackages(() => new HashSet<string>(packages, StringComparer.OrdinalIgnoreCase), (_, _) => { });
        tab.SetScriptPath(Path.Combine(ScriptDirectory, "shop.rpad"));
        tab.Load(new RpadFrontMatter { Connections = [connection] });
        Assert.Equal($"Its description couldn't be read: {reason}", Assert.Single(tab.Items).Problem);
    }

    [Theory]
    [InlineData("code TEXT PRIMARY KEY")]
    [InlineData("code INTEGER PRIMARY KEY DESC")]
    [InlineData("code TEXT, part INTEGER, PRIMARY KEY (code, part)")]
    public async Task NullablePrimaryKeys_Should_BeReadableWithoutTracking(string columns)
    {
        var sql = $"CREATE TABLE nullable_keys ({columns}); INSERT INTO nullable_keys (code) VALUES (NULL);";
        await RunSchemaScriptAsync(sql,
            "Console.WriteLine(Connections.Shop.NullableKeys.ToList().Count);", ["1"]);
    }

    [Theory]
    [InlineData("STORED")]
    [InlineData("VIRTUAL")]
    public async Task GeneratedColumns_Should_ReadAndRefreshWithoutBeingWritten(string storage)
    {
        await RunSchemaScriptAsync($"CREATE TABLE computed (id INTEGER PRIMARY KEY, value INTEGER, twice INTEGER GENERATED ALWAYS AS (value * 2) {storage}); INSERT INTO computed (value) VALUES (3);",
            """
            var shop = Connections.Shop;
            Console.WriteLine(shop.Computed.Single().Twice);
            var row = new Connections.ShopClient.ComputedRow { Value = 4, Twice = 99 };
            shop.Computed.Add(row);
            shop.SaveChanges();
            Console.WriteLine(row.Twice);
            row.Value = 7;
            row.Twice = 999;
            shop.SaveChanges();
            Console.WriteLine(row.Twice);
            """, ["6", "8", "14"]);
    }

    [Theory]
    [InlineData("code TEXT PRIMARY KEY", "STRICT")]
    [InlineData("code TEXT PRIMARY KEY", "WITHOUT ROWID")]
    [InlineData("code TEXT NOT NULL PRIMARY KEY", "")]
    public async Task NonNullablePrimaryKeys_Should_RemainWritable(string columns, string options)
    {
        await RunSchemaScriptAsync($"CREATE TABLE keyed ({columns}) {options}; INSERT INTO keyed VALUES ('first');",
            """
            var shop = Connections.Shop;
            shop.Keyed.Add(new Connections.ShopClient.KeyedRow { Code = "second" });
            shop.SaveChanges();
            Console.WriteLine(shop.Keyed.ToList().Count);
            """, ["2"]);
    }

    // A column without a declared type, such as a view's count(*), is read as text, not as bytes.
    [Fact]
    public async Task UntypedViewColumns_Should_ReadAsText()
    {
        await RunSchemaScriptAsync("CREATE VIEW city_counts AS SELECT city, count(*) AS n FROM customers GROUP BY city;",
            """
            var oslo = Connections.Shop.CityCounts.Single(c => c.City == "Oslo");
            Console.WriteLine(oslo.N);
            """, ["2"]);
    }

    // A table name comes from whoever made the file: a line break C# knows, such as U+2028, must not
    // end the documentation comment it is written in and start a line of code.
    [Fact]
    public async Task ATableName_Should_NeverEndItsDocumentationComment()
    {
        var name = "evil" + (char)0x2028 + "public static int X = 1;" + (char)0x85 + "y" + (char)0x2029 + "z";
        using (var db = new SqliteConnection($"Data Source={DatabasePath};Pooling=False"))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = $"CREATE TABLE \"{name}\" (id INTEGER PRIMARY KEY);";
            command.ExecuteNonQuery();
        }
        var connection = Shop();
        var schemas = Schemas();
        await schemas.EnsureAsync([connection], ScriptDirectory, ConnectionSchemaRefresh.Missing, TestContext.Current.CancellationToken);
        var generated = GeneratedConnections.For([connection], RpadLanguage.CSharp, new SqliteConnectionDriver().Packages.Keys,
            schemas.Drivers, schemas.Cache, ScriptDirectory)!;
        Assert.Empty(generated.Errors);
        Assert.DoesNotContain(generated.Source, c => c is (char)0x85 or (char)0x2028 or (char)0x2029);
        Assert.Contains("evil public static int X = 1; y z", generated.Source, StringComparison.Ordinal);
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(generated.Source, cancellationToken: TestContext.Current.CancellationToken);
        Assert.DoesNotContain(tree.GetRoot(TestContext.Current.CancellationToken).DescendantNodes()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclaratorSyntax>(), v => v.Identifier.Text == "X");

        Assert.Equal("a b c d e", ConnectionCodeContext.Doc("a\rb\u0085c\u2028d\u2029e"));
        Assert.Equal("&lt;a&gt; &amp; b", ConnectionCodeContext.Doc("<a>\t&\0b"));
    }

    [Fact]
    public async Task UpdatedSchemaVersion_Should_IgnoreLegacyCachedDescriptions()
    {
        var connection = Shop(DatabasePath);
        var schemas = Schemas();
        var legacy = new ConnectionSchemaCache(schemas.Cache.Folder);
        legacy.Put(connection, new ConnectionSchema("stale", [], "[]"));
        Assert.Null(schemas.Cache.Get(connection));
        await schemas.EnsureAsync([connection], null, ConnectionSchemaRefresh.Missing, TestContext.Current.CancellationToken);
        Assert.Contains("customers", schemas.Cache.Get(connection)!.Summary, StringComparison.Ordinal);
    }

    private async Task RunSchemaScriptAsync(string sql, string script, string[] expected)
    {
        using (var db = new SqliteConnection($"Data Source={DatabasePath};Pooling=False"))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
        var connection = Shop();
        var schemas = Schemas();
        await schemas.EnsureAsync([connection], ScriptDirectory, ConnectionSchemaRefresh.Missing, TestContext.Current.CancellationToken);
        var packages = new SqliteConnectionDriver().Packages;
        var generated = GeneratedConnections.For([connection], RpadLanguage.CSharp, packages.Keys, schemas.Drivers, schemas.Cache, ScriptDirectory)!;
        Assert.Empty(generated.Errors);
        var outputDir = Path.Combine(_folder, "regression-build");
        Directory.CreateDirectory(outputDir);
        var options = CompiledDotnetLanguageHostOptions.From(new ExecutionRequest
        {
            Platform = new DotnetSdkProvider().Get().First(),
            OutputDirectory = outputDir,
            WorkingDirectory = outputDir,
            CurrentQueryPath = Path.Combine(ScriptDirectory, "shop.rpad"),
            Packages = packages,
            Connections = generated,
        }, new CSharpReferenceService().GetExtensionReference());
        await using var host = new CSharpLanguageHost(options, NullLoggerFactory.Instance);
        var output = new ConcurrentQueue<string>();
        var errors = new ConcurrentQueue<string>();
        var result = await host.RunAsync(script, (data, kind) => (kind == OutputKind.StandardError ? errors : output).Enqueue(data),
                TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, string.Join('\n', errors));
        Assert.Equal(expected, output.TakeLast(expected.Length));
    }
}
