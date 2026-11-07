using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GovBudget.Models;
using GovBudget.Utils;
using Microsoft.EntityFrameworkCore;

namespace GovBudget.Services.Assistant
{
    /// <summary>
    /// Lets the assistant import a file the user attached in the panel, through the same import
    /// screen the user would use. It never uploads anything itself: propose_file_import shows a
    /// card, and only the user's click on Confirm makes their browser submit the file to the
    /// screen, whose own checks, permissions and messages then apply.
    /// </summary>
    public sealed class FileImportToolProvider : IAssistantToolProvider
    {
        private sealed record Import(
            string Key,
            string Title,
            string Description,
            string Controller,
            string Action,
            string Layout,
            string[] FormKeys,
            string YearField,
            string TemplateController,
            string TemplateAction,
            string ResultScreen);

        private static readonly Import[] Imports =
        {
            new("hr_costs", "Import HR costs",
                "Employee annual costs for a budget year, for the whole entity (administrators). Adds new employees and updates existing ones with the same EmployeeID. An 'HR Allocations' sheet, if present, replaces those employees' allocations. There is no preview step.",
                "HrCosts", "Import", SpreadsheetInspector.HrCosts, new[] { AppForms.HrCosts, AppForms.HrAllocation }, "budgetYear",
                "HrCosts", "Template", "HR Costs"),
            new("hr_budget", "Import HR budget for the working cost center",
                "Employee annual costs for the working entity and cost center picked in Budget Setup. Rows for any other entity or cost center are rejected. Adds new employees and updates existing ones. There is no preview step.",
                "HrCosts", "BudgetImport", SpreadsheetInspector.HrCosts, new[] { AppForms.HrCosts, AppForms.HrAllocation }, "budgetYear",
                "HrCosts", "BudgetTemplate", "HR Budget Allocation"),
            new("hr_actuals", "Import HR actuals",
                "Actual HR cost per employee and month. If actuals already exist for the year, the screen asks the user to confirm the overwrite.",
                "Actuals", "UploadHrEmp", SpreadsheetInspector.HrActuals, new[] { AppForms.Actuals }, "year",
                "Actuals", "TemplateHrEmp", "Actuals Import"),
        };

        private readonly AssistantAccess _access;
        private readonly AssistantAttachmentStore _attachments;
        private readonly AssistantProposalCollector _collector;
        private readonly GovBudgetContext _db;

        public FileImportToolProvider(AssistantAccess access, AssistantAttachmentStore attachments, AssistantProposalCollector collector, GovBudgetContext db)
        {
            _access = access;
            _attachments = attachments;
            _collector = collector;
            _db = db;
        }

        public IEnumerable<AssistantToolDefinition> GetTools()
        {
            yield return new AssistantToolDefinition(
                "list_file_imports",
                "List the file imports this user may run (HR costs, HR budget, HR actuals) and the layout each expects.",
                """{"type":"object","properties":{},"additionalProperties":false}""",
                ListAsync);

            yield return new AssistantToolDefinition(
                "propose_file_import",
                "Prepare the import of a file the user attached. Shows a card; nothing is uploaded until the user clicks Confirm.",
                """
                {"type":"object","properties":{
                  "attachment_id":{"type":"string","description":"attachment_id from the [Attached file ...] note."},
                  "import":{"type":"string","enum":["hr_costs","hr_budget","hr_actuals"],"description":"Which import to run, from list_file_imports."},
                  "year":{"type":"integer","description":"Budget year to import into. Default: the working budget year."},
                  "summary":{"type":"string","description":"One sentence telling the user what will happen."}
                },"required":["attachment_id","import","summary"],"additionalProperties":false}
                """,
                ProposeAsync);

            yield return new AssistantToolDefinition(
                "get_import_template",
                "Download the empty Excel template of an import for the user.",
                """
                {"type":"object","properties":{
                  "import":{"type":"string","enum":["hr_costs","hr_budget","hr_actuals"]},
                  "year":{"type":"integer","description":"Year for templates that list the year's employees (hr_actuals)."}
                },"required":["import"],"additionalProperties":false}
                """,
                TemplateAsync);
        }

        private async Task<bool> AllowedAsync(Import import) =>
            await _access.HasRightAsync(import.FormKeys, AssistantAccess.Right.Add)
            && await _access.PassesAuthorizeAsync(import.Controller, import.Action, "POST");

        private async Task<string> ListAsync(JsonElement args, AssistantUserContext user, CancellationToken ct)
        {
            var imports = new List<object>();
            foreach (var import in Imports)
            {
                if (!await AllowedAsync(import)) continue;
                imports.Add(new { import = import.Key, import.Title, import.Description, expects_layout = import.Layout });
            }
            return Json(new
            {
                imports,
                note = imports.Count == 0
                    ? "This user may not run any file import."
                    : "The user attaches the file with the paperclip in the assistant panel; its summary then appears as an [Attached file ...] note."
            });
        }

        private async Task<string> ProposeAsync(JsonElement args, AssistantUserContext user, CancellationToken ct)
        {
            var import = Find(Str(args, "import"));
            if (import is null) return Json(new { error = "Unknown import. Use list_file_imports." });
            if (!await AllowedAsync(import)) return Json(new { error = $"This user may not run \"{import.Title}\"." });

            var attachment = _attachments.Get(user.UserName, Str(args, "attachment_id"));
            if (attachment is null) return Json(new { error = "That attachment was not found. Ask the user to attach the file again with the paperclip." });
            if (attachment.Layout != import.Layout)
            {
                return Json(new
                {
                    error = $"\"{attachment.FileName}\" does not have the layout this import reads ({import.Layout}); it looks like: {attachment.Layout}. "
                        + "Tell the user which columns are expected, or offer the template (get_import_template)."
                });
            }

            var thisYear = DateTime.Now.Year;
            var year = args.TryGetProperty("year", out var y) && y.ValueKind == JsonValueKind.Number && y.TryGetInt32(out var yv) ? yv : user.DefaultYear;
            if (year < thisYear - 1 || year > thisYear + 2)
            {
                return Json(new { error = $"Imports accept the years {thisYear - 1} to {thisYear + 2}." });
            }

            var changes = new List<AssistantProposalField>
            {
                new("File", attachment.FileName, null),
                new("Import into", import.Title, null),
                new("Budget year", year.ToString(CultureInfo.InvariantCulture), null),
            };
            foreach (var (key, label) in new[] { ("employees", "Employees"), ("rows", "Rows"), ("total_annual_cost", "Total annual cost"),
                         ("total_amount", "Total amount"), ("months", "Months"), ("allocation_rows", "Allocation rows") })
            {
                if (attachment.Summary.TryGetValue(key, out var value) && value is not null)
                {
                    changes.Add(new(label, value is IEnumerable<int> list ? string.Join(", ", list) : value.ToString() ?? "", null));
                }
            }

            var warnings = new List<string>();
            var form = new List<KeyValuePair<string, string>> { new(import.YearField, year.ToString(CultureInfo.InvariantCulture)) };

            if (import.Key == "hr_budget")
            {
                if (user.ContextEntityId is null || user.ContextDepartmentId is null)
                {
                    return Json(new { error = "No working entity and cost center are selected. Propose Context.Select first (Budget Setup), then the import." });
                }
                var entity = await _db.Entities.AsNoTracking().Where(e => e.EntityId == user.ContextEntityId)
                    .Select(e => new { e.EntityCode, e.EntityName }).FirstOrDefaultAsync(ct);
                var dept = await _db.Departments.AsNoTracking().Where(d => d.DepartmentId == user.ContextDepartmentId)
                    .Select(d => new { d.DeptCode, d.DeptName }).FirstOrDefaultAsync(ct);
                changes.Add(new("Working cost center", $"{entity?.EntityName} / {dept?.DeptName}", null));
                Mismatch(warnings, "entity", attachment.EntityNames, entity?.EntityName, entity?.EntityCode);
                Mismatch(warnings, "cost center", attachment.CostCenterNames, dept?.DeptName, dept?.DeptCode);
            }
            else if (import.Key == "hr_costs" && user.EntityId.HasValue)
            {
                var entity = await _db.Entities.AsNoTracking().Where(e => e.EntityId == user.EntityId)
                    .Select(e => new { e.EntityCode, e.EntityName }).FirstOrDefaultAsync(ct);
                Mismatch(warnings, "entity", attachment.EntityNames, entity?.EntityName, entity?.EntityCode);
            }
            else if (import.Key == "hr_actuals" && !user.IsGlobalAdmin && user.EntityId.HasValue)
            {
                warnings.Add("All rows go to your own entity; the EntityCode column is ignored.");
            }

            if (attachment.Summary.TryGetValue("duplicate_employee_ids", out var dup) && dup is int d && d > 0)
                warnings.Add($"{d} employee ID(s) appear more than once; the last row wins.");
            if (attachment.Summary.TryGetValue("rows_without_a_valid_annual_cost", out var bad) && bad is int b && b > 0)
                warnings.Add($"{b} row(s) have no valid Annual Cost and will be rejected.");
            if (attachment.Summary.TryGetValue("invalid_rows", out var inv) && inv is int i && i > 0)
                warnings.Add($"{i} row(s) have an invalid month or amount and will be rejected.");

            changes.Add(new("What happens", import.Description, null));
            foreach (var w in warnings) changes.Add(new("Check", w, null));

            var summary = Str(args, "summary");
            var proposal = new AssistantProposal(
                Guid.NewGuid().ToString("N"),
                $"{import.Controller}.{import.Action}",
                "import",
                import.Title,
                string.IsNullOrWhiteSpace(summary) ? $"Import {attachment.FileName} for {year}." : (summary.Length > 300 ? summary[..300] : summary),
                $"{_access.PathBase}/{import.Controller}/{import.Action}",
                attachment.FileName,
                form,
                changes,
                attachment.Id);
            _collector.Proposals.Add(proposal);

            return Json(new
            {
                proposed = import.Title,
                year,
                warnings,
                note = "A card is shown. Nothing is uploaded until the user clicks Confirm on it; the page then changes to the "
                    + import.ResultScreen + " screen with the import's result, and the conversation carries on there."
            });
        }

        private async Task<string> TemplateAsync(JsonElement args, AssistantUserContext user, CancellationToken ct)
        {
            var import = Find(Str(args, "import"));
            if (import is null) return Json(new { error = "Unknown import. Use list_file_imports." });
            if (!await _access.HasRightAsync(import.FormKeys, AssistantAccess.Right.View)
                || !await _access.PassesAuthorizeAsync(import.TemplateController, import.TemplateAction))
            {
                return Json(new { error = $"This user may not download the \"{import.Title}\" template." });
            }

            var url = $"{_access.PathBase}/{import.TemplateController}/{import.TemplateAction}";
            if (import.Key == "hr_actuals")
            {
                var year = args.TryGetProperty("year", out var y) && y.ValueKind == JsonValueKind.Number && y.TryGetInt32(out var yv) ? yv : user.DefaultYear;
                url += "?year=" + year.ToString(CultureInfo.InvariantCulture);
            }
            _collector.Download = new AssistantDownload(url, import.Title + " template");
            return Json(new { downloading = import.Title + " template", note = "The template is downloading in the user's browser." });
        }

        private static void Mismatch(List<string> warnings, string what, IReadOnlyList<string> inFile, string? name, string? code)
        {
            var others = inFile.Where(v => !string.Equals(v, name, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(v, code, StringComparison.OrdinalIgnoreCase)).Take(5).ToList();
            if (others.Count > 0)
            {
                warnings.Add($"Rows for another {what} will be rejected: {string.Join(", ", others)}.");
            }
        }

        private static Import? Find(string key) => Imports.FirstOrDefault(i => i.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

        private static string Str(JsonElement args, string name) =>
            args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "").Trim() : "";

        private static string Json(object value) => JsonSerializer.Serialize(value);
    }
}
