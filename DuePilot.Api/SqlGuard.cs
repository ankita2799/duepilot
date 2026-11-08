using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

public static class SqlGuard
{
	static readonly string[] BannedWords =
	{
		"insert","update","delete","drop","alter","create","attach","detach","pragma",
		"replace","vacuum","reindex","analyze","load_extension","begin","commit",
		"rollback","savepoint","with"
	};

	// Other tables the AI must never touch, even through tricks
	static readonly string[] BlockedNames =
	{
		"clients","invoices","payments","approvals","audit_log",
		"sqlite_master","sqlite_schema","sqlite_temp_master"
	};

	public static (bool Ok, string? Error, string? Sql) Validate(string? sql)
	{
		if (string.IsNullOrWhiteSpace(sql)) return (false, "Empty query.", null);
		var s = sql.Trim();
		if (s.EndsWith(';')) s = s[..^1].TrimEnd();

		// Blank out string literals so values like 'Update Co' don't trigger checks
		var scan = Regex.Replace(s, @"'([^']|'')*'", "''");

		if (scan.Contains(';')) return (false, "Only one statement is allowed.", null);
		if (scan.Contains("--") || scan.Contains("/*")) return (false, "Comments are not allowed.", null);
		if (!Regex.IsMatch(scan, @"^\s*select\b", RegexOptions.IgnoreCase))
			return (false, "Only SELECT is allowed.", null);

		foreach (var w in BannedWords)
			if (Regex.IsMatch(scan, $@"\b{w}\b", RegexOptions.IgnoreCase))
				return (false, $"Keyword not allowed: {w}", null);

		foreach (var n in BlockedNames)
			if (Regex.IsMatch(scan, $@"\b{n}\b", RegexOptions.IgnoreCase))
				return (false, $"Access not allowed: {n}", null);

		if (Regex.IsMatch(scan, @"\bsqlite_\w*", RegexOptions.IgnoreCase))
			return (false, "Access not allowed: system tables.", null);

		var sources = Regex.Matches(scan, @"\b(from|join)\s+([A-Za-z_][\w\.]*)\s*(\()?", RegexOptions.IgnoreCase);
		if (sources.Count == 0) return (false, "Query must read from vw_receivables.", null);
		foreach (Match m in sources)
		{
			var name = m.Groups[2].Value;
			if (!name.Equals("vw_receivables", StringComparison.OrdinalIgnoreCase) || m.Groups[3].Success)
				return (false, $"Table not allowed: {name}", null);
		}

		return (true, null, s);
	}
}

public static class SqlRunner
{
	public static List<Dictionary<string, object?>> Run(string safeSql)
	{
		using var conn = new SqliteConnection("Data Source=duepilot.db;Mode=ReadOnly");
		conn.Open();
		using var cmd = conn.CreateCommand();
		cmd.CommandText = $"SELECT * FROM ({safeSql}) LIMIT 100";
		cmd.CommandTimeout = 5;
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
}