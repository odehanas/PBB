# GovBudget — Deployment Guide (SmarterASP.NET)

The deploy loop is **one click**: Visual Studio → *Publish*. No folder publish, no zip,
no unzip on the server, and nothing to repair by hand afterwards.

This guide exists because the manual zip loop kept destroying server-only files. The
publish profiles in `Properties/PublishProfiles/` now prevent that structurally.

---

## 1. The rule that makes redeploying safe

Some files live **only on the server** and must survive every deployment. They are not in
the repository (they hold secrets or runtime state), so any deploy that mirrors the folder
wipes them — that is the "information left to fix manually" problem.

| Server-only file / folder     | Holds                                              | If it is overwritten |
|-------------------------------|----------------------------------------------------|----------------------|
| `appsettings.Production.json` | OpenAI API key, live connection string overrides    | Assistant reports "not configured"; DB login may fail |
| `App_Data/keys/`              | Data-protection key ring (auth cookies, antiforgery)| Every signed-in user is logged out at once |
| `logs/`                       | stdout diagnostics                                  | Loses start-up failure evidence |
| `web.config`                  | ASP.NET Core Module settings, `ASPNETCORE_ENVIRONMENT` | Now safe: it is versioned in the repo and only transformed by publish |

`SmarterASP.pubxml` protects all of these three ways at once: they are excluded from the
payload, msdeploy **skip rules** block add/update/delete on the destination, and
`SkipExtraFilesOnServer=true` means a deploy never deletes anything on the server.

---

## 2. What actually needs deploying

- **Razor views (`.cshtml`)** are **compiled into the DLLs**. Runtime compilation is off,
  so uploading a `.cshtml` on its own changes nothing — a view edit needs rebuild + deploy.
- **`wwwroot/` static files** are served as-is; they need upload but no rebuild.

A full publish covers both. Never hand-pick files.

---

## 3. One-time setup — **already done**

The server values are filled in. GovBudget is **site6** (`odehanas-001-site6`) on
`win8222.site4now.net`; its FTP root is `/GovBudget`.

Only the password is still needed, and it is asked for once:

> Visual Studio → right-click the project → **Publish** → select the **SmarterASP**
> profile → **Publish**. Enter the hosting password when prompted and tick
> **Save password**.

The password goes into `SmarterASP.pubxml.user`, which git ignores. **Never** type a
password into the `.pubxml` itself — that file is committed.

If Web Deploy is ever unavailable, `SmarterASP-FTP.pubxml` is configured as a fallback;
read the caveats in its header.

If the host moves the site to a different `win####.site4now.net` server, re-download the
`.PublishSettings` from the control panel and update `MSDeployServiceURL`,
`DeployIisAppPath` and `UserName` in `SmarterASP.pubxml`.

### What replaced the old zip loop

The previous process — delete the site folder, upload a published zip — is what destroyed
the server-only files listed in section 1 and forced `App_Data`, `App_Data/keys`, `logs`
and `web.config` to be rebuilt by hand every time. It also wiped the data-protection key
ring on every deploy, signing every logged-in user out. None of that happens now:
`SkipExtraFilesOnServer=true` means a deploy never deletes anything on the server.

---

## 4. Every deploy after that

Visual Studio → **Publish** → **Publish**. Or, scriptable, from a terminal:

```
dotnet publish -c Release /p:PublishProfile=SmarterASP /p:Password=THEPASSWORD
```

Web Deploy uploads only changed files, drops `app_offline.htm` for the duration so the
DLLs are never locked, and removes it when finished. The app pool recycles by itself.

After deploying: **Ctrl+F5** in the browser, so changed `wwwroot` CSS/JS is not served
from cache, then smoke-test the screen you changed.

---

## 5. Database changes

Most schema work is automatic. `Program.cs` runs idempotent upgrades at start-up, so a
deploy is enough:

- `SecurityUpgrade` — security columns on `core.AppUsers`, password hashing backfill.
- `AllocationScenarioUpgrade` — `ScenarioName` on `AllocationRuns`.

Scripts under `docs/*.sql` are **manual, run once** in SQL Server Management Studio
against the live database — for example `AddKpiClassification.sql`. They all guard with
`COL_LENGTH` checks, so re-running them is harmless.

---

## 6. Troubleshooting

**A config change had no effect.**
Configuration is read once at start-up and `IOptions<T>` is cached for the life of the
process. A browser refresh changes nothing — the app pool must recycle. Force it by
saving `web.config` unchanged in the server's File Manager, or by creating then deleting
`app_offline.htm`.

**"The assistant is not configured yet."**
`appsettings.Production.json` in the site root (next to `GovBudget.dll`) must contain the
section shape, then the app must be restarted:

```json
{ "Assistant": { "ApiKey": "sk-..." } }
```

`OPENAI_API_KEY` only works as a real environment variable, never as a JSON key.

**HTTP 500.30 after a deploy.**
Start-up crash. Set `stdoutLogEnabled="true"` in `web.config`, reproduce, read
`logs/stdout_*.log`, then set it back to `false` (the file grows without limit). Usual
causes: malformed `appsettings.Production.json`, or the database being unreachable.

**"File in use" / locked `GovBudget.dll`.**
Only happens over FTP. Upload an `app_offline.htm` to the site root, publish, delete it.

**Removing a stale file.**
Because `SkipExtraFilesOnServer=true` never deletes, a file dropped from the project stays
on the server. Delete it by hand in the File Manager. This trade is deliberate: silent
deletion is what used to break the live site.

---

## 7. Where the connection string lives

`appsettings.json` is committed to git, so **the live connection string is not in it**. It
ships with an empty `DefaultConnection` placeholder and the app refuses to start if nothing
supplies a real one — deliberately, with a message naming both options.

| Environment | Source | Notes |
|-------------|--------|-------|
| Local development | **User secrets** | `%APPDATA%\Microsoft\UserSecrets\govbudget-9c2e-4f7a-connectionstrings\secrets.json` — outside the repo, so it cannot be committed. Loaded automatically because `ASPNETCORE_ENVIRONMENT=Development`. |
| Server | **`appsettings.Production.json`** | Site root, next to `GovBudget.dll`. Never deployed (skip rule + exclusion), never in git. |

Setting it locally:

```
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Data Source=...;Password=...;"
```

The server file holds both the connection string and the assistant key:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Data Source=SQL5110.site4now.net;Initial Catalog=db_ac6910_govbudget;User Id=db_ac6910_govbudget_admin;Password=THE_PASSWORD;Encrypt=True;TrustServerCertificate=True;"
  },
  "Assistant": { "ApiKey": "sk-..." }
}
```

Configuration is read once at start-up, so the app pool must recycle after editing it —
see section 6.

> **Order matters.** `appsettings.Production.json` must exist on the server *before* the
> first deploy that carries the empty placeholder, or the site returns HTTP 500.30 on
> start-up. Production config overrides `appsettings.json`, so adding the server file
> early is harmless and can be done at any time beforehand.

---

## 8. `deploy-update.cmd`

A robocopy-based incremental deploy for a site folder reachable as a **local or UNC path**
(on-premise IIS, or a mapped drive). It is not usable against SmarterASP, which exposes
only FTP and Web Deploy. It protects the same server-only files and is kept for the
eventual move to government-hosted infrastructure.
