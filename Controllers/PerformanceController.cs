using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClosedXML.Excel;
using GovBudget.Models;
using GovBudget.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace GovBudget.Controllers
{
    /// <summary>
    /// Phase 3 data entry for the PBB performance layer (KPIs, Maturity, Activity Outputs).
    /// Additive and isolated. Admin-managed (central DoF role).
    /// </summary>
    [Authorize(Roles = "ADMIN,SYSADMIN")]
    public class PerformanceController : Controller
    {
        private const string DefaultPeriod = "MidYear";
        private readonly GovBudgetContext _db;

        public PerformanceController(GovBudgetContext db)
        {
            _db = db;
        }

        // Entity scope helpers. SYSADMIN / global ADMIN see all entities and may filter.
        // Entity-scoped ADMINs are allowed in, but every read and write is locked to their own entity.
        private int? GetEntityClaimId()
        {
            var entityClaim = User.Claims.FirstOrDefault(c => c.Type == "EntityId")?.Value;
            if (!int.TryParse(entityClaim, out var eid) || eid <= 0) return null;
            return eid;
        }

        private bool IsGlobalAdmin()
        {
            if (User.IsInRole("SYSADMIN")) return true;
            if (!User.IsInRole("ADMIN")) return false;
            return !GetEntityClaimId().HasValue;
        }

        // Global admins: honor the requested entity (null = all). Entity admins: forced to their entity (-1 if none).
        private int? EffectiveEntityId(int? requested)
        {
            if (IsGlobalAdmin())
                return (requested.HasValue && requested.Value > 0) ? requested : (int?)null;
            var scoped = GetEntityClaimId();
            return scoped ?? -1;
        }

        private int ResolveYear(int? year)
        {
            return year ?? HttpContext.Session.GetInt("ctxYear") ?? DateTime.Now.Year;
        }

        private List<SelectListItem> YearOptions(int selected)
        {
            var thisYear = DateTime.Now.Year;
            return new[] { thisYear - 1, thisYear, thisYear + 1, thisYear + 2 }
                .Select(y => new SelectListItem(y.ToString(), y.ToString(), y == selected))
                .ToList();
        }

        public IActionResult Index()
        {
            // Cross-entity editorial narratives (Findings / Recommendations / 90-Day Plan) are central-only.
            ViewBag.IsGlobalAdmin = IsGlobalAdmin();
            return View();
        }

        // ---------------- KPIs ----------------

        [HttpGet]
        public async Task<IActionResult> Kpis(int? year = null, int? entityId = null)
        {
            var selectedYear = ResolveYear(year);

            var scope = EffectiveEntityId(entityId);
            List<Kpis> kpis;
            if (scope.HasValue && scope.Value <= 0)
            {
                kpis = new List<Kpis>();
            }
            else
            {
                var query = _db.Kpis.AsNoTracking().Where(k => k.BudgetYear == selectedYear && k.Period == DefaultPeriod);
                if (scope.HasValue) query = query.Where(k => k.EntityId == scope.Value);
                kpis = await query.ToListAsync();
            }

            var entityMap = await _db.Entities.AsNoTracking().ToDictionaryAsync(e => e.EntityId, e => e.EntityCode + " - " + e.EntityName);
            var progMap = await _db.Programs.AsNoTracking().ToDictionaryAsync(p => p.ProgramId, p => p.ProgramCode);

            var rows = kpis.Select(k => new KpiListRow
            {
                KpiId = k.KpiId,
                EntityLabel = entityMap.TryGetValue(k.EntityId, out var en) ? en : k.EntityId.ToString(),
                ProgramCode = k.ProgramId != null && progMap.TryGetValue(k.ProgramId.Value, out var pc) ? pc : "",
                KpiCode = k.KpiCode ?? "",
                Priority = k.Priority ?? "",
                KpiName = k.KpiName,
                Unit = k.Unit ?? "",
                KpiType = k.KpiType ?? "",
                Dimension = k.Dimension ?? "",
                ReadingType = k.ReadingType ?? "",
                Baseline = k.Baseline,
                Target = k.Target,
                Actual = k.ActualValue,
                StrategicTarget2029 = k.StrategicTarget2029,
                Status = ResolveKpiStatus(k)
            })
            .OrderBy(r => r.EntityLabel).ThenBy(r => r.KpiName)
            .ToList();

            ViewBag.Year = selectedYear;
            ViewBag.YearOptions = YearOptions(selectedYear);
            ViewBag.EntityOptions = await EntityOptions(entityId);
            ViewBag.SelectedEntityId = entityId;
            ViewBag.IsGlobalAdmin = IsGlobalAdmin();
            return View(rows);
        }

        // ---------------- KPI hierarchy tree (Programme > Activity > KPI) ----------------
        [HttpGet]
        public async Task<IActionResult> Tree(int? year = null, int? entityId = null)
        {
            var selectedYear = ResolveYear(year);
            var scope = EffectiveEntityId(entityId);

            var vm = new KpiTreeVm
            {
                Year = selectedYear,
                YearOptions = YearOptions(selectedYear),
                EntityOptions = await EntityOptions(entityId),
                SelectedEntityId = entityId
            };
            if (scope.HasValue && scope.Value < 0) { vm.NoAccess = true; return View(vm); }

            int? eff = (scope.HasValue && scope.Value > 0) ? scope : (int?)null;

            var entMap = await _db.Entities.AsNoTracking()
                .ToDictionaryAsync(e => e.EntityId, e => e.EntityCode + " - " + e.EntityName);
            var programs = await _db.Programs.AsNoTracking()
                .Where(p => !eff.HasValue || p.EntityId == eff.Value)
                .Select(p => new { p.ProgramId, p.ProgramCode, p.ProgramName, p.EntityId })
                .ToListAsync();
            var activities = await (
                from a in _db.Activities.AsNoTracking()
                join p in _db.Programs.AsNoTracking() on a.ProgramId equals p.ProgramId
                where (!eff.HasValue || p.EntityId == eff.Value)
                select new { a.ActivityId, a.ActivityCode, a.ActivityName, a.ProgramId, p.EntityId }
            ).ToListAsync();
            var kpis = await _db.Kpis.AsNoTracking()
                .Where(k => k.BudgetYear == selectedYear && k.Period == DefaultPeriod
                    && (!eff.HasValue || k.EntityId == eff.Value))
                .ToListAsync();

            var kpisByActivity = kpis.Where(k => k.ActivityId != null)
                .GroupBy(k => k.ActivityId!.Value).ToDictionary(g => g.Key, g => g.ToList());
            var directKpisByProgram = kpis.Where(k => k.ActivityId == null && k.ProgramId != null)
                .GroupBy(k => k.ProgramId!.Value).ToDictionary(g => g.Key, g => g.ToList());
            var activitiesByProgram = activities.GroupBy(a => a.ProgramId).ToDictionary(g => g.Key, g => g.ToList());

            KpiTreeKpi ToKpi(Kpis k) => new KpiTreeKpi
            {
                Name = k.KpiName,
                Code = k.KpiCode ?? "",
                Type = k.KpiType ?? "",
                Unit = k.Unit ?? "",
                Target = k.Target,
                Actual = k.ActualValue,
                Status = ResolveKpiStatus(k)
            };

            // Group by entity (shown once when a single entity is in scope).
            foreach (var entGroup in programs.GroupBy(p => p.EntityId).OrderBy(g => g.Key))
            {
                var entNode = new KpiTreeEntity
                {
                    EntityId = entGroup.Key,
                    EntityLabel = entMap.TryGetValue(entGroup.Key, out var en) ? en : entGroup.Key.ToString()
                };

                foreach (var p in entGroup.OrderBy(p => p.ProgramCode))
                {
                    var progNode = new KpiTreeProgram { Code = p.ProgramCode, Name = p.ProgramName };
                    if (activitiesByProgram.TryGetValue(p.ProgramId, out var acts))
                    {
                        foreach (var a in acts.OrderBy(a => a.ActivityCode))
                        {
                            var actNode = new KpiTreeActivity { Code = a.ActivityCode, Name = a.ActivityName };
                            if (kpisByActivity.TryGetValue(a.ActivityId, out var aks))
                                actNode.Kpis = aks.OrderBy(k => k.KpiName).Select(ToKpi).ToList();
                            progNode.Activities.Add(actNode);
                        }
                    }
                    if (directKpisByProgram.TryGetValue(p.ProgramId, out var dks))
                        progNode.DirectKpis = dks.OrderBy(k => k.KpiName).Select(ToKpi).ToList();

                    progNode.KpiCount = progNode.Activities.Sum(x => x.Kpis.Count) + progNode.DirectKpis.Count;
                    entNode.Programs.Add(progNode);
                }

                // KPIs in this entity with no programme and no activity.
                var orphans = kpis.Where(k => k.EntityId == entGroup.Key && k.ProgramId == null && k.ActivityId == null)
                    .OrderBy(k => k.KpiName).Select(ToKpi).ToList();
                entNode.UnassignedKpis = orphans;

                vm.Entities.Add(entNode);
            }

            vm.ProgramCount = programs.Count;
            vm.ActivityCount = activities.Count;
            vm.KpiCount = kpis.Count;
            return View(vm);
        }

        [HttpGet]
        public async Task<IActionResult> KpiEdit(long? id, int? year = null)
        {
            var selectedYear = ResolveYear(year);
            var scope = EffectiveEntityId(null);
            Kpis model;
            if (id.HasValue)
            {
                model = await _db.Kpis.FindAsync(id.Value) ?? new Kpis { BudgetYear = selectedYear, Period = DefaultPeriod, Direction = "UP" };
                if (!IsGlobalAdmin() && model.KpiId > 0 && model.EntityId != (scope ?? -1))
                    return Forbid();
            }
            else
            {
                model = new Kpis { BudgetYear = selectedYear, Period = DefaultPeriod, Direction = "UP" };
                if (!IsGlobalAdmin() && scope.HasValue && scope.Value > 0) model.EntityId = scope.Value;
            }
            await PopulateKpiEditLists(model);
            return View(model);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> KpiSave(Kpis model)
        {
            // Navigation properties are not bound from the form; remove any validation noise.
            ModelState.Remove("Entity");
            ModelState.Remove("Program");
            ModelState.Remove("Activity");
            ModelState.Remove("KpiCostLinks");

            if (string.IsNullOrWhiteSpace(model.KpiName))
            {
                ModelState.AddModelError(nameof(model.KpiName), "KPI name is required.");
            }
            // Entity admins can only save KPIs for their own entity.
            if (!IsGlobalAdmin())
            {
                var myId = GetEntityClaimId();
                if (!myId.HasValue) return Forbid();
                model.EntityId = myId.Value;
            }

            if (!ModelState.IsValid)
            {
                await PopulateKpiEditLists(model);
                return View("KpiEdit", model);
            }

            if (string.IsNullOrWhiteSpace(model.Period)) model.Period = DefaultPeriod;
            if (string.IsNullOrWhiteSpace(model.Direction)) model.Direction = "UP";
            if (model.ProgramId == 0) model.ProgramId = null;
            if (model.ActivityId == 0) model.ActivityId = null;
            model.KpiType = NormalizeChoice(model.KpiType);
            model.Dimension = NormalizeChoice(model.Dimension);
            model.ReadingType = NormalizeChoice(model.ReadingType);
            model.Priority = NormalizeChoice(model.Priority);
            model.KpiCode = NormalizeChoice(model.KpiCode);
            model.ProgramOwner = NormalizeChoice(model.ProgramOwner);
            model.CalculationMethod = NormalizeChoice(model.CalculationMethod);
            model.Scope = NormalizeChoice(model.Scope);

            if (model.KpiId > 0)
            {
                var existing = await _db.Kpis.FindAsync(model.KpiId);
                if (existing == null) return NotFound();
                if (!IsGlobalAdmin() && existing.EntityId != (GetEntityClaimId() ?? -1)) return Forbid();
                existing.BudgetYear = model.BudgetYear;
                existing.Period = model.Period;
                existing.EntityId = model.EntityId;
                existing.ProgramId = model.ProgramId;
                existing.ActivityId = model.ActivityId;
                existing.KpiName = model.KpiName;
                existing.Unit = model.Unit;
                existing.KpiType = model.KpiType;
                existing.Dimension = model.Dimension;
                existing.ReadingType = model.ReadingType;
                existing.Priority = model.Priority;
                existing.KpiCode = model.KpiCode;
                existing.ProgramOwner = model.ProgramOwner;
                existing.CalculationMethod = model.CalculationMethod;
                existing.Scope = model.Scope;
                existing.StrategicTarget2029 = model.StrategicTarget2029;
                existing.CostWeight = model.CostWeight;
                existing.Direction = model.Direction;
                existing.Baseline = model.Baseline;
                existing.Target = model.Target;
                existing.ActualValue = model.ActualValue;
                existing.Status = string.IsNullOrWhiteSpace(model.Status) ? null : model.Status;
            }
            else
            {
                model.Status = string.IsNullOrWhiteSpace(model.Status) ? null : model.Status;
                model.CreatedBy = User.Identity?.Name;
                _db.Kpis.Add(model);
            }
            await _db.SaveChangesAsync();
            return RedirectToAction(nameof(Kpis), new { year = model.BudgetYear });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> KpiDelete(long id, int year)
        {
            var existing = await _db.Kpis.FindAsync(id);
            if (existing != null)
            {
                if (!IsGlobalAdmin() && existing.EntityId != (GetEntityClaimId() ?? -1)) return Forbid();
                _db.Kpis.Remove(existing);
                await _db.SaveChangesAsync();
            }
            return RedirectToAction(nameof(Kpis), new { year });
        }

        // ---------------- KPI Excel export ----------------

        [HttpGet]
        public async Task<IActionResult> KpisExport(int? year = null, int? entityId = null)
        {
            var selectedYear = ResolveYear(year);
            var scope = EffectiveEntityId(entityId);

            var kpis = new List<Kpis>();
            if (!(scope.HasValue && scope.Value <= 0))
            {
                var query = _db.Kpis.AsNoTracking().Where(k => k.BudgetYear == selectedYear && k.Period == DefaultPeriod);
                if (scope.HasValue) query = query.Where(k => k.EntityId == scope.Value);
                kpis = await query.ToListAsync();
            }

            var entityMap = await _db.Entities.AsNoTracking().ToDictionaryAsync(e => e.EntityId, e => e.EntityCode + " - " + e.EntityName);
            var progMap = await _db.Programs.AsNoTracking().ToDictionaryAsync(p => p.ProgramId, p => p.ProgramCode + " - " + p.ProgramName);
            var actMap = await _db.Activities.AsNoTracking().ToDictionaryAsync(a => a.ActivityId, a => a.ActivityCode + " - " + a.ActivityName);

            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add("KPIs");
            var headers = new[] { "Entity", "Programme", "Activity", "KPI Code", "KPI", "Priority", "Program Owner", "Scope", "Calculation Method", "Unit", "Type", "Dimension", "Reading Type", "Direction", "Baseline", "Target", "Actual", "Strategic Target 2029", "Status", "Cost Weight" };
            for (int c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];
            var head = ws.Range(1, 1, 1, headers.Length).Style;
            head.Font.Bold = true;
            head.Fill.BackgroundColor = XLColor.FromHtml(BrandColors.HeaderHex);
            head.Font.FontColor = XLColor.White;

            int r = 2;
            foreach (var k in kpis.OrderBy(k => k.EntityId).ThenBy(k => k.KpiName))
            {
                ws.Cell(r, 1).Value = entityMap.TryGetValue(k.EntityId, out var en) ? en : k.EntityId.ToString();
                ws.Cell(r, 2).Value = k.ProgramId != null && progMap.TryGetValue(k.ProgramId.Value, out var pc) ? pc : "";
                ws.Cell(r, 3).Value = k.ActivityId != null && actMap.TryGetValue(k.ActivityId.Value, out var ac) ? ac : "";
                ws.Cell(r, 4).Value = k.KpiCode ?? "";
                ws.Cell(r, 5).Value = k.KpiName;
                ws.Cell(r, 6).Value = k.Priority ?? "";
                ws.Cell(r, 7).Value = k.ProgramOwner ?? "";
                ws.Cell(r, 8).Value = k.Scope ?? "";
                ws.Cell(r, 9).Value = k.CalculationMethod ?? "";
                ws.Cell(r, 10).Value = k.Unit ?? "";
                ws.Cell(r, 11).Value = k.KpiType ?? "";
                ws.Cell(r, 12).Value = k.Dimension ?? "";
                ws.Cell(r, 13).Value = k.ReadingType ?? "";
                ws.Cell(r, 14).Value = k.Direction;
                if (k.Baseline.HasValue) ws.Cell(r, 15).Value = k.Baseline.Value;
                if (k.Target.HasValue) ws.Cell(r, 16).Value = k.Target.Value;
                if (k.ActualValue.HasValue) ws.Cell(r, 17).Value = k.ActualValue.Value;
                if (k.StrategicTarget2029.HasValue) ws.Cell(r, 18).Value = k.StrategicTarget2029.Value;
                ws.Cell(r, 19).Value = string.IsNullOrWhiteSpace(k.Status) ? "(auto)" : k.Status;
                if (k.CostWeight.HasValue) ws.Cell(r, 20).Value = k.CostWeight.Value;
                r++;
            }
            ws.SheetView.FreezeRows(1);
            ws.Columns().AdjustToContents();

            using var stream = new MemoryStream();
            wb.SaveAs(stream);
            return File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"KPIs_{selectedYear}.xlsx");
        }

        // ---------------- KPI import template (blank, with reference codes) ----------------

        [HttpGet]
        public async Task<IActionResult> KpiTemplate(int? year = null, int? entityId = null)
        {
            var selectedYear = ResolveYear(year);
            var scope = EffectiveEntityId(entityId);

            var entitiesQ = _db.Entities.AsNoTracking().AsQueryable();
            if (scope.HasValue && scope.Value > 0) entitiesQ = entitiesQ.Where(e => e.EntityId == scope.Value);
            var entities = await entitiesQ.OrderBy(e => e.EntityCode).ToListAsync();

            var progsQ =
                from p in _db.Programs.AsNoTracking()
                select new { p.ProgramCode, p.ProgramName, p.EntityId };
            if (scope.HasValue && scope.Value > 0) progsQ = progsQ.Where(x => x.EntityId == scope.Value);
            var programs = await progsQ.OrderBy(p => p.EntityId).ThenBy(p => p.ProgramCode).ToListAsync();

            var actsQ =
                from a in _db.Activities.AsNoTracking()
                join p in _db.Programs.AsNoTracking() on a.ProgramId equals p.ProgramId
                select new { a.ActivityCode, a.ActivityName, p.ProgramCode, p.EntityId };
            if (scope.HasValue && scope.Value > 0) actsQ = actsQ.Where(x => x.EntityId == scope.Value);
            var activities = await actsQ.OrderBy(a => a.EntityId).ThenBy(a => a.ProgramCode).ThenBy(a => a.ActivityCode).ToListAsync();

            var entityCodeMap = entities.ToDictionary(e => e.EntityId, e => e.EntityCode);

            using var wb = new XLWorkbook();

            // Sheet 1: the fill-in template
            var ws = wb.Worksheets.Add("KPIs");
            var headers = new[] { "EntityCode", "ProgrammeCode", "ActivityCode", "KPI Code", "KPI Name", "Priority (High/Medium/Low)", "Program Owner", "Scope", "Calculation Method", "Unit", "Type (Input/Output/Outcome)", "Dimension (Efficiency/Quality)", "Reading Type (Cumulative/Rate)", "Direction (UP/DOWN)", "Baseline", "Target", "Actual", "Strategic Target 2029", "Cost Weight" };
            for (int c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];
            var head = ws.Range(1, 1, 1, headers.Length).Style;
            head.Font.Bold = true;
            head.Fill.BackgroundColor = XLColor.FromHtml(BrandColors.HeaderHex);
            head.Font.FontColor = XLColor.White;

            // Example row (uses first entity/programme in scope if available)
            var exEntity = entities.FirstOrDefault()?.EntityCode ?? "ENT01";
            var exProg = programs.FirstOrDefault()?.ProgramCode ?? "PRG01";
            ws.Cell(2, 1).Value = exEntity;
            ws.Cell(2, 2).Value = exProg;
            ws.Cell(2, 3).Value = "";
            ws.Cell(2, 4).Value = "DiM-01.01";
            ws.Cell(2, 5).Value = "Example: Number of visitors";
            ws.Cell(2, 6).Value = "High";
            ws.Cell(2, 7).Value = "";
            ws.Cell(2, 8).Value = "";
            ws.Cell(2, 9).Value = "";
            ws.Cell(2, 10).Value = "count";
            ws.Cell(2, 11).Value = "Output";
            ws.Cell(2, 12).Value = "Efficiency";
            ws.Cell(2, 13).Value = "Cumulative";
            ws.Cell(2, 14).Value = "UP";
            ws.Cell(2, 15).Value = 100;
            ws.Cell(2, 16).Value = 150;
            ws.Cell(2, 17).Value = "";
            ws.Cell(2, 18).Value = 300;
            ws.Cell(2, 19).Value = 1;
            ws.Row(2).Style.Font.Italic = true;
            ws.Row(2).Style.Font.FontColor = XLColor.Gray;

            // In-cell dropdown lists for the classification columns (rows 2..1000).
            void AddListValidation(int col, string csv)
            {
                var dv = ws.Range(2, col, 1000, col).CreateDataValidation();
                dv.List("\"" + csv + "\"", true);
                dv.IgnoreBlanks = true;
                dv.ErrorStyle = XLErrorStyle.Warning;
                dv.ErrorTitle = "Invalid value";
                dv.ErrorMessage = "Please pick one of: " + csv.Replace(",", ", ");
            }
            AddListValidation(6, "High,Medium,Low");        // Priority
            AddListValidation(11, "Input,Output,Outcome");  // Type
            AddListValidation(12, "Efficiency,Quality");    // Dimension
            AddListValidation(13, "Cumulative,Rate");       // Reading Type
            AddListValidation(14, "UP,DOWN");               // Direction

            ws.SheetView.FreezeRows(1);
            ws.Columns().AdjustToContents();

            // Sheet 2: valid reference codes
            var refWs = wb.Worksheets.Add("Reference");
            refWs.Cell(1, 1).Value = "Use these exact codes in the KPIs sheet. Do not edit the header row. ActualValue is optional.";
            refWs.Cell(1, 1).Style.Font.Bold = true;

            int rr = 3;
            refWs.Cell(rr, 1).Value = "ENTITIES";
            refWs.Cell(rr, 1).Style.Font.Bold = true; rr++;
            refWs.Cell(rr, 1).Value = "EntityCode"; refWs.Cell(rr, 2).Value = "Entity Name";
            refWs.Range(rr, 1, rr, 2).Style.Font.Bold = true; rr++;
            foreach (var e in entities) { refWs.Cell(rr, 1).Value = e.EntityCode; refWs.Cell(rr, 2).Value = e.EntityName; rr++; }

            rr += 1;
            refWs.Cell(rr, 1).Value = "PROGRAMMES";
            refWs.Cell(rr, 1).Style.Font.Bold = true; rr++;
            refWs.Cell(rr, 1).Value = "EntityCode"; refWs.Cell(rr, 2).Value = "ProgrammeCode"; refWs.Cell(rr, 3).Value = "Programme Name";
            refWs.Range(rr, 1, rr, 3).Style.Font.Bold = true; rr++;
            foreach (var p in programs)
            {
                refWs.Cell(rr, 1).Value = entityCodeMap.TryGetValue(p.EntityId, out var ec) ? ec : p.EntityId.ToString();
                refWs.Cell(rr, 2).Value = p.ProgramCode;
                refWs.Cell(rr, 3).Value = p.ProgramName;
                rr++;
            }

            rr += 1;
            refWs.Cell(rr, 1).Value = "ACTIVITIES (optional)";
            refWs.Cell(rr, 1).Style.Font.Bold = true; rr++;
            refWs.Cell(rr, 1).Value = "EntityCode"; refWs.Cell(rr, 2).Value = "ProgrammeCode"; refWs.Cell(rr, 3).Value = "ActivityCode"; refWs.Cell(rr, 4).Value = "Activity Name";
            refWs.Range(rr, 1, rr, 4).Style.Font.Bold = true; rr++;
            foreach (var a in activities)
            {
                refWs.Cell(rr, 1).Value = entityCodeMap.TryGetValue(a.EntityId, out var ec) ? ec : a.EntityId.ToString();
                refWs.Cell(rr, 2).Value = a.ProgramCode;
                refWs.Cell(rr, 3).Value = a.ActivityCode;
                refWs.Cell(rr, 4).Value = a.ActivityName;
                rr++;
            }
            refWs.Columns().AdjustToContents();

            using var stream = new MemoryStream();
            wb.SaveAs(stream);
            return File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"KPI_Import_Template_{selectedYear}.xlsx");
        }

        // ---------------- KPI import (upload filled template) ----------------

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> KpiImport(IFormFile? file, int year, int? entityId = null)
        {
            var selectedYear = year > 0 ? year : ResolveYear(null);
            if (file == null || file.Length == 0)
            {
                TempData["KpiImportError"] = "Please choose an .xlsx file to upload.";
                return RedirectToAction(nameof(Kpis), new { year = selectedYear, entityId });
            }

            var global = IsGlobalAdmin();
            var myEntity = GetEntityClaimId();
            if (!global && !myEntity.HasValue)
            {
                TempData["KpiImportError"] = "Your account is not scoped to an entity, so you cannot import KPIs.";
                return RedirectToAction(nameof(Kpis), new { year = selectedYear, entityId });
            }

            var entities = await _db.Entities.AsNoTracking().ToListAsync();
            var entityByCode = entities
                .GroupBy(e => e.EntityCode.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().EntityId, StringComparer.OrdinalIgnoreCase);
            var programs = await _db.Programs.AsNoTracking().Select(p => new { p.ProgramId, p.ProgramCode, p.EntityId }).ToListAsync();
            var activities = await (
                from a in _db.Activities.AsNoTracking()
                join p in _db.Programs.AsNoTracking() on a.ProgramId equals p.ProgramId
                select new { a.ActivityId, a.ActivityCode, p.EntityId }).ToListAsync();

            int created = 0, updated = 0;
            var errors = new List<string>();

            try
            {
                using var stream = file.OpenReadStream();
                using var wb = new XLWorkbook(stream);
                var ws = wb.Worksheet(1);
                var lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;

                for (int r = 2; r <= lastRow; r++)
                {
                    string Get(int c) => ws.Cell(r, c).GetString().Trim();
                    var entityCode = Get(1);
                    var progCode = Get(2);
                    var actCode = Get(3);
                    var kpiCode = Get(4);
                    var kpiName = Get(5);
                    var priority = MatchChoice(Get(6), "High", "Medium", "Low");
                    var programOwner = Get(7);
                    var scope = Get(8);
                    var calcMethod = Get(9);
                    var unit = Get(10);
                    var kpiType = MatchChoice(Get(11), "Input", "Output", "Outcome");
                    var dimension = MatchChoice(Get(12), "Efficiency", "Quality");
                    var readingType = MatchChoice(Get(13), "Cumulative", "Rate");
                    var direction = Get(14).ToUpperInvariant();

                    if (string.IsNullOrWhiteSpace(entityCode) && string.IsNullOrWhiteSpace(kpiName)) continue; // blank row
                    if (kpiName.StartsWith("Example:", StringComparison.OrdinalIgnoreCase)) continue; // template sample
                    if (string.IsNullOrWhiteSpace(kpiName)) { errors.Add($"Row {r}: KPI Name is required."); continue; }

                    int eid;
                    if (!global)
                    {
                        eid = myEntity!.Value;
                    }
                    else if (string.IsNullOrWhiteSpace(entityCode) || !entityByCode.TryGetValue(entityCode, out eid))
                    {
                        errors.Add($"Row {r}: unknown Entity code '{entityCode}'.");
                        continue;
                    }

                    int? pid = null;
                    if (!string.IsNullOrWhiteSpace(progCode))
                    {
                        var prog = programs.FirstOrDefault(p => p.EntityId == eid && string.Equals(p.ProgramCode, progCode, StringComparison.OrdinalIgnoreCase));
                        if (prog == null) errors.Add($"Row {r}: unknown Programme code '{progCode}' for this entity (KPI saved without a programme).");
                        else pid = prog.ProgramId;
                    }

                    int? aid = null;
                    if (!string.IsNullOrWhiteSpace(actCode))
                    {
                        var act = activities.FirstOrDefault(a => a.EntityId == eid && string.Equals(a.ActivityCode, actCode, StringComparison.OrdinalIgnoreCase));
                        if (act == null) errors.Add($"Row {r}: unknown Activity code '{actCode}' (KPI saved without an activity).");
                        else aid = act.ActivityId;
                    }

                    decimal? baseline = TryDecimal(Get(15));
                    decimal? target = TryDecimal(Get(16));
                    decimal? actual = TryDecimal(Get(17));
                    decimal? strategic = TryDecimal(Get(18));
                    decimal? costWeight = TryDecimal(Get(19));
                    if (direction != "UP" && direction != "DOWN") direction = "UP";

                    var existing = await _db.Kpis.FirstOrDefaultAsync(k =>
                        k.BudgetYear == selectedYear && k.Period == DefaultPeriod && k.EntityId == eid && k.KpiName == kpiName);

                    if (existing == null)
                    {
                        _db.Kpis.Add(new Kpis
                        {
                            BudgetYear = selectedYear,
                            Period = DefaultPeriod,
                            EntityId = eid,
                            ProgramId = pid,
                            ActivityId = aid,
                            KpiName = kpiName,
                            KpiCode = string.IsNullOrWhiteSpace(kpiCode) ? null : kpiCode,
                            Priority = priority,
                            ProgramOwner = string.IsNullOrWhiteSpace(programOwner) ? null : programOwner,
                            Scope = string.IsNullOrWhiteSpace(scope) ? null : scope,
                            CalculationMethod = string.IsNullOrWhiteSpace(calcMethod) ? null : calcMethod,
                            Unit = string.IsNullOrWhiteSpace(unit) ? null : unit,
                            KpiType = kpiType,
                            Dimension = dimension,
                            ReadingType = readingType,
                            Direction = direction,
                            Baseline = baseline,
                            Target = target,
                            ActualValue = actual,
                            StrategicTarget2029 = strategic,
                            CostWeight = costWeight,
                            CreatedBy = User.Identity?.Name
                        });
                        created++;
                    }
                    else
                    {
                        existing.ProgramId = pid;
                        existing.ActivityId = aid;
                        existing.KpiCode = string.IsNullOrWhiteSpace(kpiCode) ? null : kpiCode;
                        existing.Priority = priority;
                        existing.ProgramOwner = string.IsNullOrWhiteSpace(programOwner) ? null : programOwner;
                        existing.Scope = string.IsNullOrWhiteSpace(scope) ? null : scope;
                        existing.CalculationMethod = string.IsNullOrWhiteSpace(calcMethod) ? null : calcMethod;
                        existing.Unit = string.IsNullOrWhiteSpace(unit) ? null : unit;
                        existing.KpiType = kpiType;
                        existing.Dimension = dimension;
                        existing.ReadingType = readingType;
                        existing.Direction = direction;
                        existing.Baseline = baseline;
                        existing.Target = target;
                        existing.ActualValue = actual;
                        existing.StrategicTarget2029 = strategic;
                        existing.CostWeight = costWeight;
                        updated++;
                    }
                }

                await _db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                TempData["KpiImportError"] = "Could not read the file. Make sure it is the .xlsx template (the KPIs sheet must be first). " + ex.Message;
                return RedirectToAction(nameof(Kpis), new { year = selectedYear, entityId });
            }

            var msg = $"Import complete: {created} added, {updated} updated.";
            if (errors.Count > 0)
                msg += $" {errors.Count} note(s): " + string.Join(" | ", errors.Take(12)) + (errors.Count > 12 ? " ..." : "");
            TempData["KpiImportResult"] = msg;
            return RedirectToAction(nameof(Kpis), new { year = selectedYear, entityId });
        }

        private static decimal? TryDecimal(string s) => decimal.TryParse(s, out var d) ? d : (decimal?)null;

        private static string? NormalizeChoice(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        // Maps a free-text classification value to the canonical option (case/space tolerant), else null.
        private static string? MatchChoice(string? s, params string[] allowed)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var t = s.Trim();
            return allowed.FirstOrDefault(a => string.Equals(a, t, StringComparison.OrdinalIgnoreCase));
        }

        private async Task PopulateKpiEditLists(Kpis model)
        {
            var global = IsGlobalAdmin();
            var scoped = GetEntityClaimId();

            var entitiesQ = _db.Entities.AsNoTracking().AsQueryable();
            if (!global && scoped.HasValue) entitiesQ = entitiesQ.Where(e => e.EntityId == scoped.Value);
            ViewBag.EntityOptions = await entitiesQ
                .OrderBy(e => e.EntityCode)
                .Select(e => new SelectListItem(e.EntityCode + " - " + e.EntityName, e.EntityId.ToString(), e.EntityId == model.EntityId))
                .ToListAsync();

            var progsQ = _db.Programs.AsNoTracking().AsQueryable();
            if (!global && scoped.HasValue) progsQ = progsQ.Where(p => p.EntityId == scoped.Value);
            ViewBag.ProgramOptions = await progsQ
                .OrderBy(p => p.ProgramCode)
                .Select(p => new SelectListItem(p.ProgramCode + " - " + p.ProgramName, p.ProgramId.ToString(), model.ProgramId != null && p.ProgramId == model.ProgramId))
                .ToListAsync();

            var actQ =
                from a in _db.Activities.AsNoTracking()
                join p in _db.Programs.AsNoTracking() on a.ProgramId equals p.ProgramId
                select new { a.ActivityId, a.ActivityCode, a.ActivityName, p.EntityId };
            if (!global && scoped.HasValue) actQ = actQ.Where(x => x.EntityId == scoped.Value);
            ViewBag.ActivityOptions = (await actQ.OrderBy(a => a.ActivityCode).ToListAsync())
                .Select(a => new SelectListItem(a.ActivityCode + " - " + a.ActivityName, a.ActivityId.ToString(), model.ActivityId != null && a.ActivityId == model.ActivityId))
                .ToList();

            ViewBag.YearOptions = YearOptions(model.BudgetYear);
        }

        // ---------------- Maturity ----------------

        [HttpGet]
        public async Task<IActionResult> Maturity(int? year = null)
        {
            var selectedYear = ResolveYear(year);
            var entitiesQuery = _db.Entities.AsNoTracking().OrderBy(e => e.EntityCode).AsQueryable();
            if (!IsGlobalAdmin())
            {
                var myId = GetEntityClaimId();
                entitiesQuery = entitiesQuery.Where(e => myId.HasValue && e.EntityId == myId.Value);
            }
            var entities = await entitiesQuery.ToListAsync();
            var existing = await _db.MaturityAssessments.AsNoTracking()
                .Where(m => m.BudgetYear == selectedYear && m.Period == DefaultPeriod)
                .ToListAsync();
            var map = existing.ToDictionary(m => m.EntityId);

            var rows = entities.Select(e =>
            {
                map.TryGetValue(e.EntityId, out var m);
                return new MaturityEditRow
                {
                    EntityId = e.EntityId,
                    EntityLabel = e.EntityCode + " - " + e.EntityName,
                    Stage = m?.Stage ?? 1.0m,
                    Form = m?.Form ?? "",
                    StatusLabel = m?.StatusLabel ?? ""
                };
            }).ToList();

            ViewBag.Year = selectedYear;
            ViewBag.YearOptions = YearOptions(selectedYear);
            return View(rows);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> MaturitySave(int entityId, int year, decimal stage, string? form, string? statusLabel)
        {
            if (!IsGlobalAdmin() && entityId != (GetEntityClaimId() ?? -1)) return Forbid();
            var existing = await _db.MaturityAssessments
                .FirstOrDefaultAsync(m => m.EntityId == entityId && m.BudgetYear == year && m.Period == DefaultPeriod);
            if (existing == null)
            {
                _db.MaturityAssessments.Add(new MaturityAssessments
                {
                    EntityId = entityId,
                    BudgetYear = year,
                    Period = DefaultPeriod,
                    Stage = stage,
                    Form = form,
                    StatusLabel = statusLabel,
                    AssessedBy = User.Identity?.Name
                });
            }
            else
            {
                existing.Stage = stage;
                existing.Form = form;
                existing.StatusLabel = statusLabel;
                existing.AssessedBy = User.Identity?.Name;
                existing.AssessedAt = DateTime.UtcNow;
            }
            await _db.SaveChangesAsync();
            return RedirectToAction(nameof(Maturity), new { year });
        }

        // ---------------- Activity Outputs ----------------

        [HttpGet]
        public async Task<IActionResult> Outputs(int? year = null, int? entityId = null)
        {
            var selectedYear = ResolveYear(year);
            var scope = EffectiveEntityId(entityId);

            var activities = new List<ActivityRowProjection>();
            if (!(scope.HasValue && scope.Value <= 0))
            {
                var activitiesQuery =
                    from a in _db.Activities.AsNoTracking()
                    join p in _db.Programs.AsNoTracking() on a.ProgramId equals p.ProgramId
                    select new ActivityRowProjection { ActivityId = a.ActivityId, ActivityCode = a.ActivityCode, ActivityName = a.ActivityName, EntityId = p.EntityId, ProgramCode = p.ProgramCode };
                if (scope.HasValue)
                    activitiesQuery = activitiesQuery.Where(x => x.EntityId == scope.Value);
                activities = await activitiesQuery.ToListAsync();
            }
            var actIds = activities.Select(a => a.ActivityId).ToList();

            var outputs = await _db.ActivityOutputs.AsNoTracking()
                .Where(o => o.BudgetYear == selectedYear && actIds.Contains(o.ActivityId) && o.IsPrimary)
                .ToListAsync();
            var outMap = outputs.GroupBy(o => o.ActivityId).ToDictionary(g => g.Key, g => g.First());

            var rows = activities.Select(a =>
            {
                outMap.TryGetValue(a.ActivityId, out var o);
                return new OutputEditRow
                {
                    ActivityId = a.ActivityId,
                    ActivityLabel = a.ActivityCode + " - " + a.ActivityName,
                    ProgramCode = a.ProgramCode,
                    OutputMeasure = o?.OutputMeasure ?? "",
                    OutputVolume = o?.OutputVolume ?? 0m
                };
            })
            .OrderBy(r => r.ProgramCode).ThenBy(r => r.ActivityLabel)
            .ToList();

            ViewBag.Year = selectedYear;
            ViewBag.YearOptions = YearOptions(selectedYear);
            ViewBag.EntityOptions = await EntityOptions(entityId);
            return View(rows);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> OutputSave(int activityId, int year, string? outputMeasure, decimal outputVolume)
        {
            if (!IsGlobalAdmin())
            {
                var myId = GetEntityClaimId() ?? -1;
                var actEntity = await (
                    from a in _db.Activities.AsNoTracking()
                    join p in _db.Programs.AsNoTracking() on a.ProgramId equals p.ProgramId
                    where a.ActivityId == activityId
                    select (int?)p.EntityId).FirstOrDefaultAsync();
                if (actEntity != myId) return Forbid();
            }
            var existing = await _db.ActivityOutputs
                .FirstOrDefaultAsync(o => o.ActivityId == activityId && o.BudgetYear == year && o.IsPrimary);

            if (string.IsNullOrWhiteSpace(outputMeasure))
            {
                if (existing != null)
                {
                    _db.ActivityOutputs.Remove(existing);
                    await _db.SaveChangesAsync();
                }
                return RedirectToAction(nameof(Outputs), new { year });
            }

            if (existing == null)
            {
                _db.ActivityOutputs.Add(new ActivityOutputs
                {
                    ActivityId = activityId,
                    BudgetYear = year,
                    OutputMeasure = outputMeasure,
                    OutputVolume = outputVolume,
                    IsPrimary = true,
                    CreatedBy = User.Identity?.Name
                });
            }
            else
            {
                existing.OutputMeasure = outputMeasure;
                existing.OutputVolume = outputVolume;
            }
            await _db.SaveChangesAsync();
            return RedirectToAction(nameof(Outputs), new { year });
        }

        // ---------------- Entity Profile narrative notes (Assessment / Outcome / Issue) ----------------

        [HttpGet]
        public async Task<IActionResult> ReviewNotes(int? year = null, int? entityId = null, int? noteId = null)
        {
            var selectedYear = ResolveYear(year);
            var eid = IsGlobalAdmin() ? entityId : GetEntityClaimId();
            ViewBag.Year = selectedYear;
            ViewBag.YearOptions = YearOptions(selectedYear);
            ViewBag.EntityOptions = await EntityOptions(eid);
            ViewBag.SelectedEntityId = eid;

            EntityReviewNotes editing = new EntityReviewNotes { BudgetYear = selectedYear, Period = DefaultPeriod, NoteType = "Assessment" };
            if (noteId.HasValue)
            {
                var found = await _db.EntityReviewNotes.FindAsync(noteId.Value);
                if (found != null && (IsGlobalAdmin() || found.EntityId == (eid ?? -1))) editing = found;
            }
            ViewBag.Editing = editing;

            var notes = new List<EntityReviewNotes>();
            if (eid.HasValue && eid.Value > 0)
            {
                ViewBag.EntityName = await _db.Entities.AsNoTracking()
                    .Where(e => e.EntityId == eid.Value)
                    .Select(e => e.EntityCode + " - " + e.EntityName).FirstOrDefaultAsync();
                notes = await _db.EntityReviewNotes.AsNoTracking()
                    .Where(n => n.EntityId == eid.Value && n.BudgetYear == selectedYear && n.Period == DefaultPeriod)
                    .OrderBy(n => n.NoteType).ThenBy(n => n.SortOrder).ThenBy(n => n.EntityReviewNoteId)
                    .ToListAsync();
            }
            return View(notes);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ReviewNoteSave(int reviewNoteId, int entityId, int year, string noteType, string? body, int sortOrder)
        {
            if (!IsGlobalAdmin() && entityId != (GetEntityClaimId() ?? -1)) return Forbid();
            if (entityId <= 0 || string.IsNullOrWhiteSpace(body))
                return RedirectToAction(nameof(ReviewNotes), new { year, entityId });

            if (reviewNoteId > 0)
            {
                var existing = await _db.EntityReviewNotes.FindAsync(reviewNoteId);
                if (existing != null)
                {
                    if (!IsGlobalAdmin() && existing.EntityId != (GetEntityClaimId() ?? -1)) return Forbid();
                    existing.NoteType = noteType;
                    existing.Body = body;
                    existing.SortOrder = sortOrder;
                    await _db.SaveChangesAsync();
                }
            }
            else
            {
                _db.EntityReviewNotes.Add(new EntityReviewNotes
                {
                    EntityId = entityId,
                    BudgetYear = year,
                    Period = DefaultPeriod,
                    NoteType = string.IsNullOrWhiteSpace(noteType) ? "Outcome" : noteType,
                    Body = body,
                    SortOrder = sortOrder,
                    CreatedBy = User.Identity?.Name
                });
                await _db.SaveChangesAsync();
            }
            return RedirectToAction(nameof(ReviewNotes), new { year, entityId });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ReviewNoteDelete(int id, int year, int entityId)
        {
            var existing = await _db.EntityReviewNotes.FindAsync(id);
            if (existing != null)
            {
                if (!IsGlobalAdmin() && existing.EntityId != (GetEntityClaimId() ?? -1)) return Forbid();
                _db.EntityReviewNotes.Remove(existing);
                await _db.SaveChangesAsync();
            }
            return RedirectToAction(nameof(ReviewNotes), new { year, entityId });
        }

        // ---------------- Review narratives (Findings / Recommendations / 90-Day Plan) ----------------

        [HttpGet]
        public async Task<IActionResult> Narratives(int? year = null, int? id = null)
        {
            // Narratives are cross-entity editorial content: central (global) admins only.
            if (!IsGlobalAdmin()) return Forbid();
            var selectedYear = ResolveYear(year);
            ViewBag.Year = selectedYear;
            ViewBag.YearOptions = YearOptions(selectedYear);

            ReviewNarratives editing = new ReviewNarratives { BudgetYear = selectedYear, Period = DefaultPeriod, Section = "Finding" };
            if (id.HasValue)
            {
                var found = await _db.ReviewNarratives.FindAsync(id.Value);
                if (found != null) editing = found;
            }
            ViewBag.Editing = editing;

            var rows = await _db.ReviewNarratives.AsNoTracking()
                .Where(n => n.BudgetYear == selectedYear && n.Period == DefaultPeriod)
                .OrderBy(n => n.Section).ThenBy(n => n.SortOrder).ThenBy(n => n.ReviewNarrativeId)
                .ToListAsync();
            return View(rows);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> NarrativeSave(int reviewNarrativeId, int year, string section, string? title, string? body, string? owner, string? dueText, string? successMeasure, int sortOrder)
        {
            if (!IsGlobalAdmin()) return Forbid();
            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(body))
                return RedirectToAction(nameof(Narratives), new { year });

            if (reviewNarrativeId > 0)
            {
                var existing = await _db.ReviewNarratives.FindAsync(reviewNarrativeId);
                if (existing != null)
                {
                    existing.Section = section;
                    existing.Title = title;
                    existing.Body = body;
                    existing.Owner = owner;
                    existing.DueText = dueText;
                    existing.SuccessMeasure = successMeasure;
                    existing.SortOrder = sortOrder;
                    await _db.SaveChangesAsync();
                }
            }
            else
            {
                _db.ReviewNarratives.Add(new ReviewNarratives
                {
                    BudgetYear = year,
                    Period = DefaultPeriod,
                    Section = string.IsNullOrWhiteSpace(section) ? "Finding" : section,
                    Title = title,
                    Body = body,
                    Owner = owner,
                    DueText = dueText,
                    SuccessMeasure = successMeasure,
                    SortOrder = sortOrder,
                    CreatedBy = User.Identity?.Name
                });
                await _db.SaveChangesAsync();
            }
            return RedirectToAction(nameof(Narratives), new { year });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> NarrativeDelete(int id, int year)
        {
            if (!IsGlobalAdmin()) return Forbid();
            var existing = await _db.ReviewNarratives.FindAsync(id);
            if (existing != null)
            {
                _db.ReviewNarratives.Remove(existing);
                await _db.SaveChangesAsync();
            }
            return RedirectToAction(nameof(Narratives), new { year });
        }

        private async Task<List<SelectListItem>> EntityOptions(int? entityId)
        {
            // Entity admins only ever see (and are locked to) their own entity - no "All Entities".
            if (!IsGlobalAdmin())
            {
                var myId = GetEntityClaimId();
                return await _db.Entities.AsNoTracking()
                    .Where(e => myId.HasValue && e.EntityId == myId.Value)
                    .OrderBy(e => e.EntityCode)
                    .Select(e => new SelectListItem(e.EntityCode + " - " + e.EntityName, e.EntityId.ToString(), true))
                    .ToListAsync();
            }
            var entities = await _db.Entities.AsNoTracking()
                .OrderBy(e => e.EntityCode)
                .Select(e => new SelectListItem(e.EntityCode + " - " + e.EntityName, e.EntityId.ToString()))
                .ToListAsync();
            var options = new List<SelectListItem> { new SelectListItem("All Entities", "", !entityId.HasValue) };
            options.AddRange(entities);
            foreach (var o in options)
                if (entityId.HasValue && o.Value == entityId.Value.ToString()) o.Selected = true;
            return options;
        }

        // Status resolution mirrors ManagementReviewController: honor a manually set Status,
        // otherwise derive Green/Watch/Behind from progress = (actual - baseline)/(target - baseline).
        private static string ResolveKpiStatus(Kpis k)
        {
            if (!string.IsNullOrWhiteSpace(k.Status)) return k.Status!;
            return ComputeKpiStatus(k.Direction, k.Baseline, k.Target, k.ActualValue);
        }

        private static string ComputeKpiStatus(string? direction, decimal? baseline, decimal? target, decimal? actual)
        {
            if (baseline == null || target == null || actual == null) return "";
            var denom = target.Value - baseline.Value;
            decimal progress;
            if (denom == 0)
            {
                var up = !string.Equals(direction, "DOWN", StringComparison.OrdinalIgnoreCase);
                progress = up ? (actual.Value >= target.Value ? 1m : 0m) : (actual.Value <= target.Value ? 1m : 0m);
            }
            else
            {
                progress = (actual.Value - baseline.Value) / denom;
            }
            if (progress >= 0.5m) return "Green";
            if (progress >= 0.1m) return "Watch";
            return "Behind";
        }
    }

    public class KpiListRow
    {
        public long KpiId { get; set; }
        public string EntityLabel { get; set; } = "";
        public string ProgramCode { get; set; } = "";
        public string KpiCode { get; set; } = "";
        public string Priority { get; set; } = "";
        public string KpiName { get; set; } = "";
        public string Unit { get; set; } = "";
        public string KpiType { get; set; } = "";
        public string Dimension { get; set; } = "";
        public string ReadingType { get; set; } = "";
        public decimal? Baseline { get; set; }
        public decimal? Target { get; set; }
        public decimal? Actual { get; set; }
        public decimal? StrategicTarget2029 { get; set; }
        public string Status { get; set; } = "";
    }

    public class MaturityEditRow
    {
        public int EntityId { get; set; }
        public string EntityLabel { get; set; } = "";
        public decimal Stage { get; set; }
        public string Form { get; set; } = "";
        public string StatusLabel { get; set; } = "";
    }

    // ---- Programme > Activity > KPI tree ----
    public class KpiTreeVm
    {
        public int Year { get; set; }
        public List<SelectListItem> YearOptions { get; set; } = new();
        public List<SelectListItem> EntityOptions { get; set; } = new();
        public int? SelectedEntityId { get; set; }
        public bool NoAccess { get; set; }
        public List<KpiTreeEntity> Entities { get; set; } = new();
        public int ProgramCount { get; set; }
        public int ActivityCount { get; set; }
        public int KpiCount { get; set; }
    }

    public class KpiTreeEntity
    {
        public int EntityId { get; set; }
        public string EntityLabel { get; set; } = "";
        public List<KpiTreeProgram> Programs { get; set; } = new();
        public List<KpiTreeKpi> UnassignedKpis { get; set; } = new();
    }

    public class KpiTreeProgram
    {
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public List<KpiTreeActivity> Activities { get; set; } = new();
        public List<KpiTreeKpi> DirectKpis { get; set; } = new();
        public int KpiCount { get; set; }
    }

    public class KpiTreeActivity
    {
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public List<KpiTreeKpi> Kpis { get; set; } = new();
    }

    public class KpiTreeKpi
    {
        public string Name { get; set; } = "";
        public string Code { get; set; } = "";
        public string Type { get; set; } = "";
        public string Unit { get; set; } = "";
        public decimal? Target { get; set; }
        public decimal? Actual { get; set; }
        public string Status { get; set; } = "";
    }

    public class OutputEditRow
    {
        public int ActivityId { get; set; }
        public string ActivityLabel { get; set; } = "";
        public string ProgramCode { get; set; } = "";
        public string OutputMeasure { get; set; } = "";
        public decimal OutputVolume { get; set; }
    }

    public class ActivityRowProjection
    {
        public int ActivityId { get; set; }
        public string ActivityCode { get; set; } = "";
        public string ActivityName { get; set; } = "";
        public int EntityId { get; set; }
        public string ProgramCode { get; set; } = "";
    }
}
