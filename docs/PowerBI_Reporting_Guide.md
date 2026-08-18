# Power BI Reporting Guide - Combining All Expenses (Revenue, OPEX, CAPEX and HR)

This guide explains how to build reports in Power BI that show **all cost types together** - the way the system's built-in reports do - when you connect directly to the GovBudget SQL Server database.

---

## 1. Why HR does not "just appear" with the others

When you look at `core.BudgetLines` you see **REVENUE, OPEX and CAPEX**, but **not HR**. That is by design:

| Cost type | Where it lives | Links to GL by | Links to Activity by |
| --- | --- | --- | --- |
| REVENUE / OPEX / CAPEX | `core.BudgetLines` | `Items.GLAccountId -> GLAccounts` | `BudgetLines.ActivityId` (direct) |
| HR (salary) | `core.HrEmployeeCosts` | `HrEmployeeCosts.GLCode` (a text code, **no** foreign key) | `core.HrEmployeeCostAllocations` (allocations to activities/projects) |

So HR is a **separate fact table** with a **different shape**. To report on "all expenses" you must bring the two together into one common structure. That is exactly what the built-in reports do internally (in `ReportsController.GetLedgerEntries`), producing a unified row with a `CategoryCode` of `REVENUE` / `OPEX` / `CAPEX` / **`HR`**.

**One rule to remember - never double-count HR:**
- HR salary has a *total* figure per employee (`HrEmployeeCosts.AnnualCost`).
- The same salary can also be *allocated* to activities (`HrEmployeeCostAllocations.AllocatedAmount`).
- Use **one** of them, never both, in the same total.

---

## 2. The easy way (recommended): two ready-made SQL views

Run the script **`docs/PowerBI_CombinedCost_Views.sql`** once against your GovBudget database. It creates two views that already merge everything:

| View | HR source | Use it for |
| --- | --- | --- |
| `core.vw_CostByGL` | HR **imported** (full salary, per GL) | Cost per GL account, Income Statement, grand totals |
| `core.vw_CostByActivity` | HR **allocated** (per activity/project) | Cost per Activity, cost per Project, cost per Programme |

Both views expose a single **`CostType`** column with values `REVENUE`, `OPEX`, `CAPEX`, `HR` - so grouping "all expenses" becomes trivial.

### Connect Power BI
1. **Home -> Get data -> SQL Server**.
2. Server = your GovBudget server, Database = your GovBudget database.
3. Choose **DirectQuery** (live) or **Import** (faster, snapshot) - either is fine.
4. In the Navigator, tick **`core.vw_CostByGL`** and/or **`core.vw_CostByActivity`** and load.

### Build the "all expenses" report
- **Cost per GL** (a matrix visual):
  - Rows: `GLType`, then `GLCode` + `GLName`
  - Columns: `CostType`
  - Values: `Sum of Amount`
  - Slicers: `BudgetYear`, `EntityName`
  - Table source: `vw_CostByGL`
- **Cost per Activity** (a matrix visual):
  - Rows: `ProgramName`, then `ActivityCode` + `ActivityName`
  - Columns: `CostType`
  - Values: `Sum of Amount`
  - Table source: `vw_CostByActivity`

Because HR is already inside these views as `CostType = "HR"`, it shows up next to REVENUE/OPEX/CAPEX automatically.

### A "Net (Revenue - Expenses)" measure
The system treats REVENUE as income and everything else (OPEX, CAPEX, HR) as expense. Create this DAX measure on `vw_CostByGL`:

```DAX
Net Amount =
VAR Rev = CALCULATE ( SUM ( vw_CostByGL[Amount] ), vw_CostByGL[CostType] = "REVENUE" )
VAR Exp = CALCULATE ( SUM ( vw_CostByGL[Amount] ), vw_CostByGL[CostType] IN { "OPEX", "CAPEX", "HR" } )
RETURN Rev - Exp
```

Helpful extra measures:

```DAX
Total Revenue  = CALCULATE ( SUM ( vw_CostByGL[Amount] ), vw_CostByGL[CostType] = "REVENUE" )
Total Expenses = CALCULATE ( SUM ( vw_CostByGL[Amount] ), vw_CostByGL[CostType] IN { "OPEX", "CAPEX", "HR" } )
Total HR       = CALCULATE ( SUM ( vw_CostByGL[Amount] ), vw_CostByGL[CostType] = "HR" )
```

---

## 3. The no-database-change way: do it in Power Query (Append)

If you cannot (or prefer not to) add views, build the same union inside Power BI using **Append Queries**. This mirrors `vw_CostByGL`.

1. **Get data -> SQL Server** and load these `core` tables: `BudgetLines`, `Categories`, `Items`, `GLAccounts`, `Entities`, `Departments`, `HrEmployeeCosts` (and for activity-level also `HrEmployeeCostAllocations`, `Activities`, `Programs`, `Projects`).
2. Create query **Budget_Costs**:
   - Start from `BudgetLines`.
   - Merge `Categories` (on `CategoryId`) -> expand `CategoryCode`.
   - Merge `Items` (on `ItemId`) -> expand `GLAccountId`, then merge `GLAccounts` -> expand `GLCode`, `GLName`, `GLType`.
   - Merge `Entities` / `Departments` for names.
   - **Filter** `CategoryCode <> "HR"`.
   - Add a column `CostType = [CategoryCode]`.
   - Keep: `BudgetYear, EntityName, DeptName, CostType, GLCode, GLName, GLType, Amount`.
3. Create query **HR_Costs**:
   - Start from `HrEmployeeCosts`.
   - **Group By** `BudgetYear, EntityId, DepartmentId, GLCode` with an aggregation `Amount = Sum(AnnualCost)`.
   - Merge `GLAccounts` on `GLCode` -> expand `GLName`, `GLType`. Merge `Entities`/`Departments` for names.
   - Add a column `CostType = "HR"`.
   - Keep the **same columns in the same order** as `Budget_Costs`.
4. Create query **All_Costs** = **Home -> Append Queries -> Append `Budget_Costs` and `HR_Costs`**.
5. Build your matrix on **All_Costs** exactly as in Section 2.

> Tip: to reproduce `vw_CostByActivity` in Power Query, use `HrEmployeeCostAllocations` (join to `HrEmployeeCosts`, `Activities`, `Programs`, `Projects`) with `Amount = AllocatedAmount` instead of the grouped `HrEmployeeCosts`.

---

## 4. If you'd rather model the raw tables directly

Power BI auto-detects most relationships from the database foreign keys:
`BudgetLines` -> `Categories`, `Items`, `Entities`, `Departments`, `Activities`, `Projects`; `Items` -> `GLAccounts`; `HrEmployeeCostAllocations` -> `HrEmployeeCosts`, `Activities`, `Projects`.

Two things you must set up **manually**, because there is no foreign key for them:
- **HR -> GL:** create a relationship between `HrEmployeeCosts[GLCode]` and `GLAccounts[GLCode]` (Manage relationships -> New). It is a text key, so make sure `GLCode` values match on both sides.
- **A shared `CostType` dimension:** the raw tables do not have a single column that spans budget + HR. This is exactly why the appended query / SQL view in Sections 2-3 is the cleaner approach. If you insist on raw tables, you will end up writing DAX like `Total Cost = SUM(BudgetLines[Amount]) + SUM(HrEmployeeCosts[AnnualCost])`, which is harder to slice by cost type than a unified fact table.

**Recommendation:** use the unified view/append (Sections 2-3) as your fact table, and keep `Entities`, `Departments`, `GLAccounts`, `Programs`, `Activities`, `Projects` as dimension tables for slicers.

---

## 5. Cross-checking against the built-in reports

To confirm your Power BI numbers match the app:
- `vw_CostByGL` totals per `CostType` should equal the **GL Summary** / **Income Statement** reports.
- `vw_CostByActivity` totals per activity should equal the **Activity Cost** / **Project Cost** reports.
- If HR looks too small in `vw_CostByActivity`, that is expected when not all salary has been allocated to activities yet - use the **HR Allocation Variances** report in the app to find and fix under/over-allocated employees, or use `vw_CostByGL` for the full HR figure.

---

## 6. Quick reference - `CostType` values

| CostType | Meaning | Sign in "Net" |
| --- | --- | --- |
| `REVENUE` | Income budget lines | + |
| `OPEX` | Operating expense budget lines | - |
| `CAPEX` | Capital expense budget lines | - |
| `HR` | Salary cost (imported or allocated) | - |
