using System.Text.Json;

public record ChaseResult(List<object> Created, List<object> Skipped);

public static class Chaser
{
    static readonly string[] Needed =
        { "invoice_id", "invoice_number", "client_name", "amount", "days_overdue", "status" };

    public static string ToneNote(string client, string number, decimal amount, int days)
    {
        if (days <= 0)
            return $"Hi {client}, invoice {number} for ${amount:F2} is ready for payment. Thank you!";
        if (days <= 15)
            return $"Hi {client}, a friendly reminder that invoice {number} (${amount:F2}) is {days} days past due. Could you please arrange payment? Thank you!";
        if (days <= 30)
            return $"Hello {client}, invoice {number} (${amount:F2}) is now {days} days overdue. Please pay as soon as possible, or let us know if there is an issue.";
        return $"Hello {client}, invoice {number} (${amount:F2}) is {days} days overdue and needs your urgent attention. Please pay today or contact us to discuss.";
    }

    public static ChaseResult CreateProposals(List<Dictionary<string, object?>> rows)
    {
        var created = new List<object>();
        var skipped = new List<object>();
        using var c = Store.Open();

        foreach (var r in rows.Take(20))   // safety cap per request
        {
            if (Needed.Any(k => !r.ContainsKey(k) || r[k] == null))
            {
                skipped.Add(new { reason = "Result did not include full invoice details" });
                continue;
            }

            var id = Convert.ToInt64(r["invoice_id"]);
            var number = Convert.ToString(r["invoice_number"]) ?? "";
            var client = Convert.ToString(r["client_name"]) ?? "";
            var amount = Convert.ToDecimal(r["amount"]);
            var days = Convert.ToInt32(r["days_overdue"]);
            var status = Convert.ToString(r["status"]);
            r.TryGetValue("last_reminder_date", out var lastObj);

            string action;
            if (status == "Draft") action = "SendInvoice";
            else if (status == "Sent" && days > 0) action = "SendReminder";
            else { skipped.Add(new { invoice = number, reason = "Not overdue yet" }); continue; }

            if (action == "SendReminder" && lastObj is string ls && DateTime.TryParse(ls, out var lr)
                && (DateTime.Today - lr.Date).TotalDays < 2)
            { skipped.Add(new { invoice = number, reason = "Reminded in the last 2 days" }); continue; }

            using (var chk = c.CreateCommand())
            {
                chk.CommandText = "SELECT COUNT(*) FROM approvals WHERE invoice_id=$i AND action_type=$a AND status='Pending'";
                chk.Parameters.AddWithValue("$i", id);
                chk.Parameters.AddWithValue("$a", action);
                if ((long)chk.ExecuteScalar()! > 0)
                { skipped.Add(new { invoice = number, reason = "Already waiting for approval" }); continue; }
            }

            var note = action == "SendInvoice"
    ? $"Hi {client}, please find invoice {number} for ${amount:F2}. Thank you for your business!"
    : ToneNote(client, number, amount, days);
            var payload = JsonSerializer.Serialize(new { note, client, amount, days_overdue = days, invoice_number = number });

            long newId;
            using (var ins = c.CreateCommand())
            {
                ins.CommandText = @"INSERT INTO approvals(action_type,invoice_id,payload,status,requested_at)
                                    VALUES($a,$i,$p,'Pending',$t); SELECT last_insert_rowid();";
                ins.Parameters.AddWithValue("$a", action);
                ins.Parameters.AddWithValue("$i", id);
                ins.Parameters.AddWithValue("$p", payload);
                ins.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
                newId = (long)ins.ExecuteScalar()!;
            }

            Store.Audit("ai", "proposal_created",
                JsonSerializer.Serialize(new { approval_id = newId, action, invoice = number }));
            created.Add(new { approval_id = newId, action, invoice = number, client, amount, days_overdue = days, note });
        }
        return new ChaseResult(created, skipped);
    }
}