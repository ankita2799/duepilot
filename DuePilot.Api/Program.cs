using Microsoft.Data.Sqlite;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services.AddHttpClient<IPayPalGateway, RealPayPalGateway>();
builder.Services.AddHttpClient<ILlmClient, GeminiClient>(client =>
{
    client.Timeout = TimeSpan.FromMinutes(5);
});
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        policy.WithOrigins("http://localhost:5252")
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});
var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

//app.UseHttpsRedirection();
app.UseCors("AllowFrontend");

var summaries = new[]
{
    "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
};

app.MapGet("/weatherforecast", () =>
{
    var forecast =  Enumerable.Range(1, 5).Select(index =>
        new WeatherForecast
        (
            DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
            Random.Shared.Next(-20, 55),
            summaries[Random.Shared.Next(summaries.Length)]
        ))
        .ToArray();
    return forecast;
})
.WithName("GetWeatherForecast");

Db.Init();

app.MapGet("/receivables", () =>
{
    using var conn = new SqliteConnection(Db.ConnectionString);
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT * FROM vw_receivables ORDER BY days_overdue DESC LIMIT 20";
    using var r = cmd.ExecuteReader();
    var rows = new List<Dictionary<string, object?>>();
    while (r.Read())
    {
        var row = new Dictionary<string, object?>();
        for (int i = 0; i < r.FieldCount; i++) row[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
        rows.Add(row);
    }
    return rows;
});


app.MapPost("/ask", async (AskRequest req, ILlmClient llm) =>
{
    if (string.IsNullOrWhiteSpace(req.Question))
        return Results.BadRequest(new { error = "Ask a question." });
    string raw;
    var sw = System.Diagnostics.Stopwatch.StartNew();
    try { raw = await llm.CompleteAsync(SqlPrompt.System, req.Question); }
    catch (Exception ex)
    {
        var limited = ex.Message.Contains("429");
        return Results.Json(new
        {
            error = limited
            ? "The AI's free quota is used up for now. Please try again later."
            : "The AI service is busy. Please try again in a moment."
        },
            statusCode: limited ? 429 : 503);
    }
    Console.WriteLine($"LLM total: {sw.ElapsedMilliseconds} ms");

    var cleaned = raw.Replace("```sql", "").Replace("```", "").Trim();
    var (ok, error, sql) = SqlGuard.Validate(cleaned);
    if (!ok) return Results.BadRequest(new { error = "Blocked: " + error, generated = cleaned });
    try { return Results.Ok(new { question = req.Question, sql, rows = SqlRunner.Run(sql!) }); }
    catch (SqliteException ex) { return Results.BadRequest(new { error = ex.Message, generated = sql }); }
});

app.MapPost("/chase", async (AskRequest req, ILlmClient llm) =>
{
    if (string.IsNullOrWhiteSpace(req.Question))
        return Results.BadRequest(new { error = "Tell me who to chase." });

    string raw;
    try { raw = await llm.CompleteAsync(SqlPrompt.System + SqlPrompt.ChaseRule, req.Question); }
    catch (Exception ex)
    {
        var limited = ex.Message.Contains("429");
        return Results.Json(new
        {
            error = limited
            ? "The AI's free quota is used up for now. Please try again later."
            : "The AI service is busy. Please try again in a moment."
        },
            statusCode: limited ? 429 : 503);
    }

    var cleaned = raw.Replace("```sql", "").Replace("```", "").Trim();
    var (ok, error, sql) = SqlGuard.Validate(cleaned);
    if (!ok) return Results.BadRequest(new { error = "Blocked: " + error, generated = cleaned });

    List<Dictionary<string, object?>> rows;
    try { rows = SqlRunner.Run(sql!); }
    catch (SqliteException ex) { return Results.BadRequest(new { error = ex.Message, generated = sql }); }

    var result = Chaser.CreateProposals(rows);
    Store.Audit("ai", "chase_requested", JsonSerializer.Serialize(
        new { question = req.Question, sql, created = result.Created.Count, skipped = result.Skipped.Count }));
    return Results.Ok(new { question = req.Question, sql, created = result.Created, skipped = result.Skipped });
});

app.MapGet("/approvals", (string? status) =>
{
    using var c = Store.Open();
    using var cmd = c.CreateCommand();
    cmd.CommandText = @"SELECT a.id, a.action_type, a.status, a.requested_at, a.decided_by, a.decided_at, a.payload,
                               i.invoice_number, cl.name AS client_name, i.amount
                        FROM approvals a
                        JOIN invoices i ON i.id = a.invoice_id
                        JOIN clients cl ON cl.id = i.client_id
                        WHERE ($s IS NULL OR a.status = $s)
                        ORDER BY a.id DESC LIMIT 100";
    cmd.Parameters.AddWithValue("$s", (object?)status ?? DBNull.Value);
    return Store.Rows(cmd);
});

static bool IsManager(HttpRequest r) => r.Headers["X-Role"] == "manager";

app.MapPost("/approvals/{id:long}/approve", async (long id, DecisionRequest? body, IPayPalGateway pp, HttpRequest http) =>
{
    if (!IsManager(http)) return Results.Json(new { error = "Only a manager can approve." }, statusCode: 403);
    var (code, result) = await Decisions.ApproveAsync(id, "manager", pp);
    return Results.Json(result, statusCode: code);
});

app.MapPost("/approvals/{id:long}/reject", (long id, DecisionRequest? body, HttpRequest http) =>
{
    if (!IsManager(http)) return Results.Json(new { error = "Only a manager can reject." }, statusCode: 403);
    var (code, result) = Decisions.Reject(id, "manager", body?.Reason);
    return Results.Json(result, statusCode: code);
});

app.MapPost("/invoices/refresh-status", async (IPayPalGateway pp) =>
    Results.Ok(await Decisions.RefreshStatusAsync(pp)));

app.MapGet("/audit", (int? limit) =>
{
    using var c = Store.Open();
    using var cmd = c.CreateCommand();
    cmd.CommandText = "SELECT id, ts, actor, action, details FROM audit_log ORDER BY id DESC LIMIT $n";
    cmd.Parameters.AddWithValue("$n", Math.Clamp(limit ?? 50, 1, 200));
    return Store.Rows(cmd);
});

app.MapGet("/invoices", () =>
{
    using var c = Store.Open();
    using var cmd = c.CreateCommand();
    cmd.CommandText = @"SELECT i.id, i.invoice_number, c.name AS client_name, i.amount, i.currency,
                               i.due_date, i.status, i.last_reminder_date,
                               CASE WHEN i.status IN ('Draft','Sent') AND date(i.due_date) < date('now')
                                    THEN CAST(julianday(date('now')) - julianday(date(i.due_date)) AS INTEGER)
                                    ELSE 0 END AS days_overdue
                        FROM invoices i
                        JOIN clients c ON c.id = i.client_id
                        ORDER BY CASE WHEN i.status IN ('Draft','Sent') THEN 0 ELSE 1 END,
                                 days_overdue DESC, i.id
                        LIMIT 200";
    return Store.Rows(cmd);
});

app.Run();

public record SqlRequest(string? Sql);

public record AskRequest(string? Question);

public record DecisionRequest(string? DecidedBy, string? Reason);

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
