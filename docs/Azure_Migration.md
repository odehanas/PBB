# GovBudget — Hosting on Azure (or any other host)

Options for rebuilding the environment away from the current provider, and the
authoritative way to extract the database as it stands today.

Companion documents: `docs/Hosting_Requirements_EGA.md`,
`docs/Sandbox_Provisioning.md`, `docs/Schema_Inventory.sql`.

---

## 1. The short answer

The application is portable by design: one ASP.NET Core process, one SQL Server
database, no message broker, no file share, no scheduled jobs, attachments held
inside the database. Moving it is a configuration exercise, not a rewrite.

For the database, **do not rebuild from `docs/LocalDatabase_FullSchema.sql`
alone.** That script is maintained by hand, and the application also creates
objects at start-up (`core.AppRoles`, `core.RolePermissions`), so it is a close
approximation rather than a guaranteed match. The authoritative copy has to come
out of the live database. Section 4 explains how, and `docs/Schema_Inventory.sql`
measures the gap.

---

## 2. Azure target options

| | Compute | Database | Best for |
|---|---|---|---|
| **Option 1** *(recommended)* | **App Service**, Windows, .NET 10 | **Azure SQL Database** | Least infrastructure to run. No OS patching, TLS and certificates managed, backups and point-in-time restore built in. |
| **Option 2** | **Azure VM**, Windows Server + IIS | SQL Server on a VM, or Azure SQL | Closest to the current setup and to what EGA would provision. Full control, but you patch the OS and manage backups. |
| **Option 3** | **Container Apps** or App Service for Linux | Azure SQL Database | Most portable and cloud-neutral. Requires a Dockerfile, which does not exist yet. |

Indicative sandbox sizing — confirm against current Azure pricing:

| Resource | Sandbox / trial | Production |
|---|---|---|
| App Service plan | B1 (1 vCPU, 1.75 GB) | P0v3 or S1, scaled on observed load |
| Azure SQL Database | Basic or GP serverless, 2 vCore max | General Purpose, 2–4 vCore |
| Storage | Included | Included |

Both compute options need the same two things the application always needs: a
**writable folder for the data-protection key ring** and a **single instance**
(or sticky sessions), as covered in section 3.

---

## 3. What must change in the application for Azure

None of these are large, but each one causes a visible failure if missed.

### 3.1 TLS terminates at the platform — required change

App Service and Container Apps terminate TLS at the front end and forward the
request internally over HTTP. The application enforces HTTPS redirection, HSTS
and `Secure` cookies, and there is no forwarded-headers middleware today
(`@/c:/Users/anas/source/repos/GovBudget/Program.cs`). The symptom is a redirect
loop and rejected sign-in cookies.

The fix is the same one EGA may need: enable `ForwardedHeaders` for
`X-Forwarded-Proto` before the HTTPS redirection. This is the single most likely
cause of "the site does not work" on the first Azure deployment.

### 3.2 The key ring must stay writable — required check

The key ring is persisted to `App_Data\keys` under the content root. On App
Service that resolves inside `\home\site\wwwroot`, which is persistent — so it
works, **unless** the site is deployed with **run-from-package**
(`WEBSITE_RUN_FROM_PACKAGE=1`), which makes `wwwroot` read-only. In that case key
persistence fails silently and **every user is signed out on each restart**.

Options: deploy without run-from-package, point the key ring at `%HOME%\App_Data\keys`
outside `wwwroot`, or move to Azure Blob Storage with Key Vault protection —
which is also what a multi-instance deployment needs.

### 3.3 Session state and scale-out

Session is in-process. Run a single instance, or enable ARR affinity (on by
default in App Service), or move session to a distributed cache before scaling
out. Scaling out also requires the shared key ring from 3.2.

### 3.4 Connection string

Azure SQL presents a valid certificate, so drop `TrustServerCertificate=True`
and keep `Encrypt=True`. EF Core retry-on-failure is already enabled, which is
what Azure SQL's transient faults require.

### 3.5 Secrets

Move the connection string out of `appsettings.json` into **App Service
application settings** or **Key Vault references**. Note that the current
`appsettings.json` still holds live production credentials in clear text —
rotate that credential as part of any migration.

### 3.6 Time zone — check before go-live

Azure hosts run **UTC**. Several columns default to `SYSDATETIME()`, which is
server local time. Moving from a +04 host to UTC shifts newly created timestamps
by four hours relative to existing rows. Decide whether to normalise those
defaults to `SYSUTCDATETIME()` or set `WEBSITE_TIME_ZONE` on App Service to keep
current behaviour.

### 3.7 Database collation — confirmed non-default

The live database uses **`Latin1_General_CI_AS_KS_WS`**, not the
`SQL_Latin1_General_CP1_CI_AS` that Azure SQL and a fresh SQL Server install
create by default. This surfaced as a collation-conflict error while running
`docs/Schema_Inventory.sql` against production.

Consequence: **a target database created by hand will not match production.**
Sort order, uniqueness of text keys and comparisons of Arabic text can all behave
differently. Two ways to get it right:

- Import a `.bacpac` — the collation is carried inside the package. Preferred.
- Or create the database explicitly:
  `CREATE DATABASE GovBudget COLLATE Latin1_General_CI_AS_KS_WS;` before running
  any schema script.

Confirm the exact value from section 7d of `docs/Schema_Inventory.sql` and record
it in the environment definition given to EGA.

### 3.8 Azure SQL differences

No `BACKUP DATABASE` — use automated backups and point-in-time restore. No
cross-database queries. No SQL Agent — not used today. Section 7 of
`docs/Schema_Inventory.sql` checks the schema for anything Azure SQL rejects;
based on the repository schema it should come back clean.

---

## 4. Extracting the database as it is now

Everything below runs from SSMS or Azure Data Studio on your own machine against
the current host — no server access needed beyond the SQL login you already have.

### Method 1 — BACPAC: schema **and** data, and the direct route into Azure

Use this to stand up an identical database anywhere.

1. SSMS → connect to the live server → right-click the database.
2. **Tasks → Export Data-tier Application…**
3. Save as `GovBudget_YYYY-MM-DD.bacpac`.
4. To restore: in Azure, create a SQL Server, then **Import database** and upload
   the `.bacpac`. On a local or EGA SQL Server: SSMS → right-click **Databases**
   → **Import Data-tier Application…**

SSMS also has **Tasks → Deploy Database to Microsoft Azure SQL Database**, which
performs the export and import in one pass.

Two cautions: the export fails if the schema changes while it runs, so do it
outside working hours; and the `.bacpac` contains all production data, so treat
the file as confidential and delete it when finished.

### Method 2 — DACPAC: schema only

Use this for review, for handing a schema to EGA, or to rebuild an empty
database with no data.

1. SSMS → right-click the database → **Tasks → Extract Data-tier Application…**
2. Choose **schema only**.

### Method 3 — a single `.sql` script

Use this when a readable, reviewable file is wanted — the most likely choice if
EGA's DBA is to inspect it.

1. SSMS → right-click the database → **Tasks → Generate Scripts…**
2. Select **entire database and all database objects**.
3. **Advanced** → set *Types of data to script* to **Schema only**, and set
   *Script for Server Version* to the target version. Turn on
   *Script Indexes*, *Script Triggers*, *Script Primary Keys*, *Script Foreign Keys*.
4. Save to a single file.

### Command-line equivalent, for a DBA

PowerShell is blocked by group policy on this workstation, so these must be run
from `cmd.exe`, another machine, or by the DBA:

```
:: Schema + data
sqlpackage /Action:Export /ssn:SQL5110.site4now.net /sdn:<database> ^
  /su:<user> /sp:<password> /tf:C:\temp\GovBudget.bacpac

:: Schema only
sqlpackage /Action:Extract /ssn:SQL5110.site4now.net /sdn:<database> ^
  /su:<user> /sp:<password> /tf:C:\temp\GovBudget.dacpac ^
  /p:ExtractAllTableData=false

:: Publish to Azure SQL
sqlpackage /Action:Publish /sf:C:\temp\GovBudget.dacpac ^
  /tsn:<yourserver>.database.windows.net /tdn:GovBudget /tu:<user> /tp:<password>
```

---

## 5. Recommended sequence

1. Run `docs/Schema_Inventory.sql` against the live database and save the results.
   Section 2 shows any drift between the live database and the repository script;
   section 6 gives the **real view definitions**, including the one the
   repository only approximates.
2. Feed those results back into `docs/LocalDatabase_FullSchema.sql` so the
   repository stops drifting. This is worth doing regardless of Azure.
3. Rotate the SQL credential and remove it from `appsettings.json`.
4. Take a BACPAC (Method 1) as the migration artefact and a DACPAC (Method 2) as
   the reviewable schema.
5. Provision the Azure resources, or hand the DACPAC to EGA.
6. Apply the changes in section 3 — forwarded headers first, then the key-ring
   path, then the connection string and secrets.
7. Import the BACPAC, point the application at the new database, start it once,
   and check the start-up log.
8. Run the 8-step acceptance checklist in `docs/Hosting_Requirements_EGA.md`.

---

## 6. Which to choose

If the objective is EGA hosting, Azure is a parallel option worth keeping warm
rather than a replacement — the same extraction artefacts serve both, so nothing
is wasted.

If the objective is to stop depending on the current shared host quickly,
**Option 1 with an Azure SQL Database** is the fastest route to a supported,
backed-up environment, and it removes the single-server backup risk that exists
today. The work is roughly a day: the section 3 changes, then provision, import
and verify.
