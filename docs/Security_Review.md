# GovBudget — Security Review and Remediation Report

**System:** GovBudget (Programme-Based Budgeting platform)
**Owner:** Ras Al Khaimah Government — Department of Finance
**Technology:** ASP.NET Core 10 MVC, Entity Framework Core, SQL Server
**Review date:** August 2026
**Prepared for:** Electronic Government Authority (EGA) review meeting

---

## 1. Purpose and scope

This report documents a source-code level security review of the GovBudget application and
the remediation applied as a result. The review covered:

- authentication and session management
- authorisation (roles, form rights, entity/cost-centre scoping)
- credential storage and password lifecycle (including reset)
- data protection in transit and at rest
- input handling (Excel imports, file attachments), CSRF and injection exposure
- auditability and monitoring
- configuration, secret handling and hosting posture

Two classes of item are distinguished throughout:

- **Remediated** — implemented in the code base as part of this review.
- **Open** — requires an infrastructure decision, a purchase, or a hosting change, and is
  therefore scheduled rather than implemented.

---

## 2. Executive summary

The application was already strong in **authorisation**: a global permission filter enforces
a configurable role/form matrix on every request, entity and cost-centre scoping is
re-validated inside each action, all data access is parameterised through EF Core (no SQL
injection exposure), and CSRF tokens were present on state-changing requests.

The weakness was concentrated in **credential handling and platform hardening**. Most
critically, passwords were stored and compared in clear text. That single issue would have
dominated any external assessment.

All critical and high findings that can be fixed in software have now been remediated. The
remaining open items are infrastructure and governance items (MFA rollout, hosting
migration, encryption at rest, penetration test, least-privilege database account).

| Severity | Findings | Remediated | Open |
|---|---|---|---|
| Critical | 4 | 4 | 0 |
| High | 5 | 4 | 1 |
| Medium | 5 | 3 | 2 |
| Governance / hosting | 4 | 0 | 4 |

---

## 3. Findings and remediation

### 3.1 Critical

**C-1. Passwords stored and compared in clear text**
*Risk:* anyone with read access to the database, a backup file or the hosting provider's
storage could read every user's password. Because users reuse passwords, the impact extended
beyond this system.
*Remediated.* Passwords are now stored as **salted PBKDF2-SHA256** hashes (210,000
iterations, 128-bit random salt per user, 256-bit derived key, constant-time comparison).
The hash format is self-describing (`PBKDF2-SHA256$iterations$salt$key`) so the work factor
can be increased later without invalidating existing credentials. The clear-text column is
emptied and is never written again. See `Services/PasswordHasher.cs`,
`Services/SecurityUpgrade.cs`.

**C-2. No protection against password guessing**
*Risk:* unlimited login attempts, no lockout, no throttling, and failed attempts were not
recorded anywhere.
*Remediated.* Three controls now apply: **account lockout** (5 failed attempts → 15 minutes),
**IP rate limiting** (10 requests/minute on sign-in, forgot-password and reset endpoints,
HTTP 429 beyond that), and **failed-login auditing** (action `LOGIN_FAILED` with reason,
client IP and `X-Forwarded-For`). An administrator can clear a lockout from the Users screen.

**C-3. Sessions could not be revoked**
*Risk:* deactivating a user, changing their role or scope, or resetting their password had no
effect until the browser cookie expired. A disabled account kept working.
*Remediated.* Each account carries a **security stamp**. The authentication cookie is
re-validated against the database (throttled to once every two minutes per session) and the
session is terminated when the account is deactivated, its role changes, or its password is
changed. Deactivation and rights changes rotate the stamp explicitly.

**C-4. Weak reset-token handling**
*Risk:* reset tokens were stored in clear text, links were valid for 7 days, and a request row
with no expiry was treated as **valid forever**.
*Remediated.* Only the **SHA-256 digest** of a token is stored. A link is valid for
**60 minutes**, is **single use**, is invalidated when a newer link is issued for the same
user, and a row without an expiry is never accepted. Legacy clear-text tokens are invalidated
by the upgrade routine.

### 3.2 High

**H-1. Cookie and session hardening absent**
*Remediated.* The authentication cookie is now `HttpOnly`, `Secure` (HTTPS only),
`SameSite=Lax`, renamed, with a 60-minute sliding expiry. The session cookie and the
antiforgery cookie carry the same flags.

**H-2. Data-protection keys not persisted**
*Risk:* every application restart invalidated all authentication cookies and antiforgery
tokens, and prevented safe scale-out.
*Remediated.* Keys are persisted to `App_Data/keys` with a fixed application name.
*Residual (open):* the key ring should additionally be encrypted with a certificate or held
in a managed key vault once the hosting platform supports it.

**H-3. Weak password policy**
*Risk:* six-character minimum, no complexity, no first-login change.
*Remediated.* A single shared policy (`Utils/PasswordPolicy.cs`) requires **12+ characters**
and **three of four character classes**, rejects passwords containing the username and a
deny-list of common patterns, and rejects reuse of the current password. Administrator-issued
passwords set a **must-change flag**, and a middleware confines such users to the
change-password page until they set their own password.

**H-4. No HTTP security headers; wildcard allowed hosts**
*Remediated.* A headers middleware sets `Content-Security-Policy` (configurable via
`Security:ContentSecurityPolicy`), `X-Content-Type-Options`, `X-Frame-Options: DENY`,
`Referrer-Policy`, `Permissions-Policy`, `Cross-Origin-Opener-Policy` and `no-store` on
authenticated pages. `AllowedHosts` is now an explicit list, which also protects the
host-derived reset link from host-header spoofing. HSTS and HTTPS redirection were already
enabled in production.
*Follow-up:* the CSP still permits `'unsafe-inline'` for scripts because several views use
inline script blocks; moving to per-request nonces is a scheduled clean-up.

**H-5. Application database account owns the schema (open)**
*Risk:* the runtime login is a schema owner and the application executes DDL at startup, so
any SQL-level compromise becomes full schema control.
*Recommendation:* give the runtime login `db_datareader` + `db_datawriter` + execute only, and
run schema changes as a separate migration account during release. The provided script
`docs/SecurityHardening_Schema.sql` exists precisely so the schema step can be run
out-of-band by a privileged account.

### 3.3 Medium

**M-1. Global CSRF enforcement**
*Remediated.* CSRF validation is now applied by default to every unsafe HTTP verb
(`AutoValidateAntiforgeryTokenAttribute`), instead of depending on each action carrying an
attribute.

**M-2. Internal error details returned to the browser**
*Remediated (representative case fixed).* Database exception text is no longer echoed to
users; a generic message is shown and the detail is logged.
*Follow-up:* a sweep of remaining `catch` blocks that surface exception text.

**M-3. Audit coverage gaps**
*Remediated in part.* The audit trail now includes failed sign-ins with IP address,
successful sign-ins with IP address, password changes, administrator-issued passwords, reset
links issued, lockout clearance and user deactivation.
*Open:* auditing of report/salary-data exports.

**M-4. Upload abuse surface (open)**
Excel imports are parsed fully in memory with no explicit request-size cap. CAPEX attachments
are restricted by extension allow-list but not by size or content type.
*Recommendation:* a request-size limit on import endpoints, a per-file size cap, MIME
verification, and `Content-Disposition: attachment` on download (the `nosniff` header is now
in place).

**M-5. No multi-factor authentication (open)**
*Recommendation:* TOTP or OTP as a second factor, mandatory for `SYSADMIN`/`ADMIN`, optional
for other roles. The security-stamp mechanism added in C-3 provides the hook required to
enforce it per session.

### 3.4 Governance and hosting (open)

**G-1. Database credentials in the repository.** The connection string is present in
`appsettings.json` and therefore in version history. **Action required:** rotate the SQL
password, and supply the connection string from an environment variable, user-secrets or a
key vault (the application already prefers environment configuration over the file).
`TrustServerCertificate=True` should become `False` against a valid certificate; encryption
itself is already enabled.

**G-2. Encryption at rest.** `core.HrEmployeeCosts` holds employee-level salary data.
Transparent Data Encryption (TDE) plus documented, tested backups should be requested from
the hosting provider.

**G-3. Hosting and data residency.** The database is on a shared commercial host. For a
government entity, hosting should move to the approved government cloud with WAF, DDoS
protection and UAE data residency.

**G-4. Assurance activities.** No external penetration test, no dependency vulnerability
scanning in the build, and no documented control mapping. Recommend an annual penetration
test, automated dependency scanning, and a mapping of these controls to the UAE Information
Assurance Standards / ISO 27001.

---

## 4. Controls that were already in place

These are testable today and should be presented as existing strengths:

- **Role-based and form-level authorisation** enforced by a global filter, not by the UI:
  a view-only role cannot add, edit or delete even by posting directly to an action.
- **Entity and cost-centre scoping** re-validated server-side in each controller, so a
  cost-centre user cannot read or write another entity's budget.
- **No SQL injection exposure**: all user-facing data access uses parameterised EF Core
  queries; raw SQL is limited to static, development-time schema statements.
- **CSRF tokens** on state-changing requests (now enforced globally).
- **HTTPS redirection and HSTS** in production.
- **Open-redirect protection** on the login return URL (`Url.IsLocalUrl`).
- **No username enumeration** on the forgot-password page (identical response either way);
  the sign-in page now also returns an identical message for unknown, inactive and
  wrong-password cases.
- **Append-only audit log** plus explicit confirmation screens before any destructive bulk
  data replacement.

---

## 5. What happened to the existing passwords

No user is locked out and no password needs to be re-issued.

1. On the first start of the hardened build, the application adds the new columns and reads
   every row that still holds a clear-text password.
2. Each one is hashed with PBKDF2-SHA256 and written to `PasswordHash`; the clear-text
   `Password` column is set to `NULL`.
3. A single audit entry records how many credentials were converted.
4. Users continue to sign in with **exactly the same password as before** — only the storage
   format changed.
5. Any account that is somehow missed (for example added directly in SQL while the upgrade
   ran) is converted transparently at its next successful sign-in.

Verification query after the first start:

```sql
SELECT COUNT(*) AS ClearTextPasswordsRemaining
  FROM core.AppUsers
 WHERE Password IS NOT NULL AND Password <> '';   -- must return 0
```

Because hashes are one-way, **nobody — including a system administrator or a database
administrator — can read a user's password any more.** Recovery is by reset only. That is the
intended behaviour and is what an assessor will expect to see.

---

## 6. Password reset — the three supported routes

**Route 1 — user forgets their password (self-service request).**
The user clicks *Forgot password?* on the sign-in page and submits their username. A request
is recorded with status *Pending*. The page always shows the same confirmation, so the form
cannot be used to discover valid usernames. An administrator then opens
**Admin Room → Password Reset Requests**, clicks *Generate Link*, and sends the link to the
user. The link is valid for 60 minutes, works once, and supersedes any earlier link. The user
sets a password that satisfies the policy; all other pending links for that account are
burnt, and every other active session for that account is terminated.

**Route 2 — administrator issues a reset link directly.**
**Users → Reset Password** on any user row generates the same kind of one-time, 60-minute
link without waiting for a request. Used when a user calls the help desk.

**Route 3 — administrator sets a temporary password.**
**Users → Edit** allows a password to be typed in. It is stored hashed, must satisfy the full
policy, and the account is flagged **must change at next sign-in**: the user is confined to
the change-password page until they choose their own password. This keeps the administrator
from knowing a working credential.

Supporting controls on these screens:

- A locked-out account shows a **Locked** badge with an **Unlock** action for administrators.
- Accounts awaiting a first-time change show a **Change required** badge.
- User details show when the password was last changed and the last sign-in time, and state
  explicitly that the password is stored as a hash and is not readable.
- Every step above writes an audit entry naming the administrator who performed it.
- Any signed-in user can change their own password at any time from
  **Change my password** in the sidebar.

*Recommended follow-up:* replace manual link distribution with direct SMTP delivery to the
user, so the administrator never handles the reset secret. The delivery interface
(`IPasswordResetNotifier`) already exists; only an SMTP implementation is required.

---

## 7. Deployment steps for this release

1. **Run** `docs/SecurityHardening_Schema.sql` against the GovBudget database (optional if the
   runtime account may execute DDL — the application applies the same statements at startup).
2. **Rebuild and publish** the application.
3. **Confirm** on first start: the log contains
   *"Security upgrade: converted N clear-text password(s) to PBKDF2 hashes."*
4. **Verify** with the query in section 5 that no clear-text password remains.
5. **Confirm** `App_Data/keys` is writable by the application pool identity (otherwise a
   warning is logged and sessions will not survive restarts).
6. **Check** that the deployed hostname appears in `AllowedHosts` in `appsettings.json`.
7. **Rotate** the SQL password and move the connection string out of `appsettings.json` into
   environment configuration.
8. **Test**: sign in with an existing account; five wrong passwords trigger the lockout;
   *Forgot password* → *Generate Link* → set a new password; a created user is forced to
   change the password at first sign-in.

---

## 8. Recommended roadmap after this release

| Priority | Item | Owner | Effort |
|---|---|---|---|
| 1 | Rotate SQL credentials; move the connection string to environment/vault; purge from git history | DoF IT | 1 day |
| 2 | Least-privilege runtime database login; DDL only via release account | DoF IT + DBA | 2 days |
| 3 | SMTP delivery of reset links | Development | 2 days |
| 4 | MFA for ADMIN and SYSADMIN | Development | 1 week |
| 5 | Upload size/MIME limits; export auditing; remaining generic error messages | Development | 3 days |
| 6 | TDE, tested backups, restore evidence | Hosting provider | Provider-led |
| 7 | CSP without `'unsafe-inline'` (script nonces) | Development | 3 days |
| 8 | External penetration test and remediation; dependency scanning in the build | DoF IT + vendor | 4 weeks |
| 9 | Migration to the approved government cloud (WAF, DDoS, UAE residency) | DoF IT + EGA | Programme |
| 10 | Control mapping to UAE IA Standards / ISO 27001 with evidence pack | DoF IT | 2 weeks |

---

## 9. Code references

| Area | File |
|---|---|
| Password hashing, token digests, security stamps | `Services/PasswordHasher.cs` |
| Password policy | `Utils/PasswordPolicy.cs` |
| Startup hash migration and schema upgrade | `Services/SecurityUpgrade.cs` |
| Session revocation | `Services/CookieSecurityValidator.cs` |
| HTTP security headers | `Utils/SecurityHeaders.cs` |
| Forced password change | `Utils/ForcePasswordChange.cs` |
| Login, lockout, reset, change password | `Controllers/AccountController.cs` |
| Administrator user management, unlock, reset links | `Controllers/AppUsersController.cs` |
| Reset request queue | `Controllers/AdminController.cs` |
| Cookie/session/antiforgery/rate-limit configuration | `Program.cs` |
| Role and form-rights enforcement | `Utils/FormPermissionFilter.cs`, `Services/PermissionService.cs` |
| Database schema script | `docs/SecurityHardening_Schema.sql` |
