using System.Text;
using System.Text.Json;

public interface ILlmClient { Task<string> CompleteAsync(string system, string user); }

public class GeminiClient(HttpClient http, IConfiguration cfg) : ILlmClient
{
    public async Task<string> CompleteAsync(string system, string user)
    {
        var models = new[] { cfg["Llm:Model"], cfg["Llm:FallbackModel"] }
            .Where(m => !string.IsNullOrWhiteSpace(m)).ToArray();

        var body = JsonSerializer.Serialize(new
        {
            systemInstruction = new { parts = new[] { new { text = system } } },
            contents = new[] { new { role = "user", parts = new[] { new { text = user } } } },
            generationConfig = new { temperature = 0 }
        });

        Exception? last = null;
        foreach (var model in models)
        {
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                var req = new HttpRequestMessage(HttpMethod.Post,
                    $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent")
                { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                req.Headers.Add("x-goog-api-key", cfg["Llm:ApiKey"]);

                var res = await http.SendAsync(req);
                var text = await res.Content.ReadAsStringAsync();
                if (res.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(text);
                    return doc.RootElement.GetProperty("candidates")[0].GetProperty("content")
                              .GetProperty("parts")[0].GetProperty("text").GetString() ?? "";
                }

                var code = (int)res.StatusCode;
                last = new Exception($"LLM error {code}: {text}");
                if (code != 503 && code != 429 && code != 500) throw last; // a real error, don't retry
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2));
            }
        }
        throw last!;
    }
}

public static class SqlPrompt
{
    public const string System = @"You translate questions into one SQLite SELECT query.
Rules:
- Output ONLY the SQL. No explanation, no markdown, no comments, no semicolon.
- Query only the view vw_receivables. Never use any other table.
- Columns: invoice_id, invoice_number, client_name, client_email, amount, currency, issued_date, due_date, status ('Draft' or 'Sent'), last_reminder_date, days_overdue (0 if not yet due).
- 'overdue' means days_overdue > 0. 'Draft' means not yet sent to the client.
- Today's date in SQLite is date('now'). Dates are text in yyyy-MM-dd.
- Use LIMIT for 'top N' questions.
Examples:
Q: who owes me the most? -> SELECT client_name, SUM(amount) AS total FROM vw_receivables GROUP BY client_name ORDER BY total DESC
Q: invoices more than 30 days late -> SELECT * FROM vw_receivables WHERE days_overdue > 30 ORDER BY days_overdue DESC
Q: unsent invoices -> SELECT * FROM vw_receivables WHERE status = 'Draft'";

    public const string ChaseRule = @"
This query selects invoices to chase. You MUST use SELECT * FROM vw_receivables so every column is returned.
Rank by days_overdue DESC unless told otherwise. Use LIMIT for 'top N'.
If the user names clients or asks about clients, return all of those clients' invoices, for example with WHERE client_name IN (SELECT client_name FROM vw_receivables GROUP BY client_name ORDER BY SUM(amount) DESC LIMIT 3).";
}