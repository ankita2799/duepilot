using Microsoft.Data.Sqlite;
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
app.Run();

public record SqlRequest(string? Sql);

public record AskRequest(string? Question);

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
