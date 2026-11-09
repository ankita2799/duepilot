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
var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

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
app.MapPost("/sql/run", (SqlRequest req) =>
{
    var (ok, error, sql) = SqlGuard.Validate(req.Sql);
    if (!ok) return Results.BadRequest(new { error });
    try { return Results.Ok(new { sql, rows = SqlRunner.Run(sql!) }); }
    catch (SqliteException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPost("/ask", async (AskRequest req, ILlmClient llm) =>
{
    if (string.IsNullOrWhiteSpace(req.Question))
        return Results.BadRequest(new { error = "Ask a question." });
    string raw;
    try { raw = await llm.CompleteAsync(SqlPrompt.System, req.Question); }
    catch (Exception ex) { return Results.Problem(ex.Message); }

    var cleaned = raw.Replace("```sql", "").Replace("```", "").Trim();
    var (ok, error, sql) = SqlGuard.Validate(cleaned);
    if (!ok) return Results.BadRequest(new { error = "Blocked: " + error, generated = cleaned });
    try { return Results.Ok(new { question = req.Question, sql, rows = SqlRunner.Run(sql!) }); }
    catch (SqliteException ex) { return Results.BadRequest(new { error = ex.Message, generated = sql }); }
});
//------Temporary-----
app.MapPost("/dev/test-invoice", async (IPayPalGateway pp) =>
    await pp.CreateAndSendInvoiceAsync(
        new InvoiceRequest("Test Client", "test@example.com", 25m, "USD", "DEV-1", DateTime.Today.AddDays(7))));
app.MapGet("/dev/status/{id}", async (IPayPalGateway pp, string id) => new { status = await pp.GetStatusAsync(id) });
app.MapPost("/dev/remind/{id}", async (IPayPalGateway pp, string id) => { await pp.SendReminderAsync(id); return Results.Ok(); });
//---------------------

app.MapPost("/chase", async (AskRequest req, ILlmClient llm) =>
{
    if (string.IsNullOrWhiteSpace(req.Question))
        return Results.BadRequest(new { error = "Tell me who to chase." });

    string raw;
    try { raw = await llm.CompleteAsync(SqlPrompt.System + SqlPrompt.ChaseRule, req.Question); }
    catch (Exception) { return Results.Json(new { error = "The AI service is busy. Please try again in a moment." }, statusCode: 503); }

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

app.MapPost("/approvals/{id:long}/approve", async (long id, DecisionRequest? body, IPayPalGateway pp) =>
{
    var (code, result) = await Decisions.ApproveAsync(id, body?.DecidedBy ?? "manager", pp);
    return Results.Json(result, statusCode: code);
});

app.MapPost("/approvals/{id:long}/reject", (long id, DecisionRequest? body) =>
{
    var (code, result) = Decisions.Reject(id, body?.DecidedBy ?? "manager", body?.Reason);
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

app.Run();

public record SqlRequest(string? Sql);

public record AskRequest(string? Question);

public record DecisionRequest(string? DecidedBy, string? Reason);

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
