# Base.It — Pricing Recommendation

A pricing model for **Base.It**, a Windows desktop tool for SQL Server schema capture, diff, sync, and drift-watching.

## TL;DR recommendation

**Offer both, with subscription as the default.** This matches where the developer-tooling market has moved (Redgate, JetBrains, ApexSQL are all subscription-first) while keeping the perpetual option that DBA/enterprise buyers and budget-cycle-driven shops still ask for.

| Tier | Subscription (per seat) | Perpetual (per seat) | Who it's for |
|------|------------------------|----------------------|--------------|
| **Free** | $0 forever | — | Trial / light individual use (Compare + Query, 1 connection) |
| **Pro** | **$12/mo billed annually ($144/yr)** | **$249 once** (incl. 1 yr updates) | Individual devs & DBAs shipping schema changes |
| **Team** | **$9/mo per seat, 5+ seats ($108/yr)** | **$199 once per seat** | Teams standardising DB change across environments |

Perpetual maintenance/update renewal after year one: **~25% of list/yr** (~$62 Pro) to keep updates flowing — standard for the category.

## Why this is the right shape

### 1. The market has moved to subscription
The closest comparables price per developer seat and lead with subscriptions:

- **Redgate SQL Compare** — roughly **$535–$695/seat/yr** (subscription-only now).
- **ApexSQL Diff** — roughly **$399/yr**.
- **dbForge Schema Compare** — perpetual, roughly **$240 one-time** (one of the few holdouts still selling perpetual prominently).
- **JetBrains / Visual Studio–class dev tools** — almost universally annual subscription.

Current trend across dev tooling (2024–2026): **subscription-first, with a free tier for adoption**, and perpetual offered as a higher-priced "you own it" alternative rather than the default.

### 2. Base.It should undercut the incumbents, not match them
Base.It is a focused single-purpose tool, not a full suite. Pricing at **~$144/yr Pro** positions it as a clear value pick — roughly **3–4× cheaper than Redgate SQL Compare** — which is the right wedge for a newer entrant trying to win adoption. It's also wide enough above "free" to signal real product value.

### 3. Keep a real free tier
Compare + Query with one connection costs you little to give away (they're read-only paths) and drives adoption. The paid line is drawn exactly where the *risk and value* concentrate: **Sync, Batch, Watch, and DACPAC/git staging** — the features that mutate databases and integrate with source control.

### 4. Offer perpetual to remove a buying objection
Many SQL Server shops are enterprise/DBA-heavy with capex budgets and a cultural preference for "buy it once." A **$249 perpetual (1 year of updates, then optional ~25%/yr maintenance)** captures those buyers without cannibalising subscription revenue — perpetual is priced so the subscription pays for itself in roughly two years, nudging most buyers toward the subscription.

## Suggested rollout

1. **Launch:** Free + Pro (subscription) + Pro (perpetual). Keep it simple.
2. **Add Team tier** once you have 2–3 multi-seat customers asking for volume/invoicing.
3. **Enterprise / site license** (custom quote) when deal sizes justify it — bundle SSO/AD auth, priority support, and the scriptable engine for CI as the differentiators.

## Levers to test later
- **Annual vs monthly split** — offer monthly at ~$15/mo to make the annual ($12/mo equiv.) look like the deal.
- **Maintenance renewal rate** — 20–25%/yr is the proven band for perpetual dev tools.
- **Seat thresholds** — the 5-seat Team break is arbitrary; tune to where your real deals cluster.

> Competitor figures above are approximate public list prices for positioning only and change over time — verify against each vendor before publishing.
