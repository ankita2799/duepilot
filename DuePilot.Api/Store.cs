using Microsoft.Data.Sqlite;

public static class Store
{
    public static SqliteConnection Open()
    {
        var c = new SqliteConnection(Db.ConnectionString);
        c.Open();
        return c;
    }

    public static List<Dictionary<string, object?>> Rows(SqliteCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        var rows = new List<Dictionary<string, object?>>();
        while (r.Read())
        {
            var row = new Dictionary<string, object?>();
            for (int i = 0; i < r.FieldCount; i++) row[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    public static void Audit(string actor, string action, string? details = null)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO audit_log(ts,actor,action,details) VALUES($t,$a,$x,$d)";
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$a", actor);
        cmd.Parameters.AddWithValue("$x", action);
        cmd.Parameters.AddWithValue("$d", (object?)details ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
}