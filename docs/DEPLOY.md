# GovBudget — Deployment Guide (SmarterASP.NET)

How to publish updates to the live site. The goal is to replace the slow loop
(publish to folder -> zip -> upload -> unzip on server -> run) with a one-click
**Publish** from Visual Studio.

---

## What gets deployed

GovBudget is an ASP.NET Core (`net10.0`) MVC app. Two kinds of files matter:

- **Razor views (`.cshtml`)** — these are **compiled into the app DLLs** at build
  time (no runtime compilation is enabled). Editing a `.cshtml` requires a
  **rebuild + redeploy**; uploading the `.cshtml` alone does nothing on the server.
- **Static files under `wwwroot/`** (e.g. `wwwroot/js/tour.js`, `wwwroot/css/tour.css`)
  — served as-is. They must be uploaded, but need no rebuild.

A full **Publish** handles both at once, so always prefer it over hand-picking files.

---

## Option A — Web Deploy (recommended: one click, only changed files)

Pushes DLLs **and** `wwwroot` together, uploading only what changed.

### One-time setup
1. Log in to the **SmarterASP.NET control panel**.
2. Open your site → find **Auto Deploy / Web Deploy** (often under
   *Websites → Manage → Publish Settings*) and **download the `.PublishSettings`** file.
3. Visual Studio → right-click the project → **Publish** → **Import Profile** →
   select the downloaded `.PublishSettings`.
4. If it warns about a certificate, open the profile's advanced settings and set
   **Allow untrusted certificate = true**.
5. Click **Publish** once to validate.

### Every future deploy
- Just click **Publish**. No zip, no manual unzip.

---

## Option B — FTP publish profile (no zip, uploads all files)

Use if Web Deploy is unavailable on your plan.

1. In the SmarterASP control panel, get your **FTP host, username, password**.
2. Visual Studio → **Publish → New → FTP**; enter credentials.
3. Set **Site Path** to your web root (often `/` or the site's `wwwroot`,
   depending on the SmarterASP layout).
4. Click **Publish** to upload directly over FTP.

FTP re-uploads everything each time (slower than Web Deploy's incremental), but
still removes the zip/unzip steps.

---

## Avoiding "file in use / locked DLL" errors

When overwriting a running .NET app, DLLs may be locked. Standard fix:

- Place a file named **`app_offline.htm`** in the site root **before** deploying.
  ASP.NET Core stops the app and serves that page; after deploy, **remove it** to
  bring the app back.
- **Web Deploy does this automatically.** With **FTP**, you may need to add/remove
  `app_offline.htm` manually if you hit a lock.

A simple `app_offline.htm` example:

```html
<!doctype html>
<html><head><meta charset="utf-8"><title>Maintenance</title></head>
<body style="font-family:Segoe UI,Arial,sans-serif;text-align:center;padding:60px">
  <h2>GovBudget is being updated</h2>
  <p>The system will be back in a few minutes.</p>
</body></html>
```

---

## After every deploy

1. The app pool recycles automatically.
2. Do a browser **hard refresh** (Ctrl+F5) so updated static files
   (`tour.js`, `tour.css`, CSS) are not served from cache.
3. Smoke-test the changed screen (e.g. open **Budget Entry** and click
   **Take a tour**).

---

## Quick comparison

| Step                | Current (zip) | Web Deploy | FTP   |
|---------------------|---------------|------------|-------|
| Build / publish     | manual        | automatic  | automatic |
| Zip                 | yes           | none       | none  |
| Upload              | manual        | 1 click    | 1 click |
| Unzip on server     | manual        | none       | none  |
| Only changed files  | no            | yes        | no    |

**Recommendation:** set up **Web Deploy** once; afterwards every change is a single
**Publish** click that ships both the recompiled views and the `wwwroot` assets.

---

## Azure (IDO sandbox) — required server configuration

`appsettings.json` deliberately holds **no secrets and no environment-specific values**.
On the VM (`vm-govbudget-app`), set these as **environment variables on the IIS app pool**
(IIS Manager → Configuration Editor → `system.applicationHost/applicationPools` →
`environmentVariables`) or in the published `web.config` under
`<aspNetCore><environmentVariables>`. ASP.NET Core maps `__` to `:`.

| Variable | Example value | Why |
|---|---|---|
| `ConnectionStrings__DefaultConnection` | `Server=tcp:sql-govbudget-sandbox.database.windows.net,1433;Database=govbudget-sandbox-db;Authentication=Active Directory Managed Identity;Encrypt=True;TrustServerCertificate=False;` | Database. Use the VM's managed identity (no password). Never `TrustServerCertificate=True` on Azure SQL. |
| `AllowedHosts` | `govbudget.finance.rak.ae` | Requests with any other `Host` header get HTTP 400. Must match the DNS name users type. |
| `ForwardedHeaders__KnownNetworks` | `10.92.0.0/24` | The `subnet-appgw` range. Without it every user shares the App Gateway's IP, so the sign-in rate limit (10/min) is shared by everyone and the audit log records the gateway, not the user. |
| `DataProtection__KeyPath` | `D:\GovBudget\keys` | Outside the site folder so a Web Deploy cannot delete the keys. Grant the app pool identity **Modify** on this folder. |
| `Assistant__Enabled` | `false` | The assistant calls `api.openai.com` (data leaves the UAE) and the VNet has no outbound internet. Keep off until an approved endpoint (e.g. Azure OpenAI, UAE North) is agreed. |
| `Assistant__OecdLiveEnabled` | `false` | Calls `sdmx.oecd.org`; same outbound restriction. |

### Managed identity for Azure SQL
1. Enable the **system-assigned managed identity** on `vm-govbudget-app`.
2. Set a Microsoft Entra admin on `sql-govbudget-sandbox`, connect as that admin and run:
   ```sql
   CREATE USER [vm-govbudget-app] FROM EXTERNAL PROVIDER;
   ALTER ROLE db_datareader ADD MEMBER [vm-govbudget-app];
   ALTER ROLE db_datawriter ADD MEMBER [vm-govbudget-app];
   -- Temporary: the app adds missing columns/tables at startup. Remove once the
   -- schema scripts in docs/ are run by a separate migration account at release.
   ALTER ROLE db_ddladmin  ADD MEMBER [vm-govbudget-app];
   ```

### Application Gateway
- **Backend setting:** HTTPS 443, host name override = `govbudget.finance.rak.ae`
  (must match the certificate installed on IIS and the `AllowedHosts` value).
- **Health probe:** path `/Account/Login`, host = `govbudget.finance.rak.ae`, accepted
  status `200-399`. A probe sent with the backend IP as host gets HTTP 400 and the backend
  is marked unhealthy.

### IIS on the VM
- Install the **.NET 10 ASP.NET Core Hosting Bundle**, then restart IIS.
- Bind the `*.finance.rak.ae` certificate to the site on 443.
- Web Deploy (port 8172) from the admin workstation over the VPN works the same way as
  Option A above, using a publish profile pointed at the VM.

### Database migration from SmarterASP
Export a `.bacpac` of the current database (SSMS → Tasks → Export Data-tier Application)
and import it into `govbudget-sandbox-db`.
