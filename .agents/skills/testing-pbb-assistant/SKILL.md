---
name: testing-pbb-assistant
description: How to run and verify the GovBudget (PBB) ASP.NET Core app and its in-app Budget Assistant chatbot end-to-end, including OECD SDMX answer verification and budget-vs-actual arithmetic checks.
---

# Testing the GovBudget (PBB) app and its Budget Assistant

## Running the app
- .NET SDK lives at `~/.dotnet` (`export PATH=$PATH:/home/ubuntu/.dotnet`).
- Build: `dotnet build` from the repo root. Run: `dotnet run --no-build --urls http://localhost:5014`.
- The assistant needs `OPENAI_API_KEY` (or `Assistant:ApiKey`) in the process environment,
  otherwise the widget replies "not configured". Bind it with the exec tool's `env` parameter
  (`secret:session:OPENAI_API_KEY`) — env vars only take effect for a *newly started* shell,
  so start the app in the same call that binds the secret.
- Login: `/Account/Login`, user `RDAM`, password from `PBB_TEST_PASSWORD`. That user is entity
  1002 "Antiquities and Museums", FY 2026.

## Login may be broken by DB drift (shared database)
The SQL Server `db_ac6910_govbudget` is **shared across branches**. The security-hardening work
added `core.AppUsers.PasswordHash` and set the legacy plaintext `Password` to NULL for every user.
Branches whose `AccountController` still compares `u.Password` (non-nullable mapping) then fail
login — either `SqlNullValueException` or a plain "Invalid username or password."

Check first, and only work around it with the lead's/user's explicit approval:

```bash
python3 -c "
import pymssql
c=pymssql.connect(server='SQL5110.site4now.net',user='db_ac6910_govbudget_admin',
                  password='<from appsettings.json DefaultConnection>',database='db_ac6910_govbudget')
cur=c.cursor(); cur.execute(\"SELECT UserName,Password,IsActive FROM core.AppUsers WHERE UserName='RDAM'\")
print(cur.fetchone())"
```

If approved, set a temporary password for **RDAM only** and restore it to NULL when done (do this
even if the run fails or is interrupted); touch no other row or column. Use an **alphanumeric**
value — a password containing `!`, `#`, `@` etc. was written correctly to the DB but still failed
to log in through the browser (special characters mistype via synthetic keystrokes), which wastes
a lot of time looking like a data problem. `TmpTest9a72` worked; `Tmp!Test#9a72` did not.
Cleanup: `UPDATE core.AppUsers SET Password=NULL WHERE UserName='RDAM'`, then assert
`SELECT COUNT(*) FROM core.AppUsers WHERE Password IS NOT NULL` is 0.

## ALWAYS confirm you are testing the build you think you are
Kestrel fails to bind if a previous `dotnet run` still owns port 5014, and `nohup dotnet run &`
will silently keep the *old* process serving traffic while the new log file only contains an
`IOException: address already in use` stack trace. Before trusting any result:

```bash
pgrep -af "dotnet run"                 # expect exactly one, started after your rebuild
grep -c "Now listening" /tmp/<log>     # must be 1 in the new log
grep -c "already in use" /tmp/<log>    # must be 0
```

Kill stale instances by PID (`kill <pid>`) and re-check `curl -s -o /dev/null -w "%{http_code}"
http://localhost:5014/Account/Login` returns 200 from the new log's process.

## Reaching the assistant
Floating robot button at the bottom-right of any authenticated page (`Views/Shared/_ChatWidget.cshtml`,
requires the ASSISTANT form right) → panel titled "Budget Assistant" → text box "Ask a question…".
The circular-arrow icon in the panel header is Reset.

## Verifying assistant answers (the whole point)
The assistant is an LLM, so *never* accept a figure because it looks plausible:
- **Budget figures**: cross-check against the app's own screens — Executive Summary (`/`) cards and
  `/Actuals/BudgetVsActual` (GL/Category tab, has a GRAND TOTAL row). Budget-line tools exclude HR
  staff cost (~11.3M of a 20.26M total for RDAM/2026), so a budget-vs-actual answer legitimately
  totals ~8.93M and must carry the server-appended HR-exclusion note.
- **Table integrity**: parse the rendered DOM/HTML rather than eyeballing. Save the page HTML and
  check every `<tr>` has the same cell count as the header and that `Budget − Actual = Variance`
  and `Actual ÷ Budget × 100 = Execution Rate` for every row. Past regressions included a shifted
  GL-code column and a single mis-typed digit in one variance.
- **OECD/live SDMX answers**: re-fetch the URL the answer cites with `curl` and confirm the quoted
  number exists verbatim, e.g.

  ```bash
  curl -s "https://sdmx.oecd.org/public/rest/data/OECD.GOV.GIP,DSD_GOV@DF_GOV_PF_2023,1.0/all?format=jsondata&dimensionAtObservation=AllDimensions&startPeriod=2023&endPeriod=2023" \
    | python3 -c "…flatten data.dataSets[0].observations against data.structures[0].dimensions.observation…"
  ```

  Check `UNIT_MEASURE`: a percentage-of-GDP claim is only valid against a percentage unit
  (`PT_B1GQ`, `PT_PB1GQ`); `XDC` means national currency, so a % quoted from it is fabricated.
  `OECD.GOV.GIP,DSD_GOV@DF_GOV_PF_2023,1.0` is a known-good flow that returns ~74 observations.
  The tool caps parsed observations at 120, so an honest "no data" answer may just mean the first
  120 rows were empty — verify before reporting it as an upstream outage.

## Cross-checking activity / line-item answers against SQL
Schema gotchas that cost real time (verify with `INFORMATION_SCHEMA` before assuming):
- Everything is in the **`core`** schema, not `budget`.
- The fiscal-year column is **`BudgetYear`**, not `Year`.
- HR allocation amount is `core.HrEmployeeCostAllocations.**AllocatedAmount**` (not `AllocatedCost`);
  its year comes from the parent `core.HrEmployeeCosts.BudgetYear`.
- `core.Activities` has **no `EntityId`** — scope goes through the activity's department:
  `Activities.DepartmentId → Departments.EntityId`.
- GL fields are reached via `Items.GLAccountId → GLAccounts.GLCode/GLName`.

The strongest check for a line-item answer is an **exact multiset comparison**: scrape the amounts
out of the rendered assistant table, pull `SUM`/list of `BudgetLines.Amount` for the same
activity+category+year from SQL, and compare `sorted(ui) == sorted(db)` as well as counts and sums.
That catches a dropped/duplicated row that a matching grand total would hide.

Useful RDAM/FY2026 fixtures: activity **DAM-04.A01** has OPEX 1,014,204.00 (11 lines),
CAPEX 129,100.00 (9 lines), HR 1,267,807.93, total 2,411,111.93 — all visible on
`Reports?report=activities&year=2026&entityId=1002`. Entity-wide decoys that must never be
presented as one activity's figures: OPEX 5,176,879.85, HR 11,334,303.74, total 20,260,011.85.
For resolution tests, `A01` is ambiguous (7 matches, DAM-01.A01…DAM-07.A01) and `DAM-99.A09`
matches nothing.

## Known-noisy log entries
`/tmp/<log>` normally contains a benign `HttpsRedirectionMiddleware` warning. Large SDMX flows can
exceed the 60s HttpClient timeout and log a handled `TaskCanceledException` from
`OecdKnowledgeToolProvider.QueryOecdDataAsync` — the app recovers, but note it. There is no
request-level logging for `/Chat/Ask`, so HTTP status codes / antiforgery failures can only be
checked indirectly (an answer rendering at all means the token was accepted).

## Devin Secrets Needed
- `OPENAI_API_KEY` (also `secret:repo:odehanas/PBB:OPENAI_API_KEY`)
- `PBB_TEST_PASSWORD`
