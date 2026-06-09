# Status Monitor SaaS — Product & Architecture Plan

Authors: Principal Architect & Senior Product Manager (working session)
Status: Approved for Phase 1 implementation (Phase 1 backend is implemented in this repo)

---

## 1. Product summary (PM)

**What we are selling:** dead-simple, contextual uptime monitoring for teams.
A company signs up, drops in a spreadsheet of URLs (or pastes a list), and gets
email alerts when anything stops responding or starts serving error/maintenance
pages — including the "returns 200 but is actually broken" case the original
poller was built to catch.

**Who it is for:**

- *Ops-adjacent IT staff at SMBs* — they have 20–200 internal and public URLs in
  a spreadsheet today and no budget for Datadog.
- *Agencies* — monitor every client site from one account; the per-company
  partitioning matters to them contractually.
- *Developers* — free tier, sign up with GitHub/Google, paste five URLs, done.

**Core user journeys (MVP):**

1. Sign up → company (organization) is auto-provisioned on the Free plan on
   first API call. No sales touch, no onboarding form.
2. Upload a CSV exported from Excel/Sheets **or** paste URLs into a textarea in
   the `status-web` SPA → per-row validation errors come back immediately; valid
   rows are queued and appear on the dashboard within seconds.
3. Watch the dashboard: current status per URL + drill-down history.
4. Get an email when something breaks (one digest per failed polling run, not
   one email per URL).
5. Hit the free-tier wall (11th URL) → clear in-product message → self-serve
   upgrade via Stripe Checkout.

**Explicit non-goals for MVP:** public status pages, Slack/PagerDuty/webhook
alerting, multi-region probes, TLS-expiry checks, SLA reports. All are roadmap
items (§8) and all are additive — nothing in the architecture blocks them.

---

## 2. Pricing & packaging (PM, validated by Architect for cost floor)

Limits are enforced server-side in `PlanCatalog` (single source of truth) at
three choke points: URL ingestion (max URLs), the poll scheduler (interval),
and the alerter (recipient cap).

| Plan | Price/mo | URLs | Poll interval | Alert recipients | History retention |
| --- | --- | --- | --- | --- | --- |
| Free | $0 | 10 | 60 min | 1 | 7 days |
| Starter | $9 | 50 | 15 min | 5 | 30 days |
| Pro | $29 | 250 | 5 min | 15 | 90 days |
| Business | $99 | 1,000 | 1 min | 50 | 365 days |

Why this shape:

- **Free tier is genuinely useful** (10 URLs hourly) but the upgrade trigger is
  natural: more URLs *or* faster detection. Both map linearly to our costs, so
  the free tier cannot be abused into a loss leader (worst case ≈ 7,200 HTTP
  checks/month per free tenant — fractions of a cent on consumption pricing).
- **Poll interval is the real product lever.** "How fast do I find out?" is what
  people pay for in monitoring; URL count alone is too easy to work around.
- Annual billing (2 months free) ships with Stripe in Phase 2 at zero extra
  engineering cost.

---

## 3. Architecture decisions (Architect)

### 3.1 From the legacy design to the multi-tenant design

The legacy system was a chain of portal-authored `.csx` functions, single
tenant, with one global URL list and one global recipient list:

```
timer → HTTP poller → {states, history, notifications} queues → table persisters / SendGrid
```

The port keeps the event-driven queue/table shape (it is the right shape, and
the cheapest) but makes **tenant id a first-class dimension** end to end:

```
PollScheduler (timer, every minute)
  └─ for each ACTIVE tenant whose plan interval has elapsed
       └─ poll-jobs-queue ──► UrlPoller (one tenant per message, bounded concurrency)
                                ├─► status-states-queue  ──► StatusStatePersister  ──► UrlStatuses table
                                ├─► status-history-queue ──► StatusHistoryPersister ──► UrlStatusHistory table
                                └─► status-notifications-queue ──► EmailAlerter ──► SendGrid (per-tenant recipients)

status-web SPA ──(Clerk/WorkOS JWT)──► HTTP API (auth middleware → TenantContext)
   GET  /api/me                        tenant + plan + usage (auto-provisions on first call)
   GET  /api/urls                      list monitored URLs
   POST /api/urls                      bulk import: JSON array OR raw CSV/pasted text
   DELETE /api/urls/{urlKey}           remove a URL
   GET  /api/status                    current state of every URL
   GET  /api/status/{urlKey}/history   recent checks, newest first
        POST /api/urls ──► url-import-queue ──► UrlImportPersister ──► MonitoredUrls table
```

Key changes vs legacy, and why:

| Decision | Rationale |
| --- | --- |
| Compiled **.NET 8 isolated worker** instead of `.csx` | Testable, dependency-injected, CI/CD-deployable; the in-process model is on a deprecation path. |
| **Queue-driven poller fan-out** (one message per tenant) instead of one HTTP mega-poll | The legacy poller did all URLs in one invocation — at 500 tenants it would exceed function timeouts. One tenant per message scales horizontally and isolates a slow tenant from everyone else. |
| **Scheduler ticks every minute and consults the plan** instead of a fixed hourly CRON | Poll frequency is a paid feature (§2). One timer serves all tiers. |
| **Writes go through queues, reads are direct** | A 5,000-row spreadsheet upload returns 202 immediately; persistence is retried automatically by queue semantics (`maxDequeueCount: 5`). |
| `TreatWarningsAsErrors`, nullable enabled, unit-tested core | The parsing/limits/poll-classification logic is where the business risk is; it is all in `StatusMonitor.Core` with no Azure dependency, so it tests in milliseconds. |

### 3.2 Tenant partitioning model (the security boundary)

Azure Table Storage **PartitionKey = tenant id** on every tenant-owned table.
This is both the scale unit and the isolation unit:

| Table | PartitionKey | RowKey | Notes |
| --- | --- | --- | --- |
| `Tenants` | `"TENANT"` | tenant id | Small directory; plan, Stripe ids, alert emails, `LastPolledAtUtc`. |
| `MonitoredUrls` | tenant id | normalized url key | Idempotent re-imports (same name → same key). |
| `UrlStatuses` | tenant id | url key | Current state, upserted. |
| `UrlStatusHistory` | tenant id | `{urlKey}_{reverseTicks}` | Prefix range scan = one URL's history, newest first, no partition scan. |

Isolation guarantees:

- The tenant id **never comes from the request body or query string**. For HTTP
  it is resolved by auth middleware from the validated JWT (`org_id` claim);
  for queue work it travels inside messages that only our own code enqueues.
- Every repository method takes a tenant id and queries a single partition.
  There is no API shape that can express a cross-tenant read.
- Tenant id doubles as the natural shard key if a tenant ever outgrows a
  partition's throughput (2,000 ops/s — orders of magnitude above our needs).

**Why Table Storage and not SQL/Cosmos?** The access patterns are pure
key/partition lookups — no joins, no cross-tenant queries, no transactions
beyond single-entity upserts. Table Storage costs ~$0.045/GB-month plus
$0.00036 per 10k transactions; a 250-URL Pro tenant polling at 5 minutes
generates roughly 4.3M transactions/month ≈ **$0.16**. Azure SQL starts at ~$5
and Cosmos at ~$24/month before a single customer pays us. If we later need
SQL-ish reporting, we add a read model — we do not change the write path.

### 3.3 Identity: Clerk vs WorkOS

Both are OIDC issuers with an organizations concept, so the backend treats this
as configuration, not code: `TenantAuthenticator` validates any OIDC bearer
token via JWKS discovery and reads the org claim (`Auth__Authority`,
`Auth__TenantClaim` settings). **We cannot be held hostage by either vendor.**

**Recommendation: start with Clerk.**

| Criterion | Clerk | WorkOS |
| --- | --- | --- |
| SPA integration speed | Prebuilt React components (`<SignIn/>`, `<OrganizationSwitcher/>`) — days, not weeks | AuthKit is hosted-redirect based; org UI is more DIY |
| Organizations on free tier | Yes (10k MAU free) | Free up to 1M MAU but SSO/SCIM are the paid features |
| Enterprise SSO/SCIM later | Add-on, per-connection | Best-in-class; this is WorkOS's core business |
| JWT shape | `org_id` claim, standard JWKS | `org_id` claim, standard JWKS |

Decision: **Clerk for product-led launch** (the SPA is where the integration
effort lives, and Clerk's org switcher is exactly our tenant switcher). When
the first enterprise deal demands SAML, we either buy Clerk's SSO add-on or put
WorkOS behind the same middleware — a config change on the backend.

Personal accounts (no organization) map to a `user_{sub}` tenant so individual
developers get the free tier without creating an org.

### 3.4 Payments: Stripe (Phase 2, designed now)

Stripe Checkout + Billing Portal + webhooks. We never touch card data (SAQ-A).

- `Tenants` already carries `StripeCustomerId`, `StripeSubscriptionId`,
  `PlanId`, `Status` — the webhook handler only flips these fields.
- New HTTP functions (Phase 2):
  - `POST /api/billing/checkout-session` → Stripe Checkout URL for a plan.
  - `POST /api/billing/portal-session` → self-serve manage/cancel/upgrade.
  - `POST /api/stripe/webhook` (signature-verified, anonymous route):
    `checkout.session.completed` → set plan; `customer.subscription.updated` →
    sync plan; `invoice.payment_failed` (after retries) → `Status = Suspended`;
    `customer.subscription.deleted` → back to Free (over-limit URLs are kept
    but polling pauses beyond the free cap, never silently deleted).
- Enforcement is already live in the backend: the scheduler only polls
  `Active` tenants, ingestion rejects over-limit imports with an upgrade
  message, and the alerter caps recipients — so "payments" reduces to mutating
  the tenant record.

**Free tier without a card:** no Stripe objects are created until first
checkout. Free tenants cost us ~$0.001/month each (§5); requiring a card would
kill the top of funnel for no material savings.

### 3.5 Security checklist

- [x] JWT validation against issuer JWKS (cached via `ConfigurationManager`),
      lifetime + issuer validated, optional audience.
- [x] Tenant resolved exclusively from token claims; 401 before any function
      body runs (worker middleware).
- [x] Per-row input validation on imports; URL scheme allow-list (http/https);
      5,000-row cap per upload; RowKey-unsafe characters normalized away.
- [x] No secrets in repo — `local.settings.json` is gitignored; an example file
      documents required settings.
- [ ] Phase 1 deploy: Functions app behind HTTPS-only + CORS locked to the SPA
      origin; secrets in Key Vault references; managed identity for storage
      (swap connection string for `TableServiceClient(uri, credential)`).
- [ ] Phase 2: Stripe webhook signature verification; per-tenant rate limiting
      on import endpoints.
- [ ] SSRF hardening for the poller (Phase 1.5): block RFC1918 / link-local /
      metadata-endpoint targets so a tenant cannot probe our VNet via a
      "monitored URL". (Consumption-plan functions have no VNet by default,
      which conveniently limits the blast radius at launch.)

---

## 4. Frontend plan: `status-web` SPA (PM + Architect)

React + Vite + TypeScript, Clerk React SDK. **Hosting stays where it is today:
the SPA on Netlify, the Functions app on Azure** — both free tiers cover MVP,
and there is no benefit to migrating either side.

### 4.1 Repo strategy: monorepo (decision)

The SPA moves **into this repo** under `apps/status-web/` rather than living in
a separate repo:

```
src/                      backend (.NET) — deployed to Azure Functions
apps/status-web/          SPA (React/Vite) — deployed to Netlify
docs/, tests/, legacy/
```

Why monorepo:

- **One PR per feature.** Every meaningful change here touches the API and the
  UI that consumes it (import errors, usage meter, billing). Two repos means
  two PRs, cross-repo coordination, and "deploy backend first" choreography
  that a single reviewable change eliminates.
- **The API contract lives next to its consumer.** The SPA's API client types
  are reviewed in the same diff as the C# response shapes; drift is caught at
  review time. (If/when we want generated types, we emit an OpenAPI spec from
  the Functions app and generate the TS client inside the same build.)
- **One team, one product.** Polyrepo earns its overhead when separate teams
  own separate release cadences. We are nowhere near that.
- **Both deploy targets are path-aware**, so the monorepo costs nothing in
  CI/CD (see 4.2).

### 4.2 Deployment topology & CI/CD

| Component | Host | Trigger |
| --- | --- | --- |
| Functions app | Azure (consumption) | GitHub Actions with `paths: [src/**, tests/**]` filter → build, test, `azure/functions-action` deploy |
| `status-web` SPA | Netlify | Netlify Git integration with **base directory** `apps/status-web` and an ignore command (`git diff --quiet HEAD^ HEAD -- apps/status-web/`) so backend-only commits don't trigger UI builds |

Netlify specifics:

- **Deploy previews per PR** are the main reason to keep Netlify: every PR gets
  a URL the PM can click. Point previews at a staging Functions app via
  `VITE_API_BASE_URL` (deploy-context env var), never at production.
- **Proxy the API through Netlify redirects** to make the SPA and API
  same-origin: `/_redirects` rule `/api/* https://<funcapp>.azurewebsites.net/api/:splat 200`.
  This removes CORS preflights (lower latency on every dashboard poll), hides
  the raw Azure hostname, and means the SPA needs no API URL at runtime in
  production. Keep Functions-side CORS locked to the Netlify domains as
  defense-in-depth for direct calls.
- Env vars: `VITE_CLERK_PUBLISHABLE_KEY` (per deploy context),
  `VITE_API_BASE_URL` (previews/branch deploys only).
- Custom domain + HTTPS on Netlify; the Functions hostname is an internal
  detail behind the proxy.

### Screens (MVP):

1. **Sign-in / sign-up** — Clerk components, Google/GitHub/email.
2. **Dashboard** — `GET /api/status` table: name, URL, status pill, last
   checked, description; auto-refresh every 30s.
3. **Add URLs** — one screen, two inputs, both already supported by
   `POST /api/urls`:
   - file drop-zone for `.csv` (sent as `text/csv`; parsing is server-side so
     Excel/Sheets quirks are handled in one place),
   - textarea for paste (bare URLs, `name,url`, or tab-separated — i.e. a
     straight copy out of a spreadsheet).
   - Response renders queued count + per-line errors + plan usage meter
     (`currentCount`/`maxUrls` from the API).
4. **URL history** — `GET /api/status/{urlKey}/history` timeline.
5. **Settings** — alert recipients, plan card with usage, upgrade button
   (Phase 2 wires it to Stripe Checkout).

API contract is the one implemented in `UrlApiFunctions` / `StatusApiFunctions`;
the SPA sends `Authorization: Bearer <Clerk session token>` on every call.

---

## 5. Cost model (Architect)

Consumption-plan Functions + Storage on Azure; SPA on Netlify (free tier, then
$19/mo Pro when we want more build minutes/team seats). Per-tenant marginal
cost at full plan utilization:

| Tier | Checks/mo | Storage txns/mo (~5×) | Est. marginal cost/mo | Price | Gross margin |
| --- | --- | --- | --- | --- | --- |
| Free (10 @ 60m) | 7.2k | 36k | < $0.01 | $0 | top-of-funnel cost |
| Starter (50 @ 15m) | 144k | 720k | ≈ $0.08 | $9 | ~99% |
| Pro (250 @ 5m) | 2.16M | 10.8M | ≈ $1.10 | $29 | ~96% |
| Business (1,000 @ 1m) | 43.2M | 216M | ≈ $18 | $99 | ~80% |

Fixed costs at launch: SendGrid free tier (100 emails/day) → ~$20/mo at scale;
Clerk free to 10k MAU; Application Insights with sampling ≈ $5–20/mo.
**Platform floor is ≈ $0–50/month until we have real traffic**, which is the
cost-efficiency target the consumption-plan + Table Storage stack was chosen
for. The first ~thousand paying customers require no re-architecture; beyond
that, the known evolution path is Flex Consumption (cold-start SLAs), Event
Grid/Service Bus if queue semantics need sessions, and a dedicated probe pool
if Business-tier 1-minute polling saturates queue consumers.

---

## 6. What is implemented in this repo (Phase 1 backend)

- `src/StatusMonitor.Core` — plan catalog & limits, tenant context, table
  entities & repositories, queue publisher, CSV/paste parser, ingestion
  service, HTTP poller service. 31 unit tests.
- `src/StatusMonitor.Functions` — .NET 8 isolated worker: poll scheduler,
  queue-driven poller, three queue persisters, SendGrid alerter, tenant-scoped
  HTTP API, OIDC auth middleware (Clerk/WorkOS) with a Development mode
  (`x-tenant-id` header) for local work against Azurite.
- `legacy/` — the original `.csx` functions, frozen for reference, with a
  mapping table to their replacements.

## 7. Delivery phases

**Phase 1 — Replatform (this PR):** port to .NET 8 isolated worker, multi-tenant
data model, auth middleware, bulk ingestion with free-tier limits, per-tenant
alerting. *Done in this repo; deploy + CI/CD pipeline (GitHub Actions →
`az functionapp deploy`, Bicep for storage/function app/Key Vault) is the
remaining step.*

**Phase 2 — Monetization:** Stripe Checkout/Portal/webhooks (§3.4), suspension
flow, usage meter in `/api/me` (done) surfaced in SPA settings, annual pricing.

**Phase 3 — status-web SPA:** move/scaffold the SPA into `apps/status-web/`
(monorepo, §4.1), screens in §4, Clerk integration, Netlify base-directory +
ignore-command config, `/api/*` proxy redirect to the Azure Functions app,
deploy previews against staging, custom domain, CORS lock-down.

**Phase 4 — Retention & hardening:** history TTL sweeper per plan (timer
function deleting aged `UrlStatusHistory` partitions), SSRF egress filter,
per-tenant rate limits, alert deduplication (only notify on state *changes* —
the entity model already stores previous state to diff against).

**Phase 5 — Expansion (PM backlog, priority order):** Slack + webhook alerts,
public status pages (read-only, per-tenant vanity URL — strong agency pull),
multi-region probes, TLS/domain-expiry checks, enterprise SSO via WorkOS or
Clerk add-on.

## 8. Risks & open questions

| Risk | Mitigation |
| --- | --- |
| Email deliverability (alerts land in spam) | Dedicated sending domain + SPF/DKIM from day one; Phase 5 adds non-email channels. |
| A tenant points us at a target they don't own (we become a DDoS proxy) | Per-plan URL caps + 1-min floor already bound request volume; Phase 4 adds per-host dedupe and robots-style opt-out handling. |
| Storage-queue at-least-once delivery → duplicate history rows | History writes are idempotent-enough (timestamped row keys); state writes are upserts. Acceptable for MVP. |
| Clerk org claim shape changes / vendor lock concerns | Claim name and authority are config; middleware is provider-agnostic OIDC. |
| Free-tier abuse (many orgs, one user) | Clerk caps orgs per free user; revisit with signup throttling if observed. |
