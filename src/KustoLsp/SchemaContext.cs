using System.Text.Json;
using Kusto.Language;
using Kusto.Language.Symbols;

namespace KustoLsp;

public sealed record SchemaContext(GlobalState Globals)
{
    public static SchemaContext ParseFile(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return Parse(document.RootElement);
    }

    public static SchemaContext Parse(JsonElement schema)
    {
        if (Protocol.Integer(schema, "version") != 1)
            throw Protocol.Invalid("Unsupported schema version; expected 1");
        var clusterName = Protocol.String(schema, "cluster");
        var databaseName = Protocol.String(schema, "database");
        var members = new List<Symbol>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var table in Protocol.Array(schema, "tables"))
        {
            var name = UniqueName(table, names);
            var columns = new List<ColumnSymbol>();
            var columnNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var column in Protocol.Array(table, "columns"))
                columns.Add(new ColumnSymbol(UniqueName(column, columnNames), Scalar(column)));
            members.Add(new TableSymbol(name, columns));
        }
        foreach (var function in Protocol.Array(schema, "functions"))
        {
            var name = UniqueName(function, names);
            var parameters = new List<Parameter>();
            var parameterNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var parameter in Protocol.Array(function, "parameters"))
                parameters.Add(new Parameter(UniqueName(parameter, parameterNames), Scalar(parameter)));
            var body = Protocol.String(function, "body");
            members.Add(new FunctionSymbol(name, body, parameters));
        }
        var database = new DatabaseSymbol(databaseName, members);
        var cluster = new ClusterSymbol(clusterName, database);
        return new(GlobalState.Default.WithCluster(cluster).WithDatabase(database));
    }

    private static string UniqueName(JsonElement value, HashSet<string> names)
    {
        var name = Protocol.String(value, "name");
        if (!names.Add(name)) throw Protocol.Invalid($"Duplicate schema name: {name}");
        return name;
    }

    private static ScalarSymbol Scalar(JsonElement value)
    {
        var name = Protocol.String(value, "type");
        return ScalarTypes.GetSymbol(name) ?? throw Protocol.Invalid($"Unknown Kusto scalar type: {name}");
    }
}
