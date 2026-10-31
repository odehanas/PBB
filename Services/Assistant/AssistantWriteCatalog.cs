using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reflection;
using GovBudget.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace GovBudget.Services.Assistant
{
    public enum WriteKind { Add, Edit, Delete, Workflow }

    /// <summary>One form value the action binds. Rows hold a list of sub-records (Name[i].Sub).</summary>
    public sealed record WriteField(
        string Name,
        string Type,
        bool Required,
        bool IsArray,
        IReadOnlyList<WriteField>? Rows);

    /// <summary>
    /// A screen action the assistant may prepare. It is always executed by the user's own browser
    /// posting to the real endpoint, so the screen's validation, locks, permissions and audit apply.
    /// </summary>
    public sealed record WriteAction(
        string Key,
        string Controller,
        string Action,
        WriteKind Kind,
        string Description,
        Type? RecordType,
        string? KeyField,
        IReadOnlyList<WriteField> Fields);

    /// <summary>
    /// The allowlist of screen actions the assistant can prepare. Fields are read from the action
    /// signatures so they stay in step with the controllers; descriptions are written by hand.
    /// User, role and password administration and file uploads are deliberately left out.
    /// </summary>
    public sealed class AssistantWriteCatalog
    {
        // OnlyFields narrows what the assistant may send when an action binds more of a model
        // than its screen's form shows (the screen sets the rest itself).
        private sealed record Spec(string Controller, string Action, WriteKind Kind, string Description,
            Type? RecordType = null, string? KeyField = null, string[]? OnlyFields = null);

        // Audit columns are stamped by the controllers and never typed by a user.
        private static readonly HashSet<string> IgnoredProperties = new(StringComparer.OrdinalIgnoreCase)
        {
            "CreatedAt", "CreatedBy", "UpdatedAt", "UpdatedBy", "EntrySource"
        };

        private static readonly Spec[] Specs =
        {
            // Budget entry
            new("BudgetLines", "Entry", WriteKind.Add,
                "Add a budget line, or update one when BudgetLineId is given, in the working year, entity and cost center. "
                + "currentCategory is the category code (REVENUE, OPEX, CAPEX). Amount is always Quantity x UnitPrice. "
                + "DistributionMode EQUAL spreads it over 12 months; otherwise M01..M12 must sum to Amount. "
                + "CAPEX lines need CapexAssetType NEW or REPLACEMENT.",
                typeof(BudgetLines), "BudgetLineId"),
            new("BudgetLines", "Delete", WriteKind.Delete, "Delete a budget line in the working context.", typeof(BudgetLines), "id"),
            new("BudgetLines", "Submit", WriteKind.Workflow, "Submit the working cost center's budget for a category (locks it for editing)."),
            new("BudgetLines", "StartRevision", WriteKind.Workflow, "Reopen a submitted category budget for revision."),

            // Actuals, mid-year and forecasts
            new("Actuals", "ForecastSave", WriteKind.Edit,
                "Save year-end forecasts per GL code. glCodes, forecasts and notes are parallel arrays (same length and order)."),
            new("MidYear", "Save", WriteKind.Edit,
                "Save second-half (H2) forecast amounts on mid-year rows. Rows: MidYearId and ForecastH2Amount.",
                typeof(MidYearGlActualForecasts)),

            // HR
            new("HrCosts", "AddAllocation", WriteKind.Add,
                "Allocate an employee cost to an activity (and optional project). allocationMode AMOUNT uses allocatedAmount, PERCENT uses allocatedPercent. budget=true targets the budget allocation."),
            new("HrCosts", "EditAllocation", WriteKind.Edit, "Change an existing HR cost allocation.", typeof(HrEmployeeCostAllocations), "allocationId"),
            new("HrCosts", "DeleteAllocation", WriteKind.Delete, "Remove an HR cost allocation.", typeof(HrEmployeeCostAllocations), "allocationId"),

            // Performance
            new("Performance", "KpiSave", WriteKind.Add, "Add a KPI, or update one when KpiId is given (targets, baseline, actual value, status...).", typeof(Kpis), "KpiId"),
            new("Performance", "KpiDelete", WriteKind.Delete, "Delete a KPI.", typeof(Kpis), "id"),
            new("Performance", "MaturitySave", WriteKind.Edit, "Record the PBB maturity stage for an entity and year."),
            new("Performance", "OutputSave", WriteKind.Edit, "Record an activity's output measure and volume for a year."),
            new("Performance", "ReviewNoteSave", WriteKind.Edit,
                "Add (reviewNoteId 0) or update a management review note for an entity and year.", typeof(EntityReviewNotes), "reviewNoteId"),
            new("Performance", "ReviewNoteDelete", WriteKind.Delete, "Delete a management review note.", typeof(EntityReviewNotes), "id"),
            new("Performance", "NarrativeSave", WriteKind.Edit,
                "Add (reviewNarrativeId 0) or update a review narrative section.", typeof(ReviewNarratives), "reviewNarrativeId"),
            new("Performance", "NarrativeDelete", WriteKind.Delete, "Delete a review narrative.", typeof(ReviewNarratives), "id"),

            // Cost allocation and what-if
            new("Allocation", "ClassifyProgram", WriteKind.Edit, "Set a program's type and allocation sequence.", typeof(Programs), "programId"),
            new("Allocation", "RuleDelete", WriteKind.Delete, "Delete a cost allocation rule.", typeof(AllocationRules), "ruleId"),
            new("Allocation", "Run", WriteKind.Workflow, "Run the cost allocation for a year and entity."),
            new("WhatIf", "Create", WriteKind.Add, "Create a what-if scenario with cost inflation and revenue growth rates."),
            new("WhatIf", "Edit", WriteKind.Edit, "Change a what-if scenario and its per-project rates.", typeof(WhatIfScenarios), "id"),
            new("WhatIf", "Activate", WriteKind.Workflow, "Make a what-if scenario the active one.", typeof(WhatIfScenarios), "id"),
            new("WhatIf", "Clear", WriteKind.Workflow, "Stop applying any what-if scenario."),

            // Approvals and administration of data
            new("Admin", "ApproveSubmission", WriteKind.Workflow, "Approve a budget submission.", typeof(BudgetSubmissions), "id"),
            new("Admin", "ReturnSubmission", WriteKind.Workflow, "Return a budget submission to its cost center with a note.", typeof(BudgetSubmissions), "id"),
            new("Admin", "SendToCentral", WriteKind.Workflow, "Send an approved submission to the central budget office.", typeof(BudgetSubmissions), "id"),
            new("Admin", "SysApproveSubmission", WriteKind.Workflow, "Give the central (system) approval of a submission.", typeof(BudgetSubmissions), "id"),
            new("Admin", "DeleteHistoricalActual", WriteKind.Delete, "Delete a historical GL actual row.", typeof(HistoricalGlActuals), "id"),
            new("Admin", "DeleteMidYearActual", WriteKind.Delete, "Delete a mid-year GL actual row.", typeof(MidYearGlActualForecasts), "id"),
            new("Reports", "DeleteReport", WriteKind.Delete, "Delete a saved report.", typeof(SavedReports), "id"),

            // Master data
            new("Entities", "Create", WriteKind.Add, "Create an entity.", typeof(Entities)),
            new("Entities", "Edit", WriteKind.Edit, "Change an entity.", typeof(Entities), "id"),
            new("Entities", "DeleteConfirmed", WriteKind.Delete, "Delete an entity.", typeof(Entities), "id"),
            new("Departments", "Create", WriteKind.Add, "Create a department (cost center).", typeof(Departments)),
            new("Departments", "Edit", WriteKind.Edit, "Change a department (cost center).", typeof(Departments), "id"),
            new("Departments", "DeleteConfirmed", WriteKind.Delete, "Delete a department (cost center).", typeof(Departments), "id"),
            new("GLAccounts", "Create", WriteKind.Add, "Create a GL account.", typeof(GLAccounts)),
            new("GLAccounts", "Edit", WriteKind.Edit, "Change a GL account.", typeof(GLAccounts), "id"),
            new("GLAccounts", "DeleteConfirmed", WriteKind.Delete, "Delete a GL account.", typeof(GLAccounts), "id"),
            new("Items", "Create", WriteKind.Add, "Create a budget item under a GL account.", typeof(Items)),
            new("Items", "QuickCreate", WriteKind.Add, "Quickly create an item under a GL account."),
            new("Items", "Edit", WriteKind.Edit, "Change an item.", typeof(Items), "id"),
            new("Items", "DeleteConfirmed", WriteKind.Delete, "Delete an item.", typeof(Items), "id"),
            new("Items", "ActivateAll", WriteKind.Workflow, "Mark every item active."),
            new("Programs", "Create", WriteKind.Add, "Create a program.", typeof(Programs)),
            new("Programs", "Edit", WriteKind.Edit, "Change a program.", typeof(Programs), "id"),
            new("Programs", "DeleteConfirmed", WriteKind.Delete, "Delete a program.", typeof(Programs), "id"),
            new("Activities", "Create", WriteKind.Add, "Create an activity under a program and department.", typeof(Activities)),
            new("Activities", "QuickCreate", WriteKind.Add, "Quickly create an activity under a program and department."),
            new("Activities", "Edit", WriteKind.Edit, "Change an activity.", typeof(Activities), "id"),
            new("Activities", "DeleteConfirmed", WriteKind.Delete, "Delete an activity.", typeof(Activities), "id"),
            new("Activities", "Deactivate", WriteKind.Delete, "Deactivate an activity.", typeof(Activities), "id"),
            new("Projects", "Create", WriteKind.Add, "Create a project owned by a department.", typeof(Projects)),
            new("Projects", "QuickCreate", WriteKind.Add, "Quickly create a project owned by a department."),
            new("Projects", "Edit", WriteKind.Edit, "Change a project.", typeof(Projects), "id"),
            new("Projects", "DeleteConfirmed", WriteKind.Delete, "Delete a project.", typeof(Projects), "id"),

            new("WorkCalendar", "Create", WriteKind.Add,
                "Create a work calendar (hours per day, work days per week, holidays, leave, utilisation) for a year and entity.",
                typeof(WorkCalendars)),
            new("WorkCalendar", "Edit", WriteKind.Edit, "Change a work calendar.", typeof(WorkCalendars), "id"),
            new("WorkCalendar", "DeleteConfirmed", WriteKind.Delete, "Delete a work calendar.", typeof(WorkCalendars), "id"),

            // Requests and working context
            new("InternalMessages", "Index", WriteKind.Add, "Send an internal request or message to the administrators.",
                OnlyFields: new[] { "Subject", "Body" }),
            new("Context", "Select", WriteKind.Workflow, "Switch the working budget year, entity and cost center (BudgetYear, EntityId, DepartmentId)."),
        };

        private readonly IActionDescriptorCollectionProvider _descriptors;
        private IReadOnlyDictionary<string, WriteAction>? _actions;

        public AssistantWriteCatalog(IActionDescriptorCollectionProvider descriptors)
        {
            _descriptors = descriptors;
        }

        public IReadOnlyDictionary<string, WriteAction> Actions => _actions ??= Build();

        private IReadOnlyDictionary<string, WriteAction> Build()
        {
            var posts = _descriptors.ActionDescriptors.Items
                .OfType<ControllerActionDescriptor>()
                .Where(d => d.MethodInfo.IsDefined(typeof(HttpPostAttribute), inherit: true))
                .ToList();

            var result = new Dictionary<string, WriteAction>(StringComparer.OrdinalIgnoreCase);
            foreach (var spec in Specs)
            {
                // Matched on the method name too: DeleteConfirmed is routed as [ActionName("Delete")].
                var descriptor = posts.FirstOrDefault(d =>
                    string.Equals(d.ControllerName, spec.Controller, StringComparison.OrdinalIgnoreCase)
                    && (string.Equals(d.ActionName, spec.Action, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(d.MethodInfo.Name, spec.Action, StringComparison.OrdinalIgnoreCase)));
                if (descriptor is null) continue;

                var fields = FieldsOf(descriptor.MethodInfo);
                if (spec.OnlyFields is not null)
                {
                    fields = fields.Where(f => spec.OnlyFields.Contains(f.Name, StringComparer.OrdinalIgnoreCase)).ToList();
                }

                var key = $"{spec.Controller}.{spec.Action}";
                result[key] = new WriteAction(key, descriptor.ControllerName, descriptor.ActionName, spec.Kind,
                    spec.Description, spec.RecordType, spec.KeyField, fields);
            }
            return result;
        }

        private static IReadOnlyList<WriteField> FieldsOf(MethodInfo method)
        {
            var fields = new List<WriteField>();
            var nullability = new NullabilityInfoContext();

            foreach (var p in method.GetParameters())
            {
                var type = p.ParameterType;
                if (IsFile(type) || type == typeof(System.Threading.CancellationToken)) continue;

                var simple = SimpleType(type);
                if (simple is not null)
                {
                    var optional = p.HasDefaultValue || Nullable.GetUnderlyingType(type) is not null
                        || (!type.IsValueType && nullability.Create(p).WriteState != NullabilityState.NotNull);
                    fields.Add(new WriteField(p.Name!, simple, !optional, false, null));
                    continue;
                }

                var element = ElementType(type);
                if (element is not null && SimpleType(element) is { } elementType)
                {
                    fields.Add(new WriteField(p.Name!, elementType, false, true, null));
                    continue;
                }

                var include = p.GetCustomAttribute<BindAttribute>()?.Include;
                fields.AddRange(PropertiesOf(type, include));
            }

            return fields;
        }

        private static IEnumerable<WriteField> PropertiesOf(Type type, string[]? include)
        {
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanWrite || IgnoredProperties.Contains(prop.Name)) continue;
                if (include is { Length: > 0 } && !include.Contains(prop.Name, StringComparer.OrdinalIgnoreCase)) continue;

                var required = prop.IsDefined(typeof(RequiredAttribute));
                var simple = SimpleType(prop.PropertyType);
                if (simple is not null)
                {
                    yield return new WriteField(prop.Name, simple, required, false, null);
                    continue;
                }

                // Lists of plain view-model rows (e.g. mid-year rows); EF navigations are skipped.
                var element = ElementType(prop.PropertyType);
                if (element is not null && !prop.GetGetMethod()!.IsVirtual)
                {
                    if (SimpleType(element) is { } elementType)
                    {
                        yield return new WriteField(prop.Name, elementType, required, true, null);
                    }
                    else
                    {
                        yield return new WriteField(prop.Name, "rows", required, true, PropertiesOf(element, null).ToList());
                    }
                }
            }
        }

        private static bool IsFile(Type type) =>
            typeof(IFormFile).IsAssignableFrom(type) || typeof(IFormFileCollection).IsAssignableFrom(type);

        private static Type? ElementType(Type type)
        {
            if (type == typeof(string) || !typeof(IEnumerable).IsAssignableFrom(type)) return null;
            if (type.IsArray) return type.GetElementType();
            return type.IsGenericType ? type.GetGenericArguments()[0] : null;
        }

        internal static string? SimpleType(Type type)
        {
            var t = Nullable.GetUnderlyingType(type) ?? type;
            if (t == typeof(string)) return "string";
            if (t == typeof(bool)) return "boolean";
            if (t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte)) return "integer";
            if (t == typeof(decimal) || t == typeof(double) || t == typeof(float)) return "number";
            if (t == typeof(DateTime) || t == typeof(DateOnly)) return "date";
            if (t.IsEnum) return "string";
            return null;
        }
    }
}
