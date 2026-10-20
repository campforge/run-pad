using System.Text;

namespace RunPad.Connectors.Sqlite;

/// <summary>A table as the script sees it: a set on the context and a class for its rows.</summary>
internal sealed record SqliteEntity(SqliteTable Table, string SetName, string ClassName, List<SqliteProperty> Columns, List<string> Key)
{
    // EF Core can only read a table without a primary key (and a view).
    public bool Keyless => Key.Count == 0;
}

/// <summary>A column as a property of its row class.</summary>
internal sealed record SqliteProperty(SqliteColumn Column, string PropertyName, string ClrType, bool IsValueType, bool Nullable);

/// <summary>
/// Turns the database's names and types into C#: <c>order_items</c> is the set <c>OrderItems</c> of
/// <c>OrderItem</c> rows, a column is a property in PascalCase, and a declared type is read the
/// way SQLite reads it (by the words it contains), with the dates, Booleans and GUIDs EF Core
/// knows.
/// </summary>
internal static class SqliteModel
{
    // Members of DbContext (and of object) a set or a row class can't be named.
    private static readonly string[] ContextMembers =
    [
        "Add", "AddAsync", "AddRange", "AddRangeAsync", "Attach", "AttachRange", "ChangeTracker", "ConfigureConventions",
        "ConnectionString", "ContextId", "Database", "Dispose", "DisposeAsync", "Entry", "Equals", "Finalize", "Find",
        "FindAsync", "FromExpression", "GetHashCode", "GetType", "MemberwiseClone", "Model", "OnConfiguring",
        "OnModelCreating", "Remove", "RemoveRange", "SaveChanges", "SaveChangesAsync", "SaveChangesFailed", "SavedChanges",
        "SavingChanges", "Set", "ToString", "Update", "UpdateRange",
    ];

    private static readonly string[] ObjectMembers = ["Equals", "GetHashCode", "GetType", "MemberwiseClone", "ToString"];

    public static List<SqliteEntity> Build(IReadOnlyList<SqliteTable> tables, string clientClassName)
    {
        var taken = new HashSet<string>(ContextMembers, StringComparer.Ordinal) { clientClassName };
        var entities = new List<SqliteEntity>();
        foreach (var table in tables)
        {
            var name = Pascal(table.Name, "Table");
            var setName = Unique(name, taken, "Table");
            var singular = Singular(name);
            var className = Unique(singular == setName ? singular + "Row" : singular, taken, "Row");

            var members = new HashSet<string>(ObjectMembers, StringComparer.Ordinal) { className };
            var columns = new List<SqliteProperty>();
            foreach (var column in table.Columns)
            {
                var (type, isValueType) = ClrType(column.Type);
                columns.Add(new SqliteProperty(column, Unique(Pascal(column.Name, "Column"), members, "Value"), type, isValueType,
                    Nullable: !column.NotNull));
            }
            // EF keys cannot contain NULL; ordinary SQLite primary keys may allow it.
            var key = table.IsView || columns.Any(c => c.Column.PrimaryKey > 0 && c.Nullable)
                ? [] : columns.Where(c => c.Column.PrimaryKey > 0).OrderBy(c => c.Column.PrimaryKey)
                .Select(c => c.PropertyName).ToList();
            entities.Add(new SqliteEntity(table, setName, className, columns, key));
        }
        return entities;
    }

    // SQLite's own rules (https://sqlite.org/datatype3.html, "Determination Of Column Affinity"), with
    // the names EF Core reads as dates, Booleans, GUIDs and decimals checked first.
    public static (string Type, bool IsValueType) ClrType(string declared)
    {
        var type = declared.ToUpperInvariant();
        if (type.Contains("BOOL", StringComparison.Ordinal)) return ("bool", true);
        if (type.Contains("DATETIME", StringComparison.Ordinal) || type.Contains("TIMESTAMP", StringComparison.Ordinal))
            return ("global::System.DateTime", true);
        if (type.Contains("DATE", StringComparison.Ordinal)) return ("global::System.DateOnly", true);
        if (type.Contains("TIME", StringComparison.Ordinal)) return ("global::System.TimeOnly", true);
        if (type.Contains("GUID", StringComparison.Ordinal) || type.Contains("UUID", StringComparison.Ordinal)
            || type.Contains("UNIQUEIDENTIFIER", StringComparison.Ordinal))
            return ("global::System.Guid", true);
        if (type.Contains("INT", StringComparison.Ordinal)) return ("long", true);
        if (type.Contains("CHAR", StringComparison.Ordinal) || type.Contains("CLOB", StringComparison.Ordinal)
            || type.Contains("TEXT", StringComparison.Ordinal))
            return ("string", false);
        if (type.Length == 0 || type.Contains("BLOB", StringComparison.Ordinal)) return ("byte[]", false);
        if (type.Contains("REAL", StringComparison.Ordinal) || type.Contains("FLOA", StringComparison.Ordinal)
            || type.Contains("DOUB", StringComparison.Ordinal))
            return ("double", true);
        if (type.Contains("DEC", StringComparison.Ordinal) || type.Contains("NUMERIC", StringComparison.Ordinal)
            || type.Contains("MONEY", StringComparison.Ordinal))
            return ("decimal", true);
        // Any other name has numeric affinity, which can hold anything: text reads every value.
        return ("string", false);
    }

    // order_items, "Order Items" and orderItems are OrderItems; a name with nothing to keep is the fallback.
    public static string Pascal(string name, string fallback)
    {
        var sb = new StringBuilder(name.Length);
        var upper = true;
        foreach (var c in name)
        {
            if (!char.IsLetterOrDigit(c) || c > 127 && !char.IsLetter(c))
            {
                upper = true;
                continue;
            }
            sb.Append(upper ? char.ToUpperInvariant(c) : c);
            upper = false;
        }
        if (sb.Length == 0) return fallback;
        if (char.IsDigit(sb[0])) sb.Insert(0, fallback);
        return sb.ToString();
    }

    // English plurals as table names usually have them; anything else stays as it is.
    public static string Singular(string name) =>
        name.EndsWith("ies", StringComparison.Ordinal) && name.Length > 3 ? name[..^3] + "y"
        : name.EndsWith("sses", StringComparison.Ordinal) || name.EndsWith("xes", StringComparison.Ordinal)
          || name.EndsWith("ches", StringComparison.Ordinal) || name.EndsWith("shes", StringComparison.Ordinal) ? name[..^2]
        : name.EndsWith('s') && !name.EndsWith("ss", StringComparison.Ordinal) && !name.EndsWith("us", StringComparison.Ordinal)
          && !name.EndsWith("is", StringComparison.Ordinal) && name.Length > 1 ? name[..^1]
        : name;

    private static string Unique(string name, HashSet<string> taken, string suffix)
    {
        if (taken.Add(name)) return name;
        var withSuffix = name + suffix;
        var candidate = withSuffix;
        for (var i = 2; !taken.Add(candidate); i++) candidate = withSuffix + i;
        return candidate;
    }
}
