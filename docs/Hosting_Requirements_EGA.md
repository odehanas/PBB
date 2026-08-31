# GovBudget — Hosting & Technical Requirements

**Prepared for:** E-Government Authority (EGA) — sandbox / trial environment
**System:** GovBudget — Programme & Performance-Based Budgeting (PBB) platform
**Document status:** for EGA review. Values marked **[confirm]** need agreement between us and EGA.

---

## 1. Purpose and scope of this phase

EGA to provide a **sandbox environment for functional testing** with a small, named group of
test users. No production data, no public access, no high-availability requirement in this
phase. The sandbox should nonetheless mirror the intended production topology closely enough
that the findings carry over.

Out of scope for the sandbox: clustering, load balancing, DR replication, external SSO.
Section 12 lists what those would add later.

---

## 2. Solution overview

A single ASP.NET Core web application plus one SQL Server database. No message broker, no
cache server, no background worker, no file share.

```
   Government users (browser, HTTPS 443)
                 |
        [ EGA reverse proxy / WAF ]        <- optional, see 5.3
                 |
   ASP.NET Core 10 web app  (IIS + ASP.NET Core Module, in-process)
      - local disk: App_Data\keys, logs
                 |  TDS 1433 (encrypted)
   Microsoft SQL Server database  (schema: core)
                 |
   Outbound HTTPS (optional, see 5.4): OpenAI API, OECD statistics API
```

Attachments uploaded by users are stored **inside the database** (`VARBINARY(MAX)` columns),
not on disk, so no shared storage is required.

---

## 3. Application server

| Item | Requirement | Notes |
|---|---|---|
| OS | Windows Server 2019 / 2022 / 2025, 64-bit | Linux is also acceptable — see 3.3 |
| Web server | IIS 10+ with **ASP.NET Core Module V2** | Configured by the hosting bundle |
| Runtime | **.NET 10 ASP.NET Core Hosting Bundle** (LTS) | Must be .NET **10.x**; the app targets `net10.0` |
| Hosting model | In-process (`hostingModel="inprocess"`) | Set in the app's `web.config` |
| vCPU | 2 (sandbox) | 4 for production **[confirm concurrency]** |
| RAM | 4 GB (sandbox) | 8 GB for production; Excel import/export is the peak consumer |
| Disk | 20 GB | App ~200 MB; remainder for logs and OS |
| App pool | Dedicated, **No Managed Code**, Integrated pipeline | .NET Core runs out-of-CLR |
| App pool identity | Any service account with the folder rights in 3.1 | |
| Idle time-out | **0** (disabled) | Prevents cold-start delays for a low-traffic internal system |
| Recycling | Fixed daily window outside working hours, if required by policy | |

### 3.1 Required file-system permissions

The application account needs **Modify** on two folders inside the site directory:

| Path | Purpose | Consequence if not writable |
|---|---|---|
| `App_Data\keys\` | ASP.NET Core data-protection key ring — encrypts auth cookies and antiforgery tokens | Keys regenerate on every restart; **all users are signed out** on each recycle |
| `logs\` | Start-up / stdout diagnostics | No evidence available when the app fails to start |

`App_Data\keys` must be **backed up** and must **never be overwritten by a deployment**. It
is created by the app on first run.

### 3.2 No other server-side dependencies

Not required: SMTP relay (see 12.3), file share, Redis, message queue, scheduled tasks,
COM components, Office installation, or any 32-bit component.

### 3.3 Linux / container alternative

The application is platform-neutral. If EGA's sandbox is Linux or Kubernetes it can run on
`mcr.microsoft.com/dotnet/aspnet:10.0` behind Nginx, provided:

- the two writable paths in 3.1 are mounted as a **persistent volume** (an ephemeral
  container filesystem would sign users out on every pod restart), and
- the forwarded-headers change in 5.3 is applied.

We would supply a `Dockerfile` on request. Windows/IIS is the lower-risk option today
because it is the configuration already in production use.

---

## 4. Database

| Item | Requirement | Notes |
|---|---|---|
| Product | Microsoft SQL Server **2019 or later** | 2016+ works; Azure SQL / Managed Instance also acceptable |
| Edition | Standard (Express is sufficient for the sandbox) | Express caps at 10 GB — adequate for trial |
| Compatibility level | 130 or higher | |
| Database name | e.g. `GovBudget_SBX` **[confirm]** | |
| Schema | `core` — created by our scripts | All objects live in `core`, not `dbo` |
| Collation | Any; `Arabic_CI_AS` or `SQL_Latin1_General_CP1_CI_AS` both fine | All text columns are `NVARCHAR`, so Arabic is safe regardless |
| Size | 2 GB initial data + 1 GB log for the sandbox | Master data is small; growth is driven by uploaded attachments |
| Recovery model | Simple for sandbox, Full for production | |
| Connectivity | TDS 1433 from the app server only; **`Encrypt=True`** | We do not require a public endpoint |
| Auth | SQL authentication **[confirm]** — or Windows/managed identity if EGA prefers | Connection string is supplied by EGA and held server-side only |

### 4.1 Database account privileges — please read

The application performs **idempotent schema upgrades at start-up** (`SecurityUpgrade`,
`AllocationScenarioUpgrade`, `PermissionSeeder`): it adds a missing column or creates a
missing table, then seeds reference rows. Two options:

- **Option A (simpler).** Grant the app login `db_owner` on this database only. First start
  after each release self-heals the schema.
- **Option B (least privilege, preferred by most security teams).** We deliver a versioned
  DDL script per release; EGA's DBA runs it during the change window. The app login then
  needs only `db_datareader` + `db_datawriter` + `EXECUTE`. The start-up upgrades detect the
  work is already done and no-op.

**[confirm which option EGA requires]**. Option B needs no code change, only a release
process agreement.

### 4.2 Backup

Standard EGA policy is acceptable. Our request: nightly full backup plus transaction-log
backups in production, and that the `App_Data\keys` folder is included in the file-level
backup of the app server.

---

## 5. Network, DNS and TLS

### 5.1 Inbound

| Source | Destination | Port | Protocol |
|---|---|---|---|
| Government intranet clients **[confirm scope]** | App server | 443 | HTTPS |
| App server | SQL Server | 1433 | TDS, encrypted |

Port 80 is only needed if EGA wants an HTTP→HTTPS redirect at the edge; the app itself
redirects and sends HSTS.

### 5.2 Hostname and certificate

- One DNS name, e.g. `govbudget-sbx.ega.gov.**[confirm]**`
- A TLS certificate for it (TLS 1.2 minimum, 1.3 preferred)
- **The hostname must be given to us before go-live.** The app validates the `Host` header
  against an allow-list (`AllowedHosts`); an unlisted hostname returns HTTP 400 for every
  request. This is a one-line configuration value, not a code change.

### 5.3 If a reverse proxy or WAF terminates TLS — action required

The app enforces HTTPS redirection, HSTS and `Secure` cookies. If TLS terminates at a proxy
and the internal hop is plain HTTP, the app will see `http` and users will hit a **redirect
loop** and rejected cookies.

Two ways to resolve, either is fine:

- **Preferred:** keep HTTPS end-to-end (proxy re-encrypts to the app), or
- EGA sends `X-Forwarded-Proto` / `X-Forwarded-For` and **we add forwarded-headers
  middleware** with EGA's proxy IPs as known networks. Small, well-understood change; we
  need the proxy addresses.

Please tell us which topology the sandbox uses. This is the single most likely cause of a
"the site does not work" first day.

### 5.4 Outbound internet — optional, feature-dependent

Two features call external services. Both can be **switched off by configuration** if EGA
policy forbids egress; the rest of the system is unaffected.

| Destination | Port | Feature | If blocked |
|---|---|---|---|
| `api.openai.com` | 443 | AI Budget Assistant (natural-language Q&A over the user's own scoped data) | Assistant reports "not configured"; set `Assistant:Enabled=false` to hide it |
| `sdmx.oecd.org`, `oecd.org` | 443 | Live OECD benchmark/statistics lookups | Set `Assistant:OecdLiveEnabled=false`; the offline OECD reference document still works |

If egress is allowed, an outbound proxy is acceptable. Note for EGA's data-protection
review: the assistant sends the user's **question plus the query results the tools return**
to OpenAI. It never exposes the database directly, and results are already restricted to
the signed-in user's entity/cost-centre scope. If that is unacceptable for the trial,
disable it — recommended default for the sandbox unless EGA explicitly approves it.

---

## 6. Configuration values EGA must provide or set

Held **server-side only**, in `appsettings.Production.json` in the site root (excluded from
source control and from deployments), or as environment variables.

| Setting | Purpose | Provided by |
|---|---|---|
| `ConnectionStrings:DefaultConnection` | SQL Server connection, `Encrypt=True` | EGA |
| `AllowedHosts` | The sandbox hostname (see 5.2) | EGA |
| `ASPNETCORE_ENVIRONMENT` | `Production` | us (in `web.config`) |
| `Security:ContentSecurityPolicy` | Optional CSP override if EGA has a standard policy | EGA (optional) |
| `Assistant:ApiKey` | OpenAI key — only if the assistant is enabled | us or EGA **[confirm]** |
| `Assistant:Enabled`, `Assistant:OecdLiveEnabled` | Feature switches per 5.4 | EGA decision |

Note: configuration is read at process start; changing any value requires an **app pool
recycle**, not just a browser refresh.

---

## 7. Identity and access

- **Today:** local application accounts. PBKDF2 password hashing, account lockout, forced
  password change on admin-issued passwords, IP-based rate limiting on the sign-in endpoint,
  60-minute sliding session, role-based access (SYSADMIN / ADMIN / entity roles) plus a
  per-form permission matrix enforced server-side on every request.
- **Password reset** currently produces a link that an administrator hands to the user
  (no mail server needed). See 12.3 for e-mail delivery.
- **Future SSO:** if EGA requires national SSO / Entra ID / SAML, that is a scoped change to
  the authentication layer — see 12.4. Not needed for the sandbox.

For the trial we need **[confirm]**: the number of test users and whether EGA wants them
created by us or self-registered by an EGA administrator.

---

## 8. Session state and scaling

The sandbox should be a **single application instance**. If EGA later fronts multiple
instances with a load balancer, two things must be addressed:

- **Session state** is in-process today → requires **sticky sessions**, or a change to a
  distributed store (SQL Server or Redis).
- **Data-protection keys** are on local disk → all instances must share the key ring folder,
  or it must be moved to the database.

Both are configuration-level changes we can make; we simply need to know the target topology
before production.

---

## 9. Deployment and release process

What we need from EGA:

1. **A deployment channel to the sandbox**, one of, in order of preference:
   - **Web Deploy / MSDeploy** endpoint with credentials (fastest, incremental, takes the
     app offline automatically) — we already have a publish profile for this model;
   - a CI/CD path (Azure DevOps / GitHub Actions self-hosted runner) if EGA standardises on
     one — we can supply the pipeline definition;
   - SFTP/file-copy access to the site folder, or an escorted change window where EGA staff
     deploy a package we hand over.
2. **A named technical contact** for the app-pool recycle and the DB change window.
3. **The rule that these paths are never overwritten** by a deployment:
   `appsettings.Production.json`, `App_Data\`, `logs\`.

Release content is a standard .NET publish output (DLLs + `wwwroot` static assets +
`web.config`). Razor views are compiled into the DLLs, so every UI change ships as a normal
release. Typical release size: under 100 MB, incremental deploys far smaller.

---

## 10. Monitoring, logging and support

- **Application logs:** ASP.NET Core logging to stdout, written to `logs\` when enabled.
  Disabled by default because the file grows unbounded; we switch it on to diagnose a
  start-up failure. If EGA has a central log platform we can emit structured logs to it
  **[confirm platform]**.
- **Audit trail:** the application records created/updated by and timestamps on budget
  records, submissions, approvals and password-reset actions, in the database.
- **Health probe:** the app has no `/health` endpoint today. If EGA's load balancer or
  monitoring requires one, it is a very small addition — see 12.1.
- **Time zone:** server clock in **[confirm — Arabia Standard Time / UTC]**. Audit timestamps
  are written in UTC (`SYSUTCDATETIME`).

---

## 11. Data migration into the sandbox

For a functional trial we suggest starting from a **restored copy of the current database**
so testers work with realistic Programme/Activity/KPI structures and Arabic descriptions.

- We provide: a SQL Server backup (`.bak`) or a full DDL + data script, plus the object
  inventory.
- EGA provides: restore into the sandbox instance, then confirm the app login's rights per 4.1.
- Alternative: empty database plus our reference-data scripts, and testers enter their own
  data. Slower to reach meaningful testing.

**[confirm]** whether real ministry budget figures are permitted in the sandbox, or whether
we should supply an anonymised/reduced data set instead. We can produce the anonymised set.

---

## 12. Items that need a code change (not required for the sandbox)

Listed with rough sizing so EGA can decide what to include in a production phase.

| # | Item | Trigger | Size |
|---|---|---|---|
| 12.1 | `/health` endpoint (liveness + DB check) | EGA monitoring or LB probe requires it | Very small |
| 12.2 | Forwarded-headers middleware | TLS terminates at a proxy (5.3) | Very small |
| 12.3 | SMTP delivery for password-reset links | EGA wants users to receive e-mail; needs relay host, port, credentials, from-address | Small |
| 12.4 | SSO / federated identity (Entra ID, SAML, national ID) | EGA mandates central authentication | Medium |
| 12.5 | Distributed session + shared key ring | More than one app instance (8) | Small |
| 12.6 | Central structured logging / SIEM sink | EGA log platform integration | Small |
| 12.7 | Secret store integration (Key Vault / EGA equivalent) | Policy forbids secrets in server-side config files | Small–medium |

---

## 13. Sandbox acceptance checklist

Run in this order; each line either passes or gives a precise fault to chase.

1. `https://<hostname>/` returns the sign-in page — TLS, DNS, `AllowedHosts` and the
   redirect topology (5.3) are all correct.
2. Sign in as an administrator — DB connectivity and password hashing work.
3. Confirm start-up upgrades succeeded: no error in `logs\stdout_*.log`, and the role /
   permission tables are populated.
4. Create a budget line, submit it, approve it — write path and the permission matrix work.
5. Import an Excel actuals file — upload limits and `VARBINARY` storage work.
6. Export the Management Review report to Excel — ClosedXML generation works.
7. Recycle the app pool, then reload — the user is **still signed in**, proving
   `App_Data\keys` is persisted and writable (3.1).
8. Assistant panel: either answers a question (egress open) or reports "not configured"
   (disabled by design) — no error either way.

---

## 14. Summary of what we need from EGA

1. App server per section 3, with the two writable folders in 3.1.
2. SQL Server database per section 4, plus a decision on the privilege model in 4.1.
3. The sandbox **hostname** and TLS certificate, and confirmation of the proxy topology (5.3).
4. Firewall decision on outbound egress (5.4) — or instruction to disable those features.
5. A **deployment channel** and technical contact (section 9).
6. Number of test users, and a decision on the sandbox data set (11).
7. Answers to the **[confirm]** items above.

## 15. What we deliver

1. Deployment package (or repository access for a CI/CD pipeline).
2. Database backup or DDL + reference-data scripts, and the per-release DDL script if
   Option B in 4.1 is chosen.
3. This document maintained as the environment definition, plus `docs/DEPLOY.md` for the
   release procedure and the in-app user guides.
4. Support during the sandbox test window, and the acceptance run in section 13.
