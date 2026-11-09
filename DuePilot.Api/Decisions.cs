using System.Text.Json;

public static class Decisions
{
    public static async Task<(int Code, object Body)> ApproveAsync(long id, string by, IPayPalGateway pp)
    {
        using var c = Store.Open();

        // 1. Claim the request so it can only be approved once
        int claimed;
        using (var claim = c.CreateCommand())
        {
            claim.CommandText = @"UPDATE approvals SET status='Approved', decided_by=$by, decided_at=$t
                                  WHERE id=$id AND status='Pending'";
            claim.Parameters.AddWithValue("$by", by);
            claim.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
            claim.Parameters.AddWithValue("$id", id);
            claimed = claim.ExecuteNonQuery();
        }
        if (claimed == 0)
            return (409, new { error = "This request is not pending (already decided, or it does not exist)." });
        Store.Audit(by, "approved", JsonSerializer.Serialize(new { approval_id = id }));

        // 2. Load the details
        Dictionary<string, object?> d;
        using (var q = c.CreateCommand())
        {
            q.CommandText = @"SELECT a.action_type, a.invoice_id, i.invoice_number, i.amount, i.currency, i.due_date,
                                     i.paypal_invoice_id, cl.name AS client_name, cl.email AS client_email
                              FROM approvals a
                              JOIN invoices i ON i.id = a.invoice_id
                              JOIN clients cl ON cl.id = i.client_id
                              WHERE a.id = $id";
            q.Parameters.AddWithValue("$id", id);
            d = Store.Rows(q).First();
        }

        var action = (string)d["action_type"]!;
        var invoiceId = Convert.ToInt64(d["invoice_id"]);
        var number = (string)d["invoice_number"]!;
        var existing = d["paypal_invoice_id"] as string;

        // 3. Do the real PayPal action
        try
        {
            string ppId;
            string? payUrl = null;
            string what;

            if (action == "SendReminder" && !string.IsNullOrEmpty(existing))
            {
                await pp.SendReminderAsync(existing);
                ppId = existing;
                what = "reminder_sent";
            }
            else
            {
                // Sample invoices were never in PayPal, so issue the real invoice now
                var res = await pp.CreateAndSendInvoiceAsync(new InvoiceRequest(
                    (string)d["client_name"]!, (string)d["client_email"]!,
                    Convert.ToDecimal(d["amount"]), (string)d["currency"]!,
                    number, DateTime.Parse((string)d["due_date"]!)));
                ppId = res.PayPalInvoiceId;
                payUrl = res.PayUrl;
                what = action == "SendInvoice" ? "invoice_sent" : "invoice_issued_as_reminder";
            }

            using var up = c.CreateCommand();
            up.CommandText = @"UPDATE invoices
                               SET status = 'Sent', paypal_invoice_id = $p,
                                   last_reminder_date = CASE WHEN $r = 1 THEN $d ELSE last_reminder_date END
                               WHERE id = $i";
            up.Parameters.AddWithValue("$p", ppId);
            up.Parameters.AddWithValue("$r", action == "SendReminder" ? 1 : 0);
            up.Parameters.AddWithValue("$d", DateTime.Today.ToString("yyyy-MM-dd"));
            up.Parameters.AddWithValue("$i", invoiceId);
            up.ExecuteNonQuery();

            Store.Audit(by, what, JsonSerializer.Serialize(
                new { approval_id = id, invoice = number, paypal_invoice_id = ppId }));
            return (200, new { status = "Done", action = what, invoice = number, paypal_invoice_id = ppId, pay_url = payUrl });
        }
        catch (Exception ex)
        {
            using var f = c.CreateCommand();
            f.CommandText = "UPDATE approvals SET status='Failed' WHERE id=$id";
            f.Parameters.AddWithValue("$id", id);
            f.ExecuteNonQuery();
            Store.Audit("system", "action_failed", JsonSerializer.Serialize(new { approval_id = id, error = ex.Message }));
            return (502, new { error = "PayPal could not complete this action.", detail = ex.Message });
        }
    }

    public static (int Code, object Body) Reject(long id, string by, string? reason)
    {
        using var c = Store.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"UPDATE approvals SET status='Rejected', decided_by=$by, decided_at=$t
                            WHERE id=$id AND status='Pending'";
        cmd.Parameters.AddWithValue("$by", by);
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$id", id);
        if (cmd.ExecuteNonQuery() == 0)
            return (409, new { error = "This request is not pending (already decided, or it does not exist)." });
        Store.Audit(by, "rejected", JsonSerializer.Serialize(new { approval_id = id, reason }));
        return (200, new { status = "Rejected" });
    }

    public static async Task<object> RefreshStatusAsync(IPayPalGateway pp)
    {
        List<Dictionary<string, object?>> open;
        using (var c0 = Store.Open())
        using (var q = c0.CreateCommand())
        {
            q.CommandText = @"SELECT id, invoice_number, amount, paypal_invoice_id
                              FROM invoices WHERE status = 'Sent' AND paypal_invoice_id IS NOT NULL";
            open = Store.Rows(q);
        }

        int paid = 0, cancelled = 0, errors = 0;
        foreach (var r in open)
        {
            try
            {
                var s = await pp.GetStatusAsync((string)r["paypal_invoice_id"]!);
                if (s != "PAID" && s != "CANCELLED") continue;

                var invId = Convert.ToInt64(r["id"]);
                using var c = Store.Open();
                using var up = c.CreateCommand();
                up.CommandText = "UPDATE invoices SET status=$s WHERE id=$i";
                up.Parameters.AddWithValue("$s", s == "PAID" ? "Paid" : "Cancelled");
                up.Parameters.AddWithValue("$i", invId);
                up.ExecuteNonQuery();

                if (s == "PAID")
                {
                    using var pay = c.CreateCommand();
                    pay.CommandText = @"INSERT INTO payments(invoice_id, amount, paid_date)
                                        SELECT $i, $a, $d WHERE NOT EXISTS (SELECT 1 FROM payments WHERE invoice_id = $i)";
                    pay.Parameters.AddWithValue("$i", invId);
                    pay.Parameters.AddWithValue("$a", Convert.ToDouble(r["amount"]));
                    pay.Parameters.AddWithValue("$d", DateTime.Today.ToString("yyyy-MM-dd"));
                    pay.ExecuteNonQuery();
                    paid++;
                }
                else cancelled++;

                Store.Audit("system", s == "PAID" ? "payment_received" : "invoice_cancelled",
                    JsonSerializer.Serialize(new { invoice = r["invoice_number"] }));
            }
            catch { errors++; }
        }
        return new { inspected = open.Count, paid, cancelled, errors };
    }
}