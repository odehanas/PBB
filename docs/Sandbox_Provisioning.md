# GovBudget — Sandbox Database Provisioning

How to build a GovBudget database in a new environment (EGA sandbox, or any
fresh SQL Server). Two routes: **A — clean database with synthetic sample data**,
or **B — anonymised copy of the live database**.

Companion documents: `docs/Hosting_Requirements_EGA.md` (infrastructure) and
`docs/DEPLOY.md` (releases).

---

## Route A — clean database + synthetic sample data (recommended for a trial)

Nothing real leaves the current environment, so there is no data-classification
question to settle before testing can start.

| # | Step | Script / action |
|---|---|---|
| 1 | Create an empty database **with the production collation** | `CREATE DATABASE GovBudget_SBX COLLATE Latin1_General_CI_AS_KS_WS;` — the live database does **not** use the server default `SQL_Latin1_General_CP1_CI_AS`, so omitting this produces a sandbox that sorts and compares text differently from production. Confirm the exact value with section 7d of `docs/Schema_Inventory.sql`. |
| 2 | Create all tables, views and the 3 budget categories | `docs/LocalDatabase_FullSchema.sql` |
| 3 | Apply the security columns | `docs/SecurityHardening_Schema.sql` |
| 4 | Apply the remaining incremental scripts | `docs/AddKpiClassification.sql`, `docs/ActualsComparison_Schema.sql`, `docs/AddBudgetLineEntrySource.sql`, `docs/AddHrOccupation.sql` |
| 5 | Point the app at the database and **start it once** | Creates the role/permission tables and seeds the default permission matrix |
| 6 | Load the sample data | `docs/Sandbox_SampleData.sql` |
| 7 | Sign in as `admin` / `admin`, change the password immediately, then create the test users | Admin Room → User Management |

Every script is **idempotent** — each statement is guarded by an
`IF COL_LENGTH(...) IS NULL` / `IF NOT EXISTS (...)` check, so re-running any of
them is harmless. If a script reports "nothing to do", that is the expected
result on an up-to-date database.

### What the sample data gives you

One test entity (`SBX`), 2 departments, 3 programmes (2 mandate + 1 support so
cost reallocation can be demonstrated), 6 activities, a 6-account chart of
accounts, 13 budget lines across OPEX/CAPEX/REVENUE, 5 HR records with activity
allocations, prior-year actuals, mid-year actuals and forecast, 7 KPIs spanning
on-track and behind-target in both directions, activity outputs, and one
allocation driver plus rule left in **Draft** so a tester can run it.

All names and figures are invented.

---

## Route B — anonymised copy of the live database

Use when testers need the real programme and KPI structure.

| # | Step | Script / action |
|---|---|---|
| 1 | Back up the live database | `BACKUP DATABASE ... TO DISK = ...` |
| 2 | Restore it in the sandbox **under a name containing `SBX`, `SANDBOX`, `TEST`, `COPY` or `DEV`** | The anonymisation script refuses to run otherwise |
| 3 | Apply any missing incremental scripts (steps 3–4 of Route A) | Only needed if the copy predates a release |
| 4 | **Anonymise** | `docs/Sandbox_Anonymise.sql` |
| 5 | Start the app once, sign in as `sbxadmin` with the temporary password from the script, change it | The start-up upgrade converts the temporary password into a PBKDF2 hash |
| 6 | Verify with the queries at the end of the anonymisation script | Every "remaining" count must be 0 |

`Sandbox_Anonymise.sql` removes all password hashes and disables every account
except one administrator, replaces employee names and IDs, deletes all uploaded
attachments, clears internal messages, notes and narratives, and empties the
audit log and password-reset tokens. Two optional blocks (commented out) scale
every monetary value by a single factor, and rename the organisation layer.

**Be honest with EGA about the limits:** scaling amounts by one factor hides
absolute figures but preserves ratios. It is de-identification, not statistical
anonymisation. If EGA needs figures that cannot be reverse-engineered, use
Route A.

---

## Database account privileges

Steps 2–4 above are DDL and need `db_owner` (or a DBA session). After
provisioning, the **application** account can be reduced to
`db_datareader` + `db_datawriter` + `EXECUTE`, provided the schema is kept up to
date by script at each release. See section 4.1 of
`docs/Hosting_Requirements_EGA.md` for the two supported models.

---

## Verifying the result

```sql
-- Tables and views present (expect ~35 tables and 6 views)
SELECT type_desc, COUNT(*) FROM sys.objects
WHERE schema_id = SCHEMA_ID('core') AND type IN ('U','V')
GROUP BY type_desc;

-- The security columns arrived
SELECT COL_LENGTH('core.AppUsers','PasswordHash')     AS PasswordHash,
       COL_LENGTH('core.AppUsers','MustChangePassword') AS MustChangePassword;

-- The permission matrix was seeded by the app's first run
SELECT COUNT(*) FROM sys.tables WHERE schema_id = SCHEMA_ID('core') AND name LIKE '%Permission%';

-- No clear-text password survives
SELECT COUNT(*) AS ClearTextRemaining FROM core.AppUsers WHERE Password IS NOT NULL AND Password <> '';
```

Then run the functional checklist in section 13 of
`docs/Hosting_Requirements_EGA.md`.

---

## Known gaps to be aware of

- `docs/LocalDatabase_FullSchema.sql` contains a **reconstruction** of
  `core.vw_GL_CashBasis`; the original production definition was not in the
  repository. It matches the columns the app reads. If the live view differs,
  script it from the server (`Script As → CREATE`) and use that instead.
- The role/permission tables are created by the application at start-up, not by
  the schema script. This is why step 5 of Route A comes before the sample data.
