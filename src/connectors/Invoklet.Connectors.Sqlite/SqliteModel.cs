using System.Text;

namespace Invoklet.Connectors.Sqlite;

/// <summary>A table as the script sees it: a set on the context and a class for its rows.</summary>
internal sealed record SqliteEntity(SqliteTable Table, string SetName, string ClassName, List<SqliteProperty> Columns, List<string> Key)
{
    // EF Core can only read a table without a primary key (and a view).
    public bool Keyless => Key.Count == 0;

    /// <summary>The foreign keys of this table that the script can walk: each is a reference here and a collection on the other side.</summary>
    public List<SqliteRelation> Relations { get; } = [];
}

/// <summary>
/// A foreign key as navigation properties: <c>order.Customer</c> on the table that holds the key (the dependent) and
/// <c>customer.Orders</c> on the table it points to (the principal).
/// </summary>
internal sealed record SqliteRelation(SqliteEntity Principal, string ReferenceName, string CollectionName,
    List<SqliteProperty> ForeignKey, List<SqliteProperty> PrincipalKey);

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
        var memberSets = new List<HashSet<string>>();
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
            memberSets.Add(members);
        }
        AddRelations(entities, memberSets);
        return entities;
    }

    // A foreign key becomes navigation properties when EF Core can model it: both tables have a key (a table without one can only be read),
    // and each pair of columns has one type. Anything else stays a plain column.
    private static void AddRelations(List<SqliteEntity> entities, List<HashSet<string>> memberSets)
    {
        var byName = entities.Select((e, i) => (e, i)).ToDictionary(x => x.e.Table.Name, x => x, StringComparer.OrdinalIgnoreCase);
        foreach (var (dependent, dependentIndex) in entities.Select((e, i) => (e, i)))
        {
            if (dependent.Keyless) continue;
            foreach (var foreignKey in dependent.Table.ForeignKeys)
            {
                if (!byName.TryGetValue(foreignKey.Table, out var found) || found.e.Keyless) continue;
                var (principal, principalIndex) = found;

                var from = foreignKey.Columns.Select(c => dependent.Columns.Find(p => p.Column.Name.Equals(c, StringComparison.OrdinalIgnoreCase))).ToList();
                // Without "to" the key points at the primary key of the other table.
                var to = foreignKey.ReferencedColumns.Count == 0
                    ? principal.Key.Select(k => principal.Columns.Find(p => p.PropertyName == k)).ToList()
                    : foreignKey.ReferencedColumns.Select(c => principal.Columns.Find(p => p.Column.Name.Equals(c, StringComparison.OrdinalIgnoreCase))).ToList();
                if (from.Count == 0 || from.Count != to.Count || from.Contains(null) || to.Contains(null)) continue;
                if (from.Zip(to).Any(pair => pair.First!.ClrType != pair.Second!.ClrType)) continue;

                // order.customer_id is order.Customer; a key of several columns, or one not named like that, is named after the table.
                var reference = from is [{ } single] && single.PropertyName.Length > 2 && single.PropertyName.EndsWith("Id", StringComparison.Ordinal)
                    ? single.PropertyName[..^2] : principal.ClassName;
                reference = Unique(reference, memberSets[dependentIndex], "Ref");
                var collection = Unique(dependent.SetName, memberSets[principalIndex], "By" + reference);
                dependent.Relations.Add(new SqliteRelation(principal, reference, collection, from!, to!));
            }
        }
    }

    // SQLite's own rules (https://sqlite.org/datatype3.html, "Determination Of Column Affinity"), with
    // the names EF Core reads as dates, Booleans, GUIDs and decimals checked first.
    public static (string Type, bool IsValueType) ClrType(string declared)
    {
        var type = declared.Trim().ToUpperInvariant();
        // No declared type (an expression column of a view, CREATE TABLE … AS SELECT, a full-text
        // table) holds whatever the expression gives; SQLite's own BLOB affinity would read it as bytes.
        if (type.Length == 0) return ("string", false);
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
        if (type.Contains("BLOB", StringComparison.Ordinal)) return ("byte[]", false);
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
