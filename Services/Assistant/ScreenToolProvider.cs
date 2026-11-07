using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GovBudget.Utils;

namespace GovBudget.Services.Assistant
{
    /// <summary>A screen the assistant asked the widget to open, with values to fill in on arrival.</summary>
    public sealed record AssistantScreenOpen(string Url, string Name, IReadOnlyList<AssistantScreenField> Fields);

    /// <summary>One value to put into a field of the opened screen. Never saved by the assistant.</summary>
    public sealed record AssistantScreenField(string Name, string Value);

    /// <summary>
    /// Lets the assistant take the user to a screen. The list of screens is filtered by the same
    /// Roles &amp; Rights the sidebar uses, the path must be one of them, and only known query
    /// values are passed on. The widget does the navigating and carries the conversation over.
    /// </summary>
    public sealed class ScreenToolProvider : IAssistantToolProvider
    {
        private enum Need { View, Add }

        // Query values a screen may be opened with. Anything else the model adds is dropped.
        private static readonly Dictionary<string, Func<string, string?>> QueryRules = new(StringComparer.OrdinalIgnoreCase)
        {
            ["year"] = v => int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var y) && y is >= 2000 and <= 2100 ? y.ToString(CultureInfo.InvariantCulture) : null,
            ["entityId"] = v => int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id.ToString(CultureInfo.InvariantCulture) : null,
            ["category"] = v => v.ToUpperInvariant() is "REVENUE" or "OPEX" or "CAPEX" ? v.ToUpperInvariant() : null,
            ["report"] = v => new[] { "income", "gl", "projects", "activities", "activitiesalloc", "hralloc", "hrrate", "entitysummary", "trend" }
                .FirstOrDefault(r => r.Equals(v, StringComparison.OrdinalIgnoreCase)),
            ["dim"] = v => new[] { "gl", "tree", "item", "activity", "program", "department", "project" }
                .FirstOrDefault(d => d.Equals(v, StringComparison.OrdinalIgnoreCase)),
        };

        private sealed record Screen(string Path, string Name, string Description, string? FormKey, Need Need = Need.View, string[]? Query = null);

        // Paths are the routes the sidebar and the screens themselves link to.
        private static readonly Screen[] Screens =
        {
            new("/", "Executive Summary (home dashboard)", "Totals by entity for a year: revenue, HR, OPEX, CAPEX and net.", null, Query: new[] { "year", "entityId" }),
            new("/Context/Year", "Choose budget year", "Switch the working budget year.", null),
            new("/Context/Select", "Budget Setup", "Pick the working year, entity and cost center, then go on to budget entry.", AppForms.BudgetSetup),
            new("/BudgetLines/Entry", "Budget Entry", "Budget lines of the working cost center. category is REVENUE, OPEX or CAPEX.", AppForms.BudgetEntry, Query: new[] { "category" }),
            new("/HrCosts/Budget", "HR Budget Allocation", "Allocate budgeted employee cost to activities and projects.", AppForms.HrAllocation, Query: new[] { "year" }),
            new("/HrCosts", "HR Costs", "Imported employee costs and their allocations.", AppForms.HrCosts, Query: new[] { "year", "entityId" }),
            new("/HrCosts/AllocationVariances", "HR Allocation Variances", "Employees whose allocations do not add up to their cost.", AppForms.HrCosts, Query: new[] { "year", "entityId" }),
            new("/MidYear", "Mid-Year Forecast", "Half-year actuals and the second-half forecast.", AppForms.MidYear, Query: new[] { "year" }),
            new("/Actuals/Forecast", "Year-End Forecast", "Forecast the year-end amount per GL code.", AppForms.Actuals, Query: new[] { "year", "entityId" }),
            new("/Actuals", "Actuals Import", "Import SAP GL, MM and HR actuals.", AppForms.Actuals, Query: new[] { "year", "entityId" }),
            new("/Actuals/BudgetVsActual", "Budget vs Actual", "Budget against posted actuals. dim is gl, tree, item, activity, program, department or project.", AppForms.BudgetVsActual, Query: new[] { "year", "entityId", "dim" }),
            new("/Reports", "Reports", "Budget and costing reports. report is income, gl, projects, activities, activitiesalloc, hralloc, hrrate, entitysummary or trend.", AppForms.Reports, Query: new[] { "report", "year", "entityId" }),
            new("/Reports/Presentation", "Budget Presentation", "Slide-style presentation of the budget.", AppForms.Reports, Query: new[] { "year", "entityId" }),
            new("/Reports/Builder", "Report Builder", "Build a custom report.", AppForms.Reports, Query: new[] { "year", "entityId" }),
            new("/Reports/SubmissionReview", "Submission Review", "Review submitted budgets.", AppForms.Reports),
            new("/InternalMessages", "Requests", "Send and follow internal requests.", AppForms.Requests),
            new("/WhatIf", "What-If", "Scenario modelling with inflation and growth rates.", AppForms.WhatIf),
            new("/ManagementReview", "Management Review", "Review, return and approve entity submissions.", AppForms.ManagementReview, Query: new[] { "year", "entityId" }),
            new("/Performance", "Performance Data", "Landing page for KPIs, outputs and maturity.", AppForms.Performance),
            new("/Performance/Kpis", "KPIs", "KPI list with targets and actuals.", AppForms.Performance, Query: new[] { "year", "entityId" }),
            new("/Performance/KpiEdit", "New KPI", "Form to add a KPI.", AppForms.Performance, Need.Add, new[] { "year" }),
            new("/Performance/Tree", "Program Tree", "Programs, activities and their KPIs as a tree.", AppForms.Performance, Query: new[] { "year", "entityId" }),
            new("/Performance/Outputs", "Outputs", "Output measures and volumes per activity.", AppForms.Performance, Query: new[] { "year", "entityId" }),
            new("/Performance/Maturity", "PBB Maturity", "Performance-budgeting maturity assessment.", AppForms.Performance, Query: new[] { "year" }),
            new("/Performance/ReviewNotes", "Review Notes", "Review notes per entity.", AppForms.Performance, Query: new[] { "year", "entityId" }),
            new("/Performance/Narratives", "Narratives", "Narrative sections of the performance review.", AppForms.Performance, Query: new[] { "year" }),
            new("/Guides", "User Guides", "Help documentation.", AppForms.Guides),
            new("/Admin", "Admin Room", "Administration landing page.", AppForms.AdminRoom),
            new("/Admin/Submissions", "Budget Submissions", "Submitted budgets waiting for approval.", AppForms.AdminRoom, Query: new[] { "year" }),
            new("/Admin/Structure", "Organisation Structure", "Entities, cost centers, programs and activities.", AppForms.AdminRoom),
            new("/Admin/HistoricalActuals", "Historical Actuals", "Prior-year actuals.", AppForms.AdminRoom, Query: new[] { "year", "entityId" }),
            new("/Admin/MidYearActuals", "Mid-Year Actuals Upload", "Mid-year actuals loaded by the administrator.", AppForms.AdminRoom, Query: new[] { "year", "entityId" }),
            new("/ImportHistory", "Import History", "Past imports, with undo.", AppForms.AdminRoom, Query: new[] { "year" }),
            new("/Allocation", "Cost Allocation", "Allocation rules per program.", AppForms.Allocation, Query: new[] { "year", "entityId" }),
            new("/Allocation/Drivers", "Allocation Drivers", "Driver values for cost allocation.", AppForms.Allocation, Query: new[] { "year", "entityId" }),
            new("/Allocation/Runs", "Allocation Runs", "Run cost allocation and see past runs.", AppForms.Allocation, Query: new[] { "year", "entityId" }),
            new("/WorkCalendar", "Work Calendars", "Working hours, holidays and leave.", AppForms.WorkCalendar, Query: new[] { "year" }),
            new("/AuditLogs", "Audit Log", "System audit trail.", AppForms.AuditLog),
            new("/AppUsers", "Users", "Application users.", AppForms.Users),
            new("/Entities", "Entities", "Government entities.", AppForms.Entities),
            new("/Entities/Create", "New Entity", "Form to add an entity.", AppForms.Entities, Need.Add),
            new("/Departments", "Cost Centers", "Departments / cost centers.", AppForms.Departments),
            new("/Departments/Create", "New Cost Center", "Form to add a cost center.", AppForms.Departments, Need.Add),
            new("/GLAccounts", "GL Accounts", "Chart of accounts.", AppForms.GlAccounts),
            new("/GLAccounts/Create", "New GL Account", "Form to add a GL account.", AppForms.GlAccounts, Need.Add),
            new("/Items", "Items", "Budget items linked to GL accounts.", AppForms.Items),
            new("/Items/Create", "New Item", "Form to add an item.", AppForms.Items, Need.Add),
            new("/Programs", "Programs", "Programs.", AppForms.Programs),
            new("/Programs/Create", "New Program", "Form to add a program.", AppForms.Programs, Need.Add),
            new("/Activities", "Activities", "Activities within programs.", AppForms.Activities),
            new("/Activities/Create", "New Activity", "Form to add an activity.", AppForms.Activities, Need.Add),
            new("/Projects", "Projects", "Projects.", AppForms.Projects),
            new("/Projects/Create", "New Project", "Form to add a project.", AppForms.Projects, Need.Add),
            new("/Account/ChangePassword", "Change my password", "The user's own password.", null),
        };

        private const int MaxFields = 40;

        private readonly AssistantAccess _access;
        private readonly AssistantProposalCollector _collector;
        private List<Screen>? _allowed;

        public ScreenToolProvider(AssistantAccess access, AssistantProposalCollector collector)
        {
            _access = access;
            _collector = collector;
        }

        public IEnumerable<AssistantToolDefinition> GetTools()
        {
            yield return new AssistantToolDefinition(
                "list_screens",
                "List the application screens this user can open, with their paths and the query values each accepts.",
                """{"type":"object","properties":{},"additionalProperties":false}""",
                ListAsync);

            yield return new AssistantToolDefinition(
                "open_screen",
                "Open a screen for the user. The conversation carries on there. Optionally fill fields on arrival "
                + "(the user checks them and saves; nothing is saved by this tool).",
                """
                {"type":"object","properties":{
                  "path":{"type":"string","description":"Path from list_screens, optionally with its query values, e.g. /Actuals/BudgetVsActual?year=2027&dim=activity."},
                  "fields":{"type":"array","description":"Optional values to fill in on the screen's form. name is the field name from describe_write_action, or the label shown on the screen.",
                    "items":{"type":"object","properties":{"name":{"type":"string"},"value":{"type":"string"}},"required":["name","value"],"additionalProperties":false}}
                },"required":["path"],"additionalProperties":false}
                """,
                OpenAsync);
        }

        // A screen is offered only when the user holds its Roles & Rights entry and also passes the
        // [Authorize] roles or policy on its controller action, so it never offers a page that
        // would answer "Access denied".
        private async Task<List<Screen>> AllowedScreensAsync()
        {
            if (_allowed is not null) return _allowed;

            var allowed = new List<Screen>();
            if (_access.User is null) return _allowed = allowed;

            foreach (var s in Screens)
            {
                if (s.FormKey is not null
                    && !await _access.HasRightAsync(new[] { s.FormKey }, s.Need == Need.Add ? AssistantAccess.Right.Add : AssistantAccess.Right.View))
                {
                    continue;
                }

                var parts = s.Path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (await _access.PassesAuthorizeAsync(parts.Length > 0 ? parts[0] : "Home", parts.Length > 1 ? parts[1] : "Index"))
                {
                    allowed.Add(s);
                }
            }
            return _allowed = allowed;
        }

        private async Task<string> ListAsync(JsonElement args, AssistantUserContext user, CancellationToken ct)
        {
            var screens = (await AllowedScreensAsync()).Select(s => new
            {
                screen = s.Name,
                path = s.Path,
                s.Description,
                query = s.Query
            });
            return JsonSerializer.Serialize(new { screens });
        }

        private async Task<string> OpenAsync(JsonElement args, AssistantUserContext user, CancellationToken ct)
        {
            var raw = args.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String ? (p.GetString() ?? "").Trim() : "";
            var split = raw.IndexOf('?');
            var path = NormalisePath(split >= 0 ? raw[..split] : raw);
            var query = split >= 0 ? raw[(split + 1)..] : "";

            var screen = (await AllowedScreensAsync()).FirstOrDefault(s => s.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (screen is null)
            {
                return JsonSerializer.Serialize(new { error = "That screen is not one this user can open. Call list_screens for the paths." });
            }

            var kept = new List<string>();
            foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = part.IndexOf('=');
                if (eq <= 0) continue;
                var key = Uri.UnescapeDataString(part[..eq]);
                var value = Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' ')).Trim();
                if (screen.Query?.Contains(key, StringComparer.OrdinalIgnoreCase) != true || !QueryRules.TryGetValue(key, out var rule)) continue;
                var clean = rule(value);
                if (clean is not null && !kept.Any(k => k.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)))
                {
                    kept.Add(Uri.EscapeDataString(QueryRules.Keys.First(k => k.Equals(key, StringComparison.OrdinalIgnoreCase))) + "=" + Uri.EscapeDataString(clean));
                }
            }

            var fields = new List<AssistantScreenField>();
            if (args.TryGetProperty("fields", out var f) && f.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in f.EnumerateArray().Take(MaxFields))
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var name = item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? (n.GetString() ?? "").Trim() : "";
                    var value = item.TryGetProperty("value", out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText()) : "";
                    if (name.Length is 0 or > 100 || name.StartsWith("__", StringComparison.Ordinal)) continue;
                    fields.Add(new AssistantScreenField(name, value.Length > 1000 ? value[..1000] : value));
                }
            }

            var url = _access.PathBase + screen.Path + (kept.Count > 0 ? "?" + string.Join("&", kept) : "");
            _collector.OpenScreen = new AssistantScreenOpen(url, screen.Name, fields);

            return JsonSerializer.Serialize(new
            {
                opening = screen.Name,
                url,
                fields_to_fill = fields.Count,
                note = "The screen opens now and this conversation carries on there. "
                    + (fields.Count > 0 ? "The fields are filled in for the user to check and save; nothing is saved. " : "")
                    + "Tell the user in one short sentence that the screen is opening."
            });
        }

        private static string NormalisePath(string path)
        {
            path = path.Trim();
            if (path.Length == 0 || path[0] != '/' || path.StartsWith("//", StringComparison.Ordinal)) return "";
            path = path.TrimEnd('/');
            if (path.EndsWith("/Index", StringComparison.OrdinalIgnoreCase)) path = path[..^"/Index".Length];
            if (path.Length == 0 || path.Equals("/Home", StringComparison.OrdinalIgnoreCase)) return "/";
            return path;
        }
    }
}
