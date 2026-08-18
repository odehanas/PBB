# PBB Phase 0 — Business Rules & Decisions (for sign-off)

These are the rules the management-review reports depend on. Proposed defaults are derived from the deck and from how GovBudget already models data. **Please confirm or adjust each before Phase 1/Phase 4 reports are finalized.**

---

## R1. Cost-Shape Classification ("cost shape")

The deck buckets spend into: **Manpower, Consultancy, Maintenance, Other operating, Capital** (per IMF GFSM 2014).

Proposed mapping rule (evaluated in order):
1. **Manpower** = all `HrEmployeeCosts` (annual cost) + any `BudgetLines` whose `Category = HR`-type GL.
2. **Capital** = `BudgetLines` where `Category.CategoryCode = CAPEX`.
3. **Consultancy / Maintenance / Other operating** = subdivide `OPEX` using a GL/Item → bucket lookup table.

**Decision needed:** the GL→bucket mapping. Default proposal — map by `GLAccounts.GLCode` prefix or name keyword:

| Bucket | Match rule (default) |
|---|---|
| Consultancy | GL name contains "consult", "advisory", "professional fees" |
| Maintenance | GL name contains "maintenance", "repair", "upkeep" |
| Other operating | all remaining OPEX |

> Open question: do you have a canonical chart-of-accounts mapping (e.g. from the "Chart of Account Project" share) we should use instead of keyword matching? If so, we'll load it into a `CostShapeMap` reference table.

## R2. Programme Cost — Direct vs Allocated

Deck: each programme = **Direct + Allocated**, and Σ programme costs = entity total budget.

- **Direct** = `BudgetLines` (CAPEX+OPEX) directly tagged to the programme (via `ProgramId` or via `Activity.ProgramId`) + HR directly allocated to that programme's activities (`HrEmployeeCostAllocations`).
- **Allocated** = shared/overhead spend not directly tagged, distributed across programmes by a driver.

**Decision needed — allocation driver (pick one):**
- (a) **Direct cost share** *(proposed default)* — overhead split in proportion to each programme's direct cost.
- (b) **FTE share** — split by allocated headcount per programme.
- (c) **Transaction/output volume** — requires `ActivityOutputs` (Phase 2+).

**Decision needed — overhead pool definition:** which spend counts as "shared overhead"? Default proposal: budget lines with no `ProgramId` and no `ActivityId` (i.e. untagged entity/department-level spend).

## R3. KPI Status Thresholds

Deck uses **Green / Watch / Behind** and an "% on-track" headline.

Proposed default (direction-aware; "higher is better" shown, inverted for "lower is better"):
- **Green / On-track:** mid-year actual ≥ (baseline + 0.5 × (target − baseline)) — i.e. ≥ 50% of the way to target by mid-year, or already ≥ target.
- **Watch:** between 10% and 50% of the way to target.
- **Behind:** < 10% progress, no movement, or worsening.

**Decision needed:** confirm thresholds, or provide the exact mid-year banding management uses. Store as configurable values, not hard-coded.

## R4. PBB Maturity Rubric

Deck stages entities **1.0–4.0** on the OECD GOV/SBO(2023)1 4-form taxonomy: **Presentational (1) / Performance-Informed (2) / Managerial (3) / Direct (4)**.

Proposed model: `MaturityAssessments` stores a single `Stage` (decimal, e.g. 2.5) + `Form` label + optional per-dimension scores (the deck mentions "12 dimensions"). Stage is **assessed/entered** (not auto-computed) and validated by the review team.

**Decision needed:** the list of maturity dimensions to capture (if you want dimension-level scoring), or whether a single overall stage per entity/year is sufficient for now.

## R5. Cost-per-FTE Benchmark Band

Deck: OECD band **250–450K AED** per FTE.

Proposed: store min/max band as configuration; compute `Σ AnnualCost ÷ distinct EmployeeId headcount` per entity; flag inside/outside band.

**Decision needed:** confirm 250–450K, or per-grade bands.

## R6. Activity Output & Unit Cost

Deck: each activity has an **Annual Output** (volume + measure) and **Cost / Output**.

Proposed: `ActivityOutputs(ActivityId, BudgetYear, OutputMeasure, OutputVolume)`; unit cost = activity total cost ÷ volume. One primary output per activity/year (additional outputs optional).

**Decision needed:** is one primary output measure per activity sufficient, or do some activities need multiple output measures?

## R7. Review Period

Deck is **Mid-Year FY2025**. Performance data (KPI actuals, capex actuals, maturity) should be tagged with a **period** (e.g. `MidYear`, `YearEnd`) and `BudgetYear` so reviews are repeatable each cycle.

**Decision needed:** confirm the set of periods (proposed: `MidYear`, `YearEnd`).

---

## Default assumptions applied if no change requested
Unless you say otherwise, Phase 1 will use: **R1** keyword GL mapping, **R2(a)** direct-cost-share allocation with untagged spend as the overhead pool, **R5** 250–450K band. R3/R4/R6/R7 affect Phase 4 (performance) and will be confirmed before that build.
