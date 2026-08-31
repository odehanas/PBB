# GovBudget — Sandbox Hosting Request (Summary)

**To:** E-Government Authority — Hosting / Infrastructure
**Re:** Sandbox environment for GovBudget (Programme & Performance-Based Budgeting platform)

A short summary of what the system needs. Full detail, including the acceptance
test plan, is in the accompanying document *GovBudget — Hosting & Technical
Requirements*.

---

## 1. What the system is

A single ASP.NET Core 10 web application with one Microsoft SQL Server database.
Users work in a browser. No desktop client, no message broker, no cache server,
no file share, no scheduled jobs. Uploaded attachments are stored inside the
database, so no shared storage is needed.

```
   Users (browser, HTTPS 443)
        |
   [ EGA reverse proxy / WAF ]   <- optional; see item 3 below
        |
   ASP.NET Core 10 on IIS (in-process)
        |  TDS 1433, encrypted
   SQL Server database (schema: core)
        |
   Outbound HTTPS (optional, can be switched off)
```

---

## 2. What we are asking EGA to provide

**Application server**

- Windows Server 2019 / 2022 / 2025 with IIS, and the **.NET 10 ASP.NET Core
  Hosting Bundle** installed. Linux or a container platform is equally acceptable.
- Sandbox sizing: **2 vCPU, 4 GB RAM, 20 GB disk**. A dedicated application pool.
- **Modify permission on two folders** inside the site directory: `App_Data\`
  (encryption key ring) and `logs\`. If `App_Data` is not writable, every user is
  signed out each time the application restarts.

**Database**

- SQL Server 2019 or later; Express is sufficient for the trial. Roughly 2 GB
  data + 1 GB log. Azure SQL is also fine.
- A decision on one point: the application can self-heal its own schema at
  start-up if its login holds `db_owner` on that database, **or** we deliver a
  DDL script per release for an EGA DBA to run, in which case the application
  needs only read/write. Either model works — we need to know which EGA requires.

**Network**

- One hostname and a TLS certificate. Inbound 443 from the intended user
  population; outbound 1433 to the database.
- The hostname must be shared with us in advance: the application validates the
  `Host` header against an allow-list and will reject an unlisted name.

**Access**

- A deployment channel — **Web Deploy is preferred** (one click, incremental, and
  it takes the app offline automatically). SFTP or an escorted change window also
  work.
- A named technical contact for application-pool recycles and database change
  windows.

---

## 3. One technical point that decides the first day

If EGA terminates TLS at a reverse proxy or WAF and forwards plain HTTP to the
application, the application will see an insecure request, redirect the browser
back to HTTPS, and users will hit a **redirect loop** with rejected cookies.

Either keep **HTTPS end-to-end**, or tell us the proxy addresses and that
`X-Forwarded-Proto` is sent — we then enable forwarded-headers handling. Small
change on our side, but we need to know which topology applies before testing
starts.

---

## 4. Outbound internet — EGA's decision

Two optional features reach the public internet: an AI budget assistant
(`api.openai.com`) and live OECD benchmark statistics (`sdmx.oecd.org`).

Both are **switchable off by configuration** and nothing else depends on them.
For the sandbox we suggest they stay **disabled** unless EGA has reviewed and
approved the assistant, because it transmits the signed-in user's question and
the query results to an external service.

---

## 5. Security posture already built in

Delivered and testable in the sandbox: PBKDF2 password hashing, account lockout,
forced password change on administrator-issued credentials, rate limiting on
sign-in, role-based access with a per-form permission matrix enforced on every
server request, entity/cost-centre data scoping, antiforgery protection, HTTPS
enforcement with HSTS, security response headers with a configurable Content
Security Policy, and a database audit trail of budget, submission, approval and
credential actions.

---

## 6. What we deliver

1. The deployment package, or repository access if EGA prefers its own CI/CD.
2. The database: either a schema + synthetic sample-data script set (no real
   figures), or an anonymised copy of the current data — EGA's choice. Both are
   ready.
3. Documentation: environment definition, release procedure, provisioning
   run-order, and in-app user guides.
4. Support through the trial, and a joint run of the 8-step acceptance checklist.

---

## 7. Decisions we need from EGA to proceed

1. Sandbox hostname and TLS arrangement, and the proxy topology (item 3).
2. Database privilege model (item 2).
3. Outbound internet: permitted, or features disabled (item 4).
4. Deployment channel and technical contact (item 2).
5. Number of test users, and whether real budget figures are permitted in the
   sandbox or an anonymised/synthetic set should be used.
6. Confirmation of server time zone and whether EGA requires integration with a
   central logging platform or single sign-on in a later phase.

Once items 1–4 are settled the environment can be stood up and handed to testers
the same week.
