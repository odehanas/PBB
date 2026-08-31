using System.Collections.Generic;
using System.Linq;

namespace GovBudget.Utils
{
    // One permissionable screen. Forms are code artifacts, so the catalogue lives in
    // code and the database only stores which role may do what on each key.
    public sealed class AppForm
    {
        public string Key { get; init; } = "";
        public string Display { get; init; } = "";
        public string Group { get; init; } = "";
        public string Description { get; init; } = "";

        // True when the screen is read-only by nature (reports, dashboards). The
        // permissions grid disables Add/Edit/Delete for these to avoid meaningless ticks.
        public bool ViewOnlyByNature { get; init; }
    }

    // Central catalogue of permissionable forms. Add a new entry here and it appears on
    // the Roles & Permissions screen automatically; no schema change required.
    public static class AppForms
    {
        public const string BudgetSetup = "BUDGET_SETUP";
        public const string BudgetEntry = "BUDGET_ENTRY";
        public const string HrAllocation = "HR_ALLOCATION";
        public const string MidYear = "MIDYEAR";

        public const string Reports = "REPORTS";
        public const string BudgetVsActual = "BVA";
        public const string WhatIf = "WHATIF";
        public const string ManagementReview = "MGMT_REVIEW";
        public const string Performance = "PERFORMANCE";
        public const string Requests = "REQUESTS";
        public const string Guides = "GUIDES";
        public const string Assistant = "ASSISTANT";

        public const string AdminRoom = "ADMIN_ROOM";
        public const string Users = "USERS";
        public const string Roles = "ROLES";
        public const string Entities = "ENTITIES";
        public const string Departments = "DEPARTMENTS";
        public const string GlAccounts = "GLACCOUNTS";
        public const string Items = "ITEMS";
        public const string Programs = "PROGRAMS";
        public const string Activities = "ACTIVITIES";
        public const string Projects = "PROJECTS";
        public const string HrCosts = "HR_COSTS";
        public const string WorkCalendar = "WORK_CALENDAR";
        public const string Actuals = "ACTUALS";
        public const string Allocation = "ALLOCATION";
        public const string AuditLog = "AUDIT";

        public const string GroupBudgeting = "Budgeting";
        public const string GroupReporting = "Reporting & Review";
        public const string GroupAdmin = "Administration";
        public const string GroupMasterData = "Master Data";

        public static readonly IReadOnlyList<AppForm> All = new List<AppForm>
        {
            new() { Key = BudgetSetup,  Group = GroupBudgeting, Display = "Budget Setup",        Description = "Choose the working year, entity and cost center." },
            new() { Key = BudgetEntry,  Group = GroupBudgeting, Display = "Budget Entry",        Description = "Revenue, OPEX and CAPEX budget lines." },
            new() { Key = HrAllocation, Group = GroupBudgeting, Display = "HR Cost Allocation",  Description = "Allocate employee cost to activities and projects." },
            new() { Key = MidYear,      Group = GroupBudgeting, Display = "Mid-Year Forecast",   Description = "Half-year actuals and second-half forecast." },

            new() { Key = Reports,          Group = GroupReporting, Display = "Reports",            Description = "All budget and costing reports.", ViewOnlyByNature = true },
            new() { Key = BudgetVsActual,   Group = GroupReporting, Display = "Budget vs Actual",   Description = "Budget against posted actuals.", ViewOnlyByNature = true },
            new() { Key = WhatIf,           Group = GroupReporting, Display = "What-If",            Description = "Scenario modelling." },
            new() { Key = ManagementReview, Group = GroupReporting, Display = "Management Review",  Description = "Review, return and approve entity submissions." },
            new() { Key = Performance,      Group = GroupReporting, Display = "Performance Data",   Description = "KPIs, outputs and maturity assessments." },
            new() { Key = Requests,         Group = GroupReporting, Display = "Requests",           Description = "Internal messages and requests." },
            new() { Key = Guides,           Group = GroupReporting, Display = "User Guides",        Description = "Help documentation.", ViewOnlyByNature = true },
            new() { Key = Assistant,        Group = GroupReporting, Display = "Assistant",          Description = "Ask questions about your budget, performance data and OECD PBB practice.", ViewOnlyByNature = true },

            new() { Key = AdminRoom, Group = GroupAdmin, Display = "Admin Room",       Description = "Administration landing page." },
            new() { Key = Users,     Group = GroupAdmin, Display = "Users",            Description = "Create and maintain application users." },
            new() { Key = Roles,     Group = GroupAdmin, Display = "Roles & Rights",   Description = "This screen. Only grant to trusted system admins." },
            new() { Key = HrCosts,   Group = GroupAdmin, Display = "HR Costs",         Description = "Import and review imported employee costs." },
            new() { Key = WorkCalendar, Group = GroupAdmin, Display = "Work Calendars", Description = "Working hours, holidays and leave used for the employee cost per hour." },
            new() { Key = Actuals,   Group = GroupAdmin, Display = "Actuals",          Description = "Import SAP GL / MM and HR actuals." },
            new() { Key = Allocation, Group = GroupAdmin, Display = "Cost Allocation", Description = "Allocation drivers, rules and runs." },
            new() { Key = AuditLog,  Group = GroupAdmin, Display = "Audit Log",        Description = "System audit trail.", ViewOnlyByNature = true },

            new() { Key = Entities,    Group = GroupMasterData, Display = "Entities",     Description = "Government entities." },
            new() { Key = Departments, Group = GroupMasterData, Display = "Cost Centers", Description = "Departments / cost centers." },
            new() { Key = GlAccounts,  Group = GroupMasterData, Display = "GL Accounts",  Description = "Chart of accounts." },
            new() { Key = Items,       Group = GroupMasterData, Display = "Items",        Description = "Budget items linked to GL accounts." },
            new() { Key = Programs,    Group = GroupMasterData, Display = "Programs",     Description = "Programs." },
            new() { Key = Activities,  Group = GroupMasterData, Display = "Activities",   Description = "Activities within programs." },
            new() { Key = Projects,    Group = GroupMasterData, Display = "Projects",     Description = "Projects." },
        };

        public static readonly IReadOnlyList<string> Groups = new[]
        {
            GroupBudgeting, GroupReporting, GroupAdmin, GroupMasterData
        };

        public static AppForm? Find(string key) =>
            All.FirstOrDefault(f => string.Equals(f.Key, key, System.StringComparison.OrdinalIgnoreCase));

        public static IEnumerable<AppForm> InGroup(string group) =>
            All.Where(f => f.Group == group);
    }
}
