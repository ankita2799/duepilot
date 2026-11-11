## 🎥 Project Demo

**Watch DuePilot in action:** [Watch the demo on YouTube](https://youtu.be/fZiwupzm110)

**GitHub Repository:** [GitHub Repository](https://github.com/ankita2799/duepilot)

DuePilot demonstrates how AI and PayPal can work together to simplify invoice management while keeping financial actions under human control.

### Key Highlights

- Natural-language invoice analytics powered by Google Gemini.
- AI-generated and validated SQL queries.
- Intelligent overdue invoice follow-up proposals.
- Human approval before PayPal operations.
- PayPal invoice creation, sending and payment-status tracking.
- Complete activity and audit history.

**Built for the PayPal AI Hackathon 2026.**

## The problem

Small businesses and freelancers spend hours working out who owes them money and writing reminders by hand. Late payments hurt cash flow, and chasing them is tedious.

## What DuePilot does

- **Ask in plain English.** "Who owes me the most money?" The AI writes a SQL query, a safety check validates it, and the answer appears in a table. A "How the AI found this" panel shows the exact SQL.
- **Chase.** "Chase the 3 most overdue invoices" turns invoices into *proposals*: send a reminder, or send a draft invoice. Each comes with a message whose tone matches how late the invoice is. Nothing is sent yet.
- **Approve.** A manager approves or rejects each proposal. Approving creates and sends a PayPal invoice (sandbox), or sends a reminder when the invoice already exists in PayPal.
- **Get paid.** The client pays on PayPal's invoice page. "Refresh status" marks the invoice Paid and records the payment.
- **See everything.** An activity timeline shows every proposal, decision and payment.

## How PayPal and AI are used

- **PayPal:** the Invoicing REST API in the sandbox. We get an OAuth token, create an invoice, send it, send reminders, and read its status and pay link.
- **AI:** Google Gemini turns a question into SQL. It receives only our instructions and the question. It never sees invoice data.

## Safety by design

- The AI can only *propose*. Only an approved action calls PayPal, and each proposal can be approved once.
- AI-written SQL is checked before it runs: one SELECT only, no comments, only the `vw_receivables` view, every other table and system table blocked. It runs on a read-only connection with a row limit.
- Approve and reject need the manager role (a demo-level check, see Limits).
- Every step is saved in an audit log.

## Built with

ASP.NET Core (C#, minimal API), SQLite, Google Gemini API, PayPal Invoicing REST API (sandbox), Bootstrap 5 and plain JavaScript.
Development was assisted by Claude, an AI chat assistant.

## Run it locally

**You need:** the .NET SDK that matches `TargetFramework` in `DuePilot.Api/DuePilot.Api.csproj` (.NET 10), a Gemini API key from Google AI Studio, and a PayPal sandbox app (setup below).

1. Clone the repo:
   ```
   git clone https://github.com/ankita2799/duepilot.git
   cd duepilot
   ```
2. Save your settings (kept outside the repo, so no secrets are committed):
   ```
   dotnet user-secrets set "PayPal:ClientId" "YOUR_SANDBOX_CLIENT_ID" --project DuePilot.Api
   dotnet user-secrets set "PayPal:ClientSecret" "YOUR_SANDBOX_SECRET" --project DuePilot.Api
   dotnet user-secrets set "PayPal:BusinessEmail" "YOUR_SANDBOX_BUSINESS_EMAIL" --project DuePilot.Api
   dotnet user-secrets set "PayPal:RecipientOverride" "YOUR_SANDBOX_PERSONAL_EMAIL" --project DuePilot.Api
   dotnet user-secrets set "Llm:ApiKey" "YOUR_GEMINI_KEY" --project DuePilot.Api
   dotnet user-secrets set "Llm:Model" "YOUR_GEMINI_MODEL_NAME" --project DuePilot.Api
   ```
      We used `gemini-3.1-flash-lite`. Copy the exact model ID from Google AI Studio.

   Environment variables also work, with double underscores, for example `PayPal__ClientId`.
3. Start it:
   ```
   dotnet run --project DuePilot.Api
   ```
4. Open `http://localhost:5252` (the exact address is printed in the console).

The database file `duepilot.db` is created on first run and filled with 40 **fake** invoices. To reset, stop the app and delete that file.

### PayPal sandbox setup

1. Sign in at developer.paypal.com.
2. Open **Testing Tools, then Sandbox Accounts**. Create one **Business** and one **Personal** account, both in the **United States**.
3. Open **Apps & Credentials** (Sandbox mode) and create an app linked to the business account. Copy its Client ID and Secret.
4. Use the business account's email as `PayPal:BusinessEmail` and the personal account's email as `PayPal:RecipientOverride`. Every demo invoice goes to that buyer, because the sample clients' emails are fake.
5. To pay an invoice, open its pay link in a private window and log in with the sandbox personal account (its password is under **View/Edit account** in Sandbox Accounts).

## Try it (about 2 minutes)

1. In **Ask** mode, click "Who owes me the most money?". Open **How the AI found this** to see the SQL.
2. Type "Show me the clients table". The AI can only see the receivables view, so it answers from that view and never touches the real clients table. If it ever wrote SQL for another table, the safety check would block it.
3. Switch to **Chase** and run "Chase the 3 most overdue invoices". Proposals appear under **Waiting for approval**. Nothing has been sent.
4. Switch **Viewing as** to **Manager** (top right) and click **Approve** on one proposal. A PayPal invoice is created and sent, and a pay link appears.
5. Open the pay link in a private window and pay as the sandbox buyer.
6. Click **Refresh status from PayPal**. The invoice turns **Paid**, and the activity timeline tells the whole story.

## API (used by the web page)

| Endpoint | Purpose |
|---|---|
| `POST /ask` | Question in, SQL and rows out |
| `POST /chase` | Question in, proposals out |
| `GET /approvals?status=Pending` | Proposals waiting for a decision |
| `POST /approvals/{id}/approve` and `/reject` | Manager decision (needs the manager role) |
| `GET /invoices` | All invoices |
| `POST /invoices/refresh-status` | Ask PayPal which invoices are paid |
| `GET /audit` | Activity log |

## Limits (honest)

- **The role switch is a demo.** The manager role is a request header, so anyone can set it. Real use would need real sign-in.
- **Sample data is fake,** and all invoices go to one sandbox buyer. Currency is USD only.
- **Payment status needs the Refresh button.** There are no webhooks yet.
- **The AI depends on a Gemini key.** Free-tier quotas are small, so the AI may say it is busy or out of quota. Use a key with billing, or try again later.
- **Not production-ready.** This is a hackathon project that runs against the PayPal sandbox only.

## Project layout

| File | What it does |
|---|---|
| `DuePilot.Api/Program.cs` | Web addresses (endpoints) and setup |
| `DuePilot.Api/Db.cs` | Tables, view and fake data |
| `DuePilot.Api/SqlGuard.cs` | SQL safety check and read-only runner |
| `DuePilot.Api/Llm.cs` | Gemini client and our instructions to it |
| `DuePilot.Api/Chaser.cs` | Turns results into proposals |
| `DuePilot.Api/Decisions.cs` | Approve, reject and refresh status |
| `DuePilot.Api/PayPalGateway.cs` | PayPal sandbox calls |
| `DuePilot.Api/Store.cs` | Database helpers and the audit log |
| `DuePilot.Api/wwwroot/` | The web page |

## Licence

MIT. See `LICENSE`.
