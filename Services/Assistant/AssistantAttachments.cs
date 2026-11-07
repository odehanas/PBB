using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.Extensions.Caching.Memory;

namespace GovBudget.Services.Assistant
{
    /// <summary>
    /// What the assistant knows about a file the user attached in the panel. Only a summary is
    /// kept (counts, totals, entity and cost-center names) - never employee names or the rows -
    /// and the file itself stays in the user's browser until they confirm an import.
    /// </summary>
    public sealed record AssistantAttachment(
        string Id,
        string UserName,
        string FileName,
        long Size,
        string Layout,
        IReadOnlyDictionary<string, object?> Summary,
        IReadOnlyList<string> EntityNames,
        IReadOnlyList<string> CostCenterNames)
    {
        /// <summary>The note added to the conversation, so the model knows the file and its id.</summary>
        public string Note => $"[Attached file \"{FileName}\" (attachment_id {Id}, layout {Layout}): {JsonSerializer.Serialize(Summary)}]";
    }

    /// <summary>Attachment summaries per user, kept for an hour in memory.</summary>
    public sealed class AssistantAttachmentStore
    {
        private static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);
        private readonly IMemoryCache _cache;

        public AssistantAttachmentStore(IMemoryCache cache) => _cache = cache;

        public AssistantAttachment Add(string userName, string fileName, long size, SpreadsheetInspector.Result inspected)
        {
            var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
            var attachment = new AssistantAttachment(id, userName, fileName, size, inspected.Layout, inspected.Summary,
                inspected.EntityNames, inspected.CostCenterNames);
            _cache.Set(Key(userName, id), attachment, Lifetime);
            return attachment;
        }

        /// <summary>Only the user who attached a file can refer to it.</summary>
        public AssistantAttachment? Get(string userName, string? id)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 32) return null;
            return _cache.TryGetValue(Key(userName, id.Trim().ToLowerInvariant()), out AssistantAttachment? a) ? a : null;
        }

        private static string Key(string userName, string id) => $"assistantAttachment:{userName.ToLowerInvariant()}:{id}";
    }

    /// <summary>
    /// Recognises the layout of an attached workbook the way the import screens read it, and
    /// summarises it. Headers are compared like HrCostsController does (letters and digits only).
    /// </summary>
    public static class SpreadsheetInspector
    {
        public const string HrCosts = "hr_costs";
        public const string HrActuals = "hr_actuals";
        public const string Unknown = "unknown";

        private const int MaxRows = 50000;

        public sealed record Result(
            string Layout,
            Dictionary<string, object?> Summary,
            List<string> EntityNames,
            List<string> CostCenterNames);

        public static Result Inspect(Stream stream)
        {
            using var wb = new XLWorkbook(stream);
            var ws = wb.Worksheets.First();
            var headers = ws.Row(1).CellsUsed()
                .Select(c => (Key: Normalize(c.GetString()), Column: c.Address.ColumnNumber, Text: c.GetString().Trim()))
                .Where(h => h.Key.Length > 0)
                .ToList();
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in headers) map[h.Key] = h.Column;
            var lastRow = Math.Min(ws.LastRowUsed()?.RowNumber() ?? 1, MaxRows);

            int? Col(params string[] keys) => keys.Select(k => map.TryGetValue(k, out var c) ? c : (int?)null).FirstOrDefault(c => c.HasValue);

            var hrCosts = new
            {
                Employee = Col("employeeid"),
                Name = Col("employeename"),
                Entity = Col("entityname"),
                Department = Col("departmentname"),
                Cost = Col("annualcost"),
                Gl = Col("glaccountcode", "glcode", "gl"),
                Kind = Col("glkind", "gltype"),
            };

            if (hrCosts.Employee.HasValue && hrCosts.Name.HasValue && hrCosts.Entity.HasValue && hrCosts.Department.HasValue
                && hrCosts.Cost.HasValue && hrCosts.Gl.HasValue && hrCosts.Kind.HasValue)
            {
                var employees = 0; var missing = 0; var total = 0m;
                var entities = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                var departments = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                var kinds = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var duplicates = 0;
                for (var r = 2; r <= lastRow; r++)
                {
                    var row = ws.Row(r);
                    var id = row.Cell(hrCosts.Employee.Value).GetString().Trim();
                    if (id.Length == 0) continue;
                    employees++;
                    if (!ids.Add(id)) duplicates++;
                    var entity = row.Cell(hrCosts.Entity.Value).GetString().Trim();
                    var dept = row.Cell(hrCosts.Department.Value).GetString().Trim();
                    var kind = row.Cell(hrCosts.Kind.Value).GetString().Trim();
                    if (entity.Length > 0) entities.Add(entity);
                    if (dept.Length > 0) departments.Add(dept);
                    if (kind.Length > 0) kinds.Add(kind.ToUpperInvariant());
                    if (TryNumber(row.Cell(hrCosts.Cost.Value), out var cost)) total += cost; else missing++;
                }

                var allocations = wb.Worksheets.FirstOrDefault(s =>
                    s.Name.Equals("HR Allocations", StringComparison.OrdinalIgnoreCase) || s.Name.Equals("Allocations", StringComparison.OrdinalIgnoreCase));
                var allocationRows = allocations is null ? 0
                    : Enumerable.Range(2, Math.Max(0, (allocations.LastRowUsed()?.RowNumber() ?? 1) - 1))
                        .Count(r => allocations.Cell(r, 1).GetString().Trim().Length > 0);

                return new Result(HrCosts, new Dictionary<string, object?>
                {
                    ["layout_meaning"] = "HR employee cost template (EmployeeID, Employee Name, Entity Name, Department Name, Annual Cost, GL Account Code, GL Kind)",
                    ["employees"] = employees,
                    ["duplicate_employee_ids"] = duplicates,
                    ["total_annual_cost"] = total.ToString("#,##0.00", CultureInfo.InvariantCulture),
                    ["rows_without_a_valid_annual_cost"] = missing,
                    ["entities"] = entities.Take(10).ToList(),
                    ["cost_centers"] = departments.Take(20).ToList(),
                    ["cost_center_count"] = departments.Count,
                    ["gl_kinds"] = kinds.ToList(),
                    ["allocation_rows"] = allocationRows,
                    ["has_occupation_column"] = Col("occupation", "jobtitle", "title", "designation").HasValue,
                }, entities.ToList(), departments.ToList());
            }

            // The HR actuals upload reads its columns by position: EntityCode, EmployeeCode, Month, Amount.
            string Header(int column) => Normalize(ws.Cell(1, column).GetString());
            if (Header(1) == "entitycode" && Header(2) == "employeecode" && Header(3).StartsWith("month", StringComparison.Ordinal) && Header(4) == "amount")
            {
                var rows = 0; var invalid = 0; var total = 0m;
                var months = new SortedSet<int>();
                var entities = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                var employees = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var r = 2; r <= lastRow; r++)
                {
                    var entity = ws.Cell(r, 1).GetString().Trim();
                    var employee = ws.Cell(r, 2).GetString().Trim();
                    if (entity.Length == 0 && employee.Length == 0) continue;
                    rows++;
                    if (entity.Length > 0) entities.Add(entity);
                    if (employee.Length > 0) employees.Add(employee);
                    var ok = int.TryParse(ws.Cell(r, 3).GetString().Trim(), out var month) && month is >= 1 and <= 12;
                    if (ok) months.Add(month);
                    if (ok && TryNumber(ws.Cell(r, 4), out var amount)) total += amount; else invalid++;
                }

                return new Result(HrActuals, new Dictionary<string, object?>
                {
                    ["layout_meaning"] = "HR actuals by employee and month (EntityCode, EmployeeCode, Month 1-12, Amount)",
                    ["rows"] = rows,
                    ["employees"] = employees.Count,
                    ["months"] = months.ToList(),
                    ["total_amount"] = total.ToString("#,##0.00", CultureInfo.InvariantCulture),
                    ["invalid_rows"] = invalid,
                    ["entity_codes"] = entities.Take(10).ToList(),
                }, entities.ToList(), new List<string>());
            }

            return new Result(Unknown, new Dictionary<string, object?>
            {
                ["layout_meaning"] = "Not an import template the assistant recognises",
                ["sheets"] = wb.Worksheets.Select(s => s.Name).Take(10).ToList(),
                ["first_sheet_headers"] = headers.Select(h => h.Text).Take(20).ToList(),
                ["data_rows"] = Math.Max(0, lastRow - 1),
            }, new List<string>(), new List<string>());
        }

        private static string Normalize(string value) =>
            new string(value.Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

        private static bool TryNumber(IXLCell cell, out decimal value)
        {
            if (cell.DataType == XLDataType.Number) { value = (decimal)cell.GetDouble(); return true; }
            var raw = cell.GetString().Trim();
            return decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out value);
        }
    }
}
