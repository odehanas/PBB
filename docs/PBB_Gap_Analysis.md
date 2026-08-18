# GovBudget ↔ Management Deck — Gap Analysis & Integration Plan

**Reference:** `From Salim/PBB_Cross_Entity_Deck_v1` — *PBB Cross-Entity Performance & Maturity Review, Mid-Year FY2025*
**Scope:** 6 entities, 22 programmes, 67 activities, ~135 KPIs, 5 analytical dimensions.
**Status:** Analysis only — no changes to existing budget inputs, workflow, or reports.

---

## 1. Summary

GovBudget is a budget **preparation** system. The management deck is a **performance & maturity review** product. They share the same financial backbone (entities → programmes → activities → GL → HR/capex/opex, plus mid-year actuals), but the deck adds an entire **performance layer** (KPIs, outcomes, output volumes, unit costs, maturity staging) the system does not capture today.

Integration is therefore: **extend the model with an additive performance layer, then build management views on top — leaving budget inputs untouched.**

## 2. Current System Inventory

**Data model (`Models/GovBudgetContext.cs`, schema `core`):**
- Org: `Entities` → `Departments`; `Programs` → `Activities`; `Projects`
- Budget: `BudgetLines` (monthly M01–M12, forecasts F1/F2, `Category` REVENUE/CAPEX/OPEX, `Item` → `GLAccounts`, links to Program/Activity/Project), `BudgetLineDocuments`
- Workflow: `BudgetSubmissions` + `BudgetSubmissionLines` (Draft → Submitted → Approved → SentToCentral → Finalized; revision/return)
- Costing: `HrEmployeeCosts` + `HrEmployeeCostAllocations` (to activity/project), `HistoricalGlActuals`, `MidYearGlActualForecasts` (H1 actual + H2 forecast)
- Scenario: `WhatIfScenarios` (+ defaults, project rates)
- View: `vw_GL_CashBasis`

**Outputs (`Controllers/ReportsController.cs`):** income, gl, projects, activities, hralloc, entitysummary, trend — all with ClosedXML Excel export + print.
**Dashboard (`Controllers/HomeController.cs`):** cross-entity rollup, donut (HR/OPEX/CAPEX), headcount.

## 3. Gap Analysis

Bucket **A** = financial views feasible from existing data (no schema change).
Bucket **B** = performance/maturity layer needing additive tables + data capture.

| # | Deck requirement | GovBudget today | Gap | Bucket |
|---|---|---|---|---|
| 1 | Entity exec scorecard (Budget, FTE, status AHEAD/MIXED/BEHIND) | Home dashboard: budget + headcount | Status classification (needs KPIs/maturity) | A/B |
| 2 | Cost structure / "cost shape": Manpower / Consultancy / Maintenance / Other op / Capital (% of budget) | Categories REVENUE/CAPEX/OPEX + HR; donut | Sub-classification not first-class; derivable via GL/Item mapping | A |
| 3 | Capex discipline: capex budget vs mid-year actual, variance % | `MidYearGlActualForecasts` + budget; trend report | No dedicated capex variance view | A |
| 4 | Manpower: cost-per-FTE vs OECD band (250–450K) | `HrEmployeeCosts` + headcount | cost-per-FTE vs benchmark not presented | A |
| 5 | Programme cost = Direct + Allocated (Σ = entity budget) | Activity Costs (budget lines + allocated HR) | No formal shared-overhead allocation / Direct+Allocated split | A (needs rule) |
| 6 | Activities + output volume + cost-per-output | Activity Costs (AED only) | **No output/volume measure → no unit cost** | B |
| 7 | KPIs: baseline→target→mid-year, status, % green | — none — | **Entire KPI model absent** | B |
| 8 | KPI ↔ cost linkage: cost per unit/pp improvement | — none — | Depends on KPI + activity cost link | B |
| 9 | PBB maturity staging (1.0–4.0, OECD 4-form) | — none — | **No maturity model** | B |
| 10 | Entity profile narrative + key outcomes | — none — | No narrative/outcome storage | B (light) |
| 11 | Governance / RACI / cadence / 90-day plan | — n/a — | Pure narrative — not system data | Out of scope |

**Headline:** financial scaffolding is present and rich; the missing half is the **performance dimension** (KPIs, outputs, unit costs, maturity).

## 4. Feasibility

- **Low risk to current operations** — all changes are additive (new tables, new controllers/reports). No edits to `BudgetLines`, submissions, or existing reports.
- **Effort is mostly data, not code** — KPIs, output volumes, maturity scores must be captured/imported; without them Bucket B reports are empty shells.
- **Allocation rule needed** — "Direct + Allocated" programme cost needs an agreed methodology (today only HR is allocated to activities).
- Governance/RACI/90-day slides are narrative — out of application scope.

**Verdict: Feasible. Deliver Bucket A first (fast wins from existing data), then Bucket B as a phased performance module.**

## 5. Solution Design

### 5.1 Map existing data → outputs (no schema change)
- **Cost shape:** classify each `BudgetLines` row into Manpower (`HrEmployeeCosts`) / Capital (`CAPEX`) / Consultancy / Maintenance / Other, via a GL/Item → bucket lookup (small reference mapping).
- **Capex variance:** join budget CAPEX to `MidYearGlActualForecasts.ActualH1Amount` per entity/GL → variance %.
- **Cost-per-FTE:** `Σ HrEmployeeCosts.AnnualCost ÷ headcount` per entity vs configurable band.
- **Programme cost (Direct+Allocated):** Direct = lines tagged to programme/activities; Allocated = HR allocations + overhead rule. Validate Σ = Entity Budget Summary total.

### 5.2 Additive performance tables (`core`)
- `Kpis`, `ActivityOutputs`, `MaturityAssessments`, `KpiCostLinks`, `EntityReviewNotes` (see `docs/PBB_Phase0_Rules.md` and the migration script). All nullable/optional, FK to existing entities, no input changes.

### 5.3 Output enhancements
New "Management Review" report pack mirroring the existing `Build... + Build...Worksheet` ClosedXML pattern; plus an executive dashboard and a single consolidated "Cross-Entity Workbook" export.

### 5.4 Inputs unchanged
Budget entry, HR import, submissions, what-if untouched. Performance data enters via new dedicated screens/imports.

## 6. Implementation Plan (phased)

- **Phase 0 — Confirm rules (no code):** allocation driver, cost-shape GL mapping, KPI thresholds, maturity rubric. See `PBB_Phase0_Rules.md`.
- **Phase 1 — Bucket A reports:** Cost Structure, Capex Variance, Cost-per-FTE, Programme Cost (Direct+Allocated) + Excel. *Validate: Σ programme = entity total; capex actuals tie to MidYear.*
- **Phase 2 — Performance schema:** additive tables + EF models + migration.
- **Phase 3 — Performance data entry/import:** CRUD + Excel import (reuse HR-import pattern).
- **Phase 4 — Performance reports & exec dashboard:** KPI scorecard, maturity ladder, activity unit cost, KPI↔cost, cross-entity scorecard + consolidated workbook.
- **Phase 5 — Hardening:** role scoping, audit logging, regression test of existing reports.

**Testing strategy:** reconcile every new financial total to an existing report (same year/entity); snapshot-compare existing reports before/after each phase; seed one entity end-to-end and match it to a deck slide.

## 7. Recommendations

- Treat performance as a first-class module, versioned by `BudgetYear` + period (mid-year/year-end) for repeatable reviews.
- Formalize a configurable allocation engine (drivers: FTE, direct cost, transaction volume).
- Make cost-shape buckets and KPI/maturity thresholds configuration-driven, not hard-coded.
- Add a one-click consolidated "Cross-Entity Workbook" export.
- Keep governance/RACI/90-day content out of the app; optionally store entity review notes/outcomes year-over-year.
- Index new tables on (BudgetYear, EntityId) for Wave-2 scalability.
