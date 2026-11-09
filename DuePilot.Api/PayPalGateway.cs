using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

public record InvoiceRequest(string ClientName, string ClientEmail, decimal Amount,
                             string Currency, string Number, DateTime DueDate);
public record InvoiceResult(string PayPalInvoiceId, string Status, string? PayUrl);

public interface IPayPalGateway
{
    Task<InvoiceResult> CreateAndSendInvoiceAsync(InvoiceRequest req);
    Task SendReminderAsync(string paypalInvoiceId, string? note = null);
    Task<string> GetStatusAsync(string paypalInvoiceId);
}

public class RealPayPalGateway(HttpClient http, IConfiguration cfg) : IPayPalGateway
{
    const string Base = "https://api-m.sandbox.paypal.com";
    static string? _token;
    static DateTime _tokenExpiry;

    async Task<string> TokenAsync()
    {
        if (_token != null && DateTime.UtcNow < _tokenExpiry) return _token;
        var creds = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"{cfg["PayPal:ClientId"]}:{cfg["PayPal:ClientSecret"]}"));
        var req = new HttpRequestMessage(HttpMethod.Post, Base + "/v1/oauth2/token")
        {
            Content = new FormUrlEncodedContent(
                new Dictionary<string, string> { ["grant_type"] = "client_credentials" })
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", creds);
        var res = await http.SendAsync(req);
        if (!res.IsSuccessStatusCode) throw new Exception($"PayPal sign-in failed ({(int)res.StatusCode})");
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        _token = doc.RootElement.GetProperty("access_token").GetString()!;
        _tokenExpiry = DateTime.UtcNow.AddSeconds(doc.RootElement.GetProperty("expires_in").GetInt32() - 60);
        return _token;
    }

    async Task<string> CallAsync(HttpMethod method, string path, object? body = null)
    {
        var req = new HttpRequestMessage(method, Base + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync());
        if (body != null)
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var res = await http.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new Exception($"PayPal error {(int)res.StatusCode}: {text}");
        return text;
    }

    async Task<(string Status, string? PayUrl)> FetchAsync(string id)
    {
        var text = await CallAsync(HttpMethod.Get, $"/v2/invoicing/invoices/{id}");
        using var doc = JsonDocument.Parse(text);
        var status = doc.RootElement.GetProperty("status").GetString() ?? "UNKNOWN";
        string? url = null;
        if (doc.RootElement.TryGetProperty("detail", out var d) &&
            d.TryGetProperty("metadata", out var m) &&
            m.TryGetProperty("recipient_view_url", out var u))
            url = u.GetString();
        return (status == "MARKED_AS_PAID" ? "PAID" : status, url);
    }

    public async Task<InvoiceResult> CreateAndSendInvoiceAsync(InvoiceRequest r)
    {
        var recipient = cfg["PayPal:RecipientOverride"] is { Length: > 0 } o ? o : r.ClientEmail;
        var due = r.DueDate.Date < DateTime.Today ? DateTime.Today : r.DueDate.Date; // PayPal may reject past due dates

        var created = await CallAsync(HttpMethod.Post, "/v2/invoicing/invoices", new
        {
            detail = new
            {
                invoice_number = $"{r.Number}-{DateTime.UtcNow:MMddHHmmss}",
                currency_code = r.Currency,
                payment_term = new { term_type = "DUE_ON_DATE_SPECIFIED", due_date = due.ToString("yyyy-MM-dd") }
            },
            invoicer = new { email_address = cfg["PayPal:BusinessEmail"] },
            primary_recipients = new[]
            {
                new { billing_info = new { business_name = r.ClientName, email_address = recipient } }
            },
            items = new[]
            {
                new { name = $"Invoice {r.Number}", quantity = "1",
                      unit_amount = new { currency_code = r.Currency, value = r.Amount.ToString("F2") } }
            }
        });

        using var doc = JsonDocument.Parse(created);
        var id = doc.RootElement.TryGetProperty("id", out var idEl)
            ? idEl.GetString()!
            : doc.RootElement.GetProperty("href").GetString()!.Split('/').Last();

        await CallAsync(HttpMethod.Post, $"/v2/invoicing/invoices/{id}/send", new { send_to_recipient = true });
        var (status, payUrl) = await FetchAsync(id);
        return new InvoiceResult(id, status, payUrl);
    }

    public Task SendReminderAsync(string id, string? note = null) =>
     CallAsync(HttpMethod.Post, $"/v2/invoicing/invoices/{id}/remind", new
     {
         subject = "Payment reminder",
         note = string.IsNullOrWhiteSpace(note) ? "A friendly reminder that this invoice is awaiting payment." : note,
         send_to_invoicer = false
     });

    public async Task<string> GetStatusAsync(string id) => (await FetchAsync(id)).Status;
}

// Practice stand-in, kept only for offline testing
public class FakePayPalGateway : IPayPalGateway
{
    static readonly Dictionary<string, string> Statuses = new();
    public Task<InvoiceResult> CreateAndSendInvoiceAsync(InvoiceRequest req)
    {
        var id = "FAKE-" + Guid.NewGuid().ToString("N")[..8];
        Statuses[id] = "SENT";
        return Task.FromResult(new InvoiceResult(id, "SENT", null));
    }
    public Task SendReminderAsync(string id, string? note = null) => Task.CompletedTask;
    public Task<string> GetStatusAsync(string id) => Task.FromResult(Statuses.GetValueOrDefault(id, "UNKNOWN"));
}