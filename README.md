# Status Monitor

Contextual uptime monitoring, rebuilt as a multi-tenant SaaS backend on Azure
Functions (.NET 8, isolated worker). Companies sign up (Clerk/WorkOS), upload
URLs from a spreadsheet or paste them into the `status-web` SPA, and get email
alerts when sites stop responding — including pages that return 200 but
actually serve an error or maintenance page.

The original single-tenant C# script (`.csx`) functions are preserved in
[`legacy/`](legacy/README.md), with a mapping to their replacements.

## Repository layout

```
src/
  StatusMonitor.Core/        Domain logic: plans & limits, tenancy, table/queue
                             storage, CSV/paste parsing, URL polling. No Azure
                             Functions dependency; fully unit tested.
  StatusMonitor.Functions/   .NET 8 isolated-worker Azure Functions app:
                             scheduler, poller, persisters, alerter, HTTP API,
                             OIDC tenant-auth middleware.
tests/
  StatusMonitor.Core.Tests/  xUnit tests for the core logic.
docs/
  SAAS_PLAN.md               Product & architecture plan for the SaaS offering.
legacy/                      Original .csx functions (frozen, not deployed).
```

## Architecture

```
PollScheduler (timer, 1 min)
  └─ enqueues a job per tenant whose plan interval elapsed
       └─ poll-jobs-queue ─► UrlPoller ─┬─► status-states-queue  ─► StatusStatePersister  ─► UrlStatuses
                                        ├─► status-history-queue ─► StatusHistoryPersister ─► UrlStatusHistory
                                        └─► status-notifications-queue ─► EmailAlerter ─► SendGrid

status-web SPA ──(Bearer JWT: Clerk/WorkOS)──► HTTP API
  GET    /api/me                       tenant, plan, usage (auto-provisions on first call)
  GET    /api/urls                     list monitored URLs
  POST   /api/urls                     bulk import (JSON array, CSV upload, or pasted list)
  DELETE /api/urls/{urlKey}            remove a URL
  GET    /api/status                   current status of every URL
  GET    /api/status/{urlKey}/history  recent checks, newest first
```

Every table is partitioned by tenant id, and the tenant id is resolved
exclusively from the validated JWT — see
[docs/SAAS_PLAN.md](docs/SAAS_PLAN.md) for the full design, pricing tiers,
payments (Stripe) plan, and cost model.

## Local development

Prerequisites: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0),
[Azure Functions Core Tools v4](https://learn.microsoft.com/azure/azure-functions/functions-run-local),
and [Azurite](https://learn.microsoft.com/azure/storage/common/storage-use-azurite)
for local storage emulation.

```bash
dotnet build StatusMonitor.sln
dotnet test  StatusMonitor.sln

cd src/StatusMonitor.Functions
cp local.settings.example.json local.settings.json   # then edit as needed
func start
```

With `Auth__Mode` set to `Development` (the default in the example settings),
requests authenticate with headers instead of a JWT:

```bash
curl -H "x-tenant-id: demo-tenant" http://localhost:7071/api/me

curl -X POST -H "x-tenant-id: demo-tenant" -H "Content-Type: text/csv" \
     --data-binary $'name,url\nExample,https://example.com' \
     http://localhost:7071/api/urls

curl -H "x-tenant-id: demo-tenant" http://localhost:7071/api/status
```

For production, set `Auth__Mode=Jwt` and point `Auth__Authority` at your Clerk
instance (or WorkOS AuthKit issuer); the middleware validates tokens against
the issuer's JWKS and resolves the tenant from the `org_id` claim.

## Configuration

| Setting | Purpose |
| --- | --- |
| `AzureWebJobsStorage` | Storage account for tables and queues. |
| `Auth__Mode` | `Jwt` (production) or `Development` (header-based). |
| `Auth__Authority` | OIDC issuer, e.g. `https://your-app.clerk.accounts.dev`. |
| `Auth__Audience` | Optional expected `aud` claim; empty skips the check. |
| `Auth__TenantClaim` | Claim carrying the organization id (default `org_id`). |
| `SENDGRID_API_KEY` | SendGrid API key for alert emails. |
| `ALERT_FROM_EMAIL` | From address for alerts. |
| `EMAIL_RECIPIENTS` | Fallback recipients when a tenant has none configured. |
