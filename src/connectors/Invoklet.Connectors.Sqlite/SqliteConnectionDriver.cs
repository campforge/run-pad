using System.Text;
using System.Text.Json;
using Invoklet.Contracts.Connections;
using Invoklet.Format.MdRun;
using Microsoft.Data.Sqlite;

namespace Invoklet.Connectors.Sqlite;

/// <summary>
/// A SQLite database file. Invoklet reads its tables and views, and the script gets an Entity
/// Framework Core context with a set per table, as LINQPad gives a typed data context:
/// <c>Connections.Shop.Customers.Where(c => c.City == "Oslo")</c>. A relative path is read from
/// the script's folder.
/// </summary>
public sealed class SqliteConnectionDriver : IConnectionSchemaDriver
{
    public const string DriverId = "sqlite";

    // The version the script's context is built with.
    public const string EntityFrameworkVersion = "10.0.13";

    private const string File = "file";
    private const string ReadOnly = "readOnly";

    public string Id => DriverId;

    public string SchemaVersion => "2";

    public string DisplayName => "SQLite";

    public ConnectionCategory Category => ConnectionCategory.Database;

    public IReadOnlyDictionary<string, string> Packages { get; } = new Dictionary<string, string>
    {
        ["Microsoft.EntityFrameworkCore.Sqlite"] = EntityFrameworkVersion,
    };

    public IReadOnlyList<ConnectionParam> Params { get; } =
    [
        new(File, "Database file", Required: true, Example: "data/shop.db",
            Description: "A relative path is read from the script's folder. The file must exist."),
        new(ReadOnly, "Read only", ConnectionParamType.Boolean, Default: "false",
            Description: "Opens the file for reading only, so the script can't change it."),
    ];

    public IReadOnlyList<string> Validate(MdRunConnection connection)
    {
        var errors = new List<string>();
        if (!connection.Params.TryGetValue(File, out var file) || string.IsNullOrWhiteSpace(file.Text))
            errors.Add("The database file is missing.");
        else if (file.Text.Trim() == ":memory:")
            errors.Add("An in-memory database has no tables to read: give a database file.");
        if (connection.Params.TryGetValue(ReadOnly, out var readOnly) && readOnly.Text is { Length: > 0 } flag && !bool.TryParse(flag, out _))
            errors.Add($"Read only must be true or false, not '{flag}'.");
        return errors;
    }

    // A relative path, or one a placeholder may make relative, depends on the script's folder.
    public bool UsesScriptDirectory(MdRunConnection connection) =>
        connection.Params.TryGetValue(File, out var file) && file.Text is { } text
        && (MdRunPlaceholders.Parse(text).Parts.Any(p => p.Kind != MdRunPlaceholders.Kind.Text) || !Path.IsPathRooted(text));

    public async Task<ConnectionTestResult> TestAsync(IReadOnlyDictionary<string, MdRunValue> resolvedParams, CancellationToken cancellationToken)
    {
        try
        {
            var tables = await ReadAsync(resolvedParams, null, cancellationToken).ConfigureAwait(false);
            return new ConnectionTestResult(true, Summary(tables));
        }
        catch (SqliteConnectionException ex)
        {
            return new ConnectionTestResult(false, ex.Message);
        }
    }

    public async Task<ConnectionSchema> FetchSchemaAsync(IReadOnlyDictionary<string, MdRunValue> resolvedParams, ConnectionTools tools,
        CancellationToken cancellationToken)
    {
        var tables = await ReadAsync(resolvedParams, tools.ScriptDirectory, cancellationToken).ConfigureAwait(false);
        return new ConnectionSchema(Summary(tables), [], JsonSerializer.Serialize(tables));
    }

    // A DbContext opening the file, with a set per table and view, each a nested class.
    public ConnectionClientCode CreateClient(MdRunConnection connection, ConnectionCodeContext context)
    {
        var file = connection.Params[File].Text!.Trim();
        var readOnly = connection.Params.TryGetValue(ReadOnly, out var r) && bool.TryParse(r.Text, out var flag) && flag;
        var tables = context.Schema is { } schema ? JsonSerializer.Deserialize<List<SqliteTable>>(schema.Data) ?? [] : [];
        var model = SqliteModel.Build(tables, context.ClassName);
        const string ef = "global::Microsoft.EntityFrameworkCore";
        const string io = "global::System.IO.Path";

        var sb = new StringBuilder()
            .Append("/// <summary>The SQLite connection '").Append(ConnectionCodeContext.Doc(connection.Name)).Append("' to ")
            .Append(ConnectionCodeContext.Doc(file)).Append(": an Entity Framework Core context with a set per table.</summary>\n")
            .Append("public sealed class ").Append(context.ClassName).Append(" : ").Append(ef).Append(".DbContext\n{\n")
            .Append("    internal ").Append(context.ClassName).Append("()\n    {\n")
            .Append("        var file = ").Append(context.Text(file)).Append(";\n")
            .Append("        if (!").Append(io).Append(".IsPathRooted(file))\n")
            .Append("            file = ").Append(io).Append(".GetFullPath(file, ").Append(io).Append(".GetDirectoryName(global::Util.CurrentQueryPath)\n")
            .Append("                ?? throw new global::System.InvalidOperationException(")
            .Append(ConnectionCodeContext.Literal($"Connection '{connection.Name}': the database file '{file}' is relative to the script's folder: save the script first, or give a full path."))
            .Append("));\n")
            .Append("        ConnectionString = new global::Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = file, Mode = ")
            .Append("global::Microsoft.Data.Sqlite.SqliteOpenMode.").Append(readOnly ? "ReadOnly" : "ReadWrite").Append(" }.ToString();\n")
            .Append("    }\n\n")
            .Append("    /// <summary>What the context opens the database with.</summary>\n")
            .Append("    public string ConnectionString { get; }\n\n")
            .Append("    protected override void OnConfiguring(").Append(ef).Append(".DbContextOptionsBuilder optionsBuilder) =>\n")
            .Append("        ").Append(ef).Append(".SqliteDbContextOptionsBuilderExtensions.UseSqlite(optionsBuilder, ConnectionString);\n");

        foreach (var entity in model)
            sb.Append('\n')
                .Append("    /// <summary>The ").Append(entity.Table.IsView ? "view " : "table ").Append(ConnectionCodeContext.Doc(entity.Table.Name))
                .Append(entity.Keyless ? " (read only: it has no non-null primary key)" : "").Append(".</summary>\n")
                .Append("    public ").Append(ef).Append(".DbSet<").Append(entity.ClassName).Append("> ").Append(entity.SetName)
                .Append(" => Set<").Append(entity.ClassName).Append(">();\n");

        sb.Append('\n')
            .Append("    protected override void OnModelCreating(").Append(ef).Append(".ModelBuilder modelBuilder)\n    {\n");
        foreach (var entity in model)
        {
            sb.Append("        modelBuilder.Entity<").Append(entity.ClassName).Append(">(e =>\n        {\n")
                .Append("            ").Append(ef).Append(".RelationalEntityTypeBuilderExtensions.").Append(entity.Table.IsView ? "ToView" : "ToTable")
                .Append("(e, ").Append(ConnectionCodeContext.Literal(entity.Table.Name)).Append(");\n");
            if (entity.Keyless) sb.Append("            e.HasNoKey();\n");
            else if (entity.Key.Count == 1) sb.Append("            e.HasKey(x => x.").Append(entity.Key[0]).Append(");\n");
            else sb.Append("            e.HasKey(x => new { ").Append(string.Join(", ", entity.Key.Select(k => "x." + k))).Append(" });\n");
            foreach (var column in entity.Columns)
            {
                sb.Append("            ").Append(ef).Append(".RelationalPropertyBuilderExtensions.HasColumnName(e.Property(x => x.")
                    .Append(column.PropertyName).Append("), ").Append(ConnectionCodeContext.Literal(column.Column.Name)).Append(");\n");
                if (column.Column.Generated)
                    sb.Append("            e.Property(x => x.").Append(column.PropertyName).Append(").ValueGeneratedOnAddOrUpdate();\n");
            }
            sb.Append("        });\n");
        }
        sb.Append("    }\n");

        foreach (var entity in model)
        {
            sb.Append('\n')
                .Append("    /// <summary>A row of the ").Append(entity.Table.IsView ? "view " : "table ").Append(ConnectionCodeContext.Doc(entity.Table.Name))
                .Append(".</summary>\n")
                .Append("    public sealed class ").Append(entity.ClassName).Append("\n    {\n");
            foreach (var column in entity.Columns)
            {
                sb.Append("        public ").Append(column.ClrType).Append(column.Nullable ? "?" : "").Append(' ').Append(column.PropertyName)
                    .Append(" { get; set; }");
                if (!column.Nullable && !column.IsValueType) sb.Append(" = null!;");
                sb.Append('\n');
            }
            sb.Append("    }\n");
        }
        sb.Append('}');
        return new ConnectionClientCode(sb.ToString());
    }

    private static string Summary(IReadOnlyList<SqliteTable> tables)
    {
        if (tables.Count == 0) return "The database has no tables yet.";
        var tableCount = tables.Count(t => !t.IsView);
        var viewCount = tables.Count - tableCount;
        var counts = string.Join(" and ", new[]
        {
            tableCount == 0 ? null : tableCount == 1 ? "1 table" : $"{tableCount} tables",
            viewCount == 0 ? null : viewCount == 1 ? "1 view" : $"{viewCount} views",
        }.OfType<string>());
        const int shown = 10;
        return $"{counts}: {string.Join(", ", tables.Take(shown).Select(t => t.Name))}{(tables.Count > shown ? ", …" : "")}.";
    }

    // Reads the tables and views of the file the parameters name, opened for reading only so that
    // Invoklet never creates or changes it.
    private static async Task<List<SqliteTable>> ReadAsync(IReadOnlyDictionary<string, MdRunValue> resolvedParams, string? scriptDirectory,
        CancellationToken cancellationToken)
    {
        if (!resolvedParams.TryGetValue(File, out var fileValue) || fileValue.Text is not { Length: > 0 } text)
            throw new SqliteConnectionException("The database file is missing.");
        var file = text.Trim();
        if (!Path.IsPathRooted(file))
        {
            if (scriptDirectory is null)
                throw new SqliteConnectionException(
                    $"'{file}' is relative to the script's folder: save the script first, or give a full path.");
            file = Path.GetFullPath(file, scriptDirectory);
        }
        if (!System.IO.File.Exists(file))
            throw new SqliteConnectionException($"The database file {file} doesn't exist.");

        var connectionString = new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
        try
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var tables = new List<SqliteTable>();
            await using (var list = connection.CreateCommand())
            {
                list.CommandText = "SELECT name, type FROM sqlite_schema WHERE type IN ('table', 'view') AND name NOT LIKE 'sqlite\\_%' ESCAPE '\\' ORDER BY name";
                await using var reader = await list.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    tables.Add(new SqliteTable(reader.GetString(0), reader.GetString(1) == "view", []));
            }
            foreach (var table in tables)
            {
                // A true INTEGER PRIMARY KEY aliases rowid and has no separate primary-key index.
                // INTEGER PRIMARY KEY DESC is a notable exception and can contain NULL.
                await using var indexes = connection.CreateCommand();
                indexes.CommandText = "SELECT count(*) FROM pragma_index_list($table) WHERE origin = 'pk'";
                indexes.Parameters.AddWithValue("$table", table.Name);
                var hasPrimaryKeyIndex = Convert.ToInt64(await indexes.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) > 0;
                await using var info = connection.CreateCommand();
                info.CommandText = "SELECT name, type, \"notnull\", pk, hidden FROM pragma_table_xinfo($table) WHERE hidden != 1 ORDER BY cid";
                info.Parameters.AddWithValue("$table", table.Name);
                await using var reader = await info.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    table.Columns.Add(new SqliteColumn(reader.GetString(0), reader.IsDBNull(1) ? "" : reader.GetString(1),
                        reader.GetInt64(2) != 0, (int)reader.GetInt64(3), reader.GetInt64(4) is 2 or 3));
                if (!table.IsView && !hasPrimaryKeyIndex && table.Columns.Where(c => c.PrimaryKey > 0).ToList() is [var key]
                    && key.Type.Equals("INTEGER", StringComparison.OrdinalIgnoreCase))
                {
                    var index = table.Columns.IndexOf(key);
                    table.Columns[index] = key with { NotNull = true };
                }
            }
            return tables;
        }
        catch (SqliteException ex)
        {
            throw new SqliteConnectionException($"The database file {file} can't be read: {ex.Message}");
        }
    }
}

/// <summary>A failure to read the database, in words for the user.</summary>
internal sealed class SqliteConnectionException(string message) : Exception(message);

/// <summary>A table or view as SQLite describes it.</summary>
internal sealed record SqliteTable(string Name, bool IsView, List<SqliteColumn> Columns);

/// <param name="Type">The declared type, which SQLite only uses as a hint (its type affinity).</param>
/// <param name="PrimaryKey">Its position in the primary key, from 1; 0 when it isn't part of it.</param>
internal sealed record SqliteColumn(string Name, string Type, bool NotNull, int PrimaryKey, bool Generated = false);
