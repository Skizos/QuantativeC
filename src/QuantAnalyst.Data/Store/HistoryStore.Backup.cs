using System.Data.Common;
using DuckDB.NET.Data;

namespace QuantAnalyst.Data.Store;

/// <summary>Plan 25: a consistent copy of the store for the backup, and the row counts that check it.</summary>
public sealed partial class HistoryStore
{
    private const string CopyAlias = "qa_backup_copy";

    /// <summary>
    /// Copies the whole store (tables, views, data) into the new file <paramref name="path"/> with DuckDB's own
    /// <c>ATTACH</c> + <c>COPY FROM DATABASE</c>: one consistent copy, also of a store in use by this connection.
    /// </summary>
    public void CopyTo(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string full = System.IO.Path.GetFullPath(path);
        if (File.Exists(full))
        {
            throw new IOException($"{full} exists already: a store copy goes into a new file.");
        }

        string database;
        using (DuckDBCommand name = Command("SELECT current_database()"))
        {
            database = (string)name.ExecuteScalar()!;
        }

        Execute($"ATTACH '{full.Replace("'", "''", StringComparison.Ordinal)}' AS {CopyAlias}");
        try
        {
            Execute($"COPY FROM DATABASE {Quote(database)} TO {CopyAlias}");
        }
        finally
        {
            Execute($"DETACH {CopyAlias}");
        }
    }

    /// <summary>Every table's row count, by table name.</summary>
    public IReadOnlyDictionary<string, long> RowCounts() => RowCounts(_db);

    /// <summary>The row counts of the store file <paramref name="path"/>, opened read-only (a backup's copy is never changed by its check).</summary>
    public static IReadOnlyDictionary<string, long> RowCountsOf(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var db = new DuckDBConnection($"Data Source={System.IO.Path.GetFullPath(path)};ACCESS_MODE=READ_ONLY");
        db.Open();
        return RowCounts(db);
    }

    private static SortedDictionary<string, long> RowCounts(DuckDBConnection db)
    {
        var tables = new List<string>();
        using (DuckDBCommand list = db.CreateCommand())
        {
            list.CommandText = "SELECT table_name FROM duckdb_tables() WHERE database_name = current_database() AND schema_name = 'main' ORDER BY table_name";
            using DbDataReader r = list.ExecuteReader();
            while (r.Read())
            {
                tables.Add(r.GetString(0));
            }
        }

        var counts = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (string table in tables)
        {
            using DuckDBCommand count = db.CreateCommand();
            count.CommandText = $"SELECT count(*) FROM {Quote(table)}";
            counts[table] = Convert.ToInt64(count.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }

        return counts;
    }

    private void Execute(string sql)
    {
        using DuckDBCommand cmd = Command(sql);
        cmd.ExecuteNonQuery();
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
