using Microsoft.Data.Sqlite;

public static class Db
{
    public const string ConnectionString = "Data Source=duepilot.db";

    const string Schema = @"
CREATE TABLE IF NOT EXISTS clients (
    id INTEGER PRIMARY KEY, name TEXT NOT NULL, email TEXT NOT NULL);

CREATE TABLE IF NOT EXISTS invoices (
    id INTEGER PRIMARY KEY, client_id INTEGER NOT NULL REFERENCES clients(id),
    invoice_number TEXT NOT NULL, amount REAL NOT NULL, currency TEXT NOT NULL DEFAULT 'USD',
    issued_date TEXT NOT NULL, due_date TEXT NOT NULL,
    status TEXT NOT NULL CHECK (status IN ('Draft','Sent','Paid','Cancelled')),
    paypal_invoice_id TEXT NULL, last_reminder_date TEXT NULL);

CREATE TABLE IF NOT EXISTS payments (
    id INTEGER PRIMARY KEY, invoice_id INTEGER NOT NULL REFERENCES invoices(id),
    amount REAL NOT NULL, paid_date TEXT NOT NULL, paypal_transaction_id TEXT NULL);

CREATE TABLE IF NOT EXISTS approvals (
    id INTEGER PRIMARY KEY, action_type TEXT NOT NULL, invoice_id INTEGER NOT NULL,
    payload TEXT NULL, status TEXT NOT NULL DEFAULT 'Pending',
    requested_at TEXT NOT NULL, decided_by TEXT NULL, decided_at TEXT NULL);

CREATE TABLE IF NOT EXISTS audit_log (
    id INTEGER PRIMARY KEY, ts TEXT NOT NULL, actor TEXT NOT NULL,
    action TEXT NOT NULL, details TEXT NULL);

DROP VIEW IF EXISTS vw_receivables;
CREATE VIEW vw_receivables AS
SELECT i.id AS invoice_id, i.invoice_number, c.name AS client_name, c.email AS client_email,
       i.amount, i.currency, i.issued_date, i.due_date, i.status, i.last_reminder_date,
       CASE WHEN date(i.due_date) < date('now')
            THEN CAST(julianday(date('now')) - julianday(date(i.due_date)) AS INTEGER)
            ELSE 0 END AS days_overdue
FROM invoices i
JOIN clients c ON c.id = i.client_id
WHERE i.status IN ('Draft','Sent');";

    public static void Init()
    {
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        using (var cmd = conn.CreateCommand()) { cmd.CommandText = Schema; cmd.ExecuteNonQuery(); }
        using var check = conn.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM clients";
        if ((long)check.ExecuteScalar()! == 0) Seed(conn);
    }

    static void Run(SqliteConnection c, SqliteTransaction t, string sql, params (string, object)[] ps)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = t;
        cmd.CommandText = sql;
        foreach (var p in ps) cmd.Parameters.AddWithValue(p.Item1, p.Item2);
        cmd.ExecuteNonQuery();
    }

    static void Seed(SqliteConnection conn)
    {
        var names = new[] { "Acme Retail", "Northwind Traders", "Bluebird Studio", "Greenleaf Foods",
                            "Orbit Logistics", "Pixel & Co", "Sunrise Clinic", "Metro Print Works" };
        var rnd = new Random(42);
        using var tx = conn.BeginTransaction();

        for (int i = 0; i < names.Length; i++)
            Run(conn, tx, "INSERT INTO clients(name,email) VALUES($n,$e)",
                ("$n", names[i]), ("$e", $"billing{i + 1}@example.com"));

        for (int i = 1; i <= 40; i++)
        {
            var due = DateTime.Today.AddDays(rnd.Next(-45, 21));
            var status = rnd.Next(10) switch { 0 or 1 or 2 => "Paid", 3 => "Draft", _ => "Sent" };
            Run(conn, tx,
                @"INSERT INTO invoices(client_id,invoice_number,amount,issued_date,due_date,status)
                  VALUES($c,$n,$a,$i,$d,$s)",
                ("$c", rnd.Next(1, names.Length + 1)), ("$n", $"INV-{1000 + i}"),
                ("$a", rnd.Next(120, 4800)), ("$i", due.AddDays(-30).ToString("yyyy-MM-dd")),
                ("$d", due.ToString("yyyy-MM-dd")), ("$s", status));
        }
        tx.Commit();
    }
}