/* ==========================================================================
   GovBudget - Combined Cost views for Power BI (and any external reporting)
   --------------------------------------------------------------------------
   These two views merge Budget Lines (REVENUE / OPEX / CAPEX) with HR costs
   into a single fact table each, exactly the way the built-in reports do:

     core.vw_CostByGL        -> one row per GL account (HR taken from the
                                imported per-employee salary costs).  Use this
                                for "cost per GL", Income Statement, totals.

     core.vw_CostByActivity  -> one row per Activity / Project (HR taken from
                                the HR ALLOCATIONS to activities).  Use this
                                for "cost per Activity" and "cost per Project".
                                This is the cost BEFORE cost-allocation (unchanged).

     core.vw_CostByActivity_AfterAllocation
                             -> same grain as vw_CostByActivity, PLUS the
                                step-down cost-allocation (Support -> Mandate)
                                from the latest Posted allocation run.  The
                                programme-level allocation is spread onto each
                                programme's activities pro-rata by their direct
                                cost in the same category.  Use this for
                                "Activity Costs AFTER reallocation".  It refreshes
                                automatically every time the allocation is re-run
                                (it always reads the latest Posted run).

   IMPORTANT - do not add both views' HR together, or HR is double counted:
     * vw_CostByGL       uses HR *imported*  (full salary cost, no activity)
     * vw_CostByActivity uses HR *allocated* (only the part spread to activities)

   CostType column values: REVENUE, OPEX, CAPEX, HR.
   Source column values: Budget, HR-Imported, HR-Allocated, Allocation-In, Allocation-Out.
     * Allocation-In / Allocation-Out rows appear ONLY in the After-Allocation
       view.  They net to zero within an entity (a reallocation moves cost, it
       does not add cost), and CAPEX is never reallocated.

   HOW TO RUN
     1. In SSMS / Azure Data Studio: select your GovBudget database in the
        dropdown, then run this whole script (F5).
     2. On a web SQL panel that rejects GO: this script uses EXEC(N'...') so it
        has NO GO batch separators and can be pasted and run as one statement.
   This script is idempotent - re-running it just refreshes the views.
   ========================================================================== */

/* -------------------------------------------------------------------------
   1) core.vw_CostByGL  -  all costs grouped by GL account
   ------------------------------------------------------------------------- */
EXEC(N'
CREATE OR ALTER VIEW core.vw_CostByGL AS
    /* Budget lines: REVENUE / OPEX / CAPEX (HR budget lines are excluded so
       that HR is sourced only from the HR tables and never double counted). */
    SELECT
        b.BudgetYear,
        b.EntityId,     e.EntityCode,  e.EntityName,
        b.DepartmentId, d.DeptCode,    d.DeptName,
        cat.CategoryCode                    AS CostType,
        gl.GLCode, gl.GLName, gl.GLType,
        b.Amount,
        b.M01, b.M02, b.M03, b.M04, b.M05, b.M06,
        b.M07, b.M08, b.M09, b.M10, b.M11, b.M12,
        b.F1_Amount                         AS Forecast1Amount,
        b.F2_Amount                         AS Forecast2Amount,
        CAST(N''Budget'' AS nvarchar(20))   AS Source
    FROM core.BudgetLines  b
    JOIN core.Categories   cat ON cat.CategoryId  = b.CategoryId
    JOIN core.Items        it  ON it.ItemId       = b.ItemId
    JOIN core.GLAccounts   gl  ON gl.GLAccountId  = it.GLAccountId
    JOIN core.Entities     e   ON e.EntityId      = b.EntityId
    JOIN core.Departments  d   ON d.DepartmentId  = b.DepartmentId
    WHERE cat.CategoryCode <> N''HR''

    UNION ALL

    /* HR imported salary cost, aggregated per GL (matches GL Summary report). */
    SELECT
        emp.BudgetYear,
        emp.EntityId,     e.EntityCode,  e.EntityName,
        emp.DepartmentId, d.DeptCode,    d.DeptName,
        N''HR''                             AS CostType,
        emp.GLCode,
        MAX(gl.GLName)                      AS GLName,
        MAX(gl.GLType)                      AS GLType,
        SUM(emp.AnnualCost)                 AS Amount,
        SUM(emp.AnnualCost)/12.0, SUM(emp.AnnualCost)/12.0, SUM(emp.AnnualCost)/12.0,
        SUM(emp.AnnualCost)/12.0, SUM(emp.AnnualCost)/12.0, SUM(emp.AnnualCost)/12.0,
        SUM(emp.AnnualCost)/12.0, SUM(emp.AnnualCost)/12.0, SUM(emp.AnnualCost)/12.0,
        SUM(emp.AnnualCost)/12.0, SUM(emp.AnnualCost)/12.0, SUM(emp.AnnualCost)/12.0,
        SUM(emp.AnnualCost)                 AS Forecast1Amount,
        SUM(emp.AnnualCost)                 AS Forecast2Amount,
        CAST(N''HR-Imported'' AS nvarchar(20)) AS Source
    FROM core.HrEmployeeCosts emp
    LEFT JOIN core.GLAccounts  gl ON gl.GLCode      = emp.GLCode
    LEFT JOIN core.Entities    e  ON e.EntityId     = emp.EntityId
    LEFT JOIN core.Departments d  ON d.DepartmentId = emp.DepartmentId
    GROUP BY
        emp.BudgetYear, emp.EntityId, e.EntityCode, e.EntityName,
        emp.DepartmentId, d.DeptCode, d.DeptName, emp.GLCode;
');

/* -------------------------------------------------------------------------
   2) core.vw_CostByActivity  -  all costs grouped by Activity / Project
   ------------------------------------------------------------------------- */
EXEC(N'
CREATE OR ALTER VIEW core.vw_CostByActivity AS
    /* Budget lines with their Programme / Activity / Project. */
    SELECT
        b.BudgetYear,
        b.EntityId,     e.EntityCode,  e.EntityName,
        b.DepartmentId, d.DeptCode,    d.DeptName,
        cat.CategoryCode                    AS CostType,
        gl.GLCode, gl.GLName, gl.GLType,
        prog.ProgramId, prog.ProgramCode, prog.ProgramName, prog.ProgramType,
        act.ActivityId, act.ActivityCode, act.ActivityName,
        b.ProjectId, proj.ProjectCode, proj.ProjectName,
        b.Amount,
        CAST(N''Budget'' AS nvarchar(20))   AS Source
    FROM core.BudgetLines  b
    JOIN core.Categories   cat ON cat.CategoryId  = b.CategoryId
    JOIN core.Items        it  ON it.ItemId       = b.ItemId
    JOIN core.GLAccounts   gl  ON gl.GLAccountId  = it.GLAccountId
    JOIN core.Entities     e   ON e.EntityId      = b.EntityId
    JOIN core.Departments  d   ON d.DepartmentId  = b.DepartmentId
    LEFT JOIN core.Activities act  ON act.ActivityId = b.ActivityId
    LEFT JOIN core.Programs   prog ON prog.ProgramId = COALESCE(b.ProgramId, act.ProgramId)
    LEFT JOIN core.Projects   proj ON proj.ProjectId = b.ProjectId
    WHERE cat.CategoryCode <> N''HR''

    UNION ALL

    /* HR cost ALLOCATED to activities / projects (matches Activity & Project
       Cost reports). Only the allocated portion of salary appears here. */
    SELECT
        emp.BudgetYear,
        emp.EntityId,     e.EntityCode,  e.EntityName,
        act.DepartmentId, d.DeptCode,    d.DeptName,
        N''HR''                             AS CostType,
        emp.GLCode, gl.GLName, gl.GLType,
        prog.ProgramId, prog.ProgramCode, prog.ProgramName, prog.ProgramType,
        act.ActivityId, act.ActivityCode, act.ActivityName,
        a.ProjectId, proj.ProjectCode, proj.ProjectName,
        a.AllocatedAmount                   AS Amount,
        CAST(N''HR-Allocated'' AS nvarchar(20)) AS Source
    FROM core.HrEmployeeCostAllocations a
    JOIN core.HrEmployeeCosts emp ON emp.EmployeeCostId = a.EmployeeCostId
    JOIN core.Activities      act ON act.ActivityId     = a.ActivityId
    JOIN core.Programs        prog ON prog.ProgramId     = act.ProgramId
    LEFT JOIN core.Projects   proj ON proj.ProjectId     = a.ProjectId
    LEFT JOIN core.GLAccounts gl  ON gl.GLCode           = emp.GLCode
    LEFT JOIN core.Entities   e   ON e.EntityId          = emp.EntityId
    LEFT JOIN core.Departments d  ON d.DepartmentId      = act.DepartmentId;
');

/* -------------------------------------------------------------------------
   3) core.vw_CostByActivity_AfterAllocation
      Activity/Project cost AFTER the step-down cost allocation.

      = every row of vw_CostByActivity (the BEFORE-allocation cost, unchanged)
        PLUS Allocation-In / Allocation-Out rows from the latest Posted run.

      The allocation is stored at the (programme, category) grain, so it is
      spread onto each programme's activities pro-rata by their direct cost in
      the SAME category. Where a receiving programme has no direct cost in that
      category, the amount is parked on a programme-level row (ActivityId NULL)
      so the totals still reconcile. Allocation-In/Out net to zero per entity.

      Always reads the LATEST Posted run per (BudgetYear, EntityId), so it
      refreshes automatically whenever the allocation is re-run.
   ------------------------------------------------------------------------- */
EXEC(N'
CREATE OR ALTER VIEW core.vw_CostByActivity_AfterAllocation AS
    WITH Base AS (
        SELECT * FROM core.vw_CostByActivity
    ),
    /* Latest Posted run per (year, entity) that actually produced transactions. */
    RunRank AS (
        SELECT r.RunId, x.BudgetYear, x.EntityId,
               ROW_NUMBER() OVER (PARTITION BY x.BudgetYear, x.EntityId
                                  ORDER BY r.RunAt DESC, r.RunId DESC) AS rn
        FROM core.AllocationRuns r
        JOIN (SELECT DISTINCT RunId, BudgetYear, EntityId
              FROM core.AllocationTransactions) x ON x.RunId = r.RunId
        WHERE r.Status = N''Posted''
    ),
    LatestRun AS (
        SELECT RunId, BudgetYear, EntityId FROM RunRank WHERE rn = 1
    ),
    Txn AS (
        SELECT tx.BudgetYear, tx.EntityId,
               UPPER(tx.SourceCategoryCode) AS CostType,
               tx.SourceProgramId, tx.TargetProgramId, tx.Amount
        FROM core.AllocationTransactions tx
        JOIN LatestRun lr ON lr.RunId = tx.RunId
                         AND lr.BudgetYear = tx.BudgetYear
                         AND lr.EntityId  = tx.EntityId
    ),
    /* Net movement per programme & category: + to target (Mandate), - from source (Support). */
    ProgNet AS (
        SELECT BudgetYear, EntityId, TargetProgramId AS ProgramId, CostType,
               SUM(Amount) AS NetAmount, CAST(N''Allocation-In'' AS nvarchar(20)) AS Source
        FROM Txn
        GROUP BY BudgetYear, EntityId, TargetProgramId, CostType
        UNION ALL
        SELECT BudgetYear, EntityId, SourceProgramId AS ProgramId, CostType,
               -SUM(Amount) AS NetAmount, CAST(N''Allocation-Out'' AS nvarchar(20)) AS Source
        FROM Txn
        GROUP BY BudgetYear, EntityId, SourceProgramId, CostType
    ),
    /* Direct cost per programme + activity + category (the pro-rata weights). */
    ActBase AS (
        SELECT BudgetYear, EntityId, ProgramId, ActivityId, ActivityCode, ActivityName,
               CostType, SUM(Amount) AS ActAmount
        FROM Base
        WHERE ActivityId IS NOT NULL
        GROUP BY BudgetYear, EntityId, ProgramId, ActivityId, ActivityCode, ActivityName, CostType
    ),
    ProgBase AS (
        SELECT BudgetYear, EntityId, ProgramId, CostType, SUM(ActAmount) AS ProgAmount
        FROM ActBase
        GROUP BY BudgetYear, EntityId, ProgramId, CostType
    )

    /* (a) all BEFORE-allocation rows, unchanged. */
    SELECT
        BudgetYear, EntityId, EntityCode, EntityName,
        DepartmentId, DeptCode, DeptName,
        CostType, GLCode, GLName, GLType,
        ProgramId, ProgramCode, ProgramName, ProgramType,
        ActivityId, ActivityCode, ActivityName,
        ProjectId, ProjectCode, ProjectName,
        Amount, Source
    FROM Base

    UNION ALL

    /* (b) allocation spread onto activities pro-rata by direct cost in the same category. */
    SELECT
        pn.BudgetYear, pn.EntityId, e.EntityCode, e.EntityName,
        CAST(NULL AS int), CAST(NULL AS nvarchar(4000)), CAST(NULL AS nvarchar(4000)),
        pn.CostType,
        CAST(NULL AS nvarchar(4000)), CAST(NULL AS nvarchar(4000)), CAST(NULL AS nvarchar(4000)),
        p.ProgramId, p.ProgramCode, p.ProgramName, p.ProgramType,
        ab.ActivityId, ab.ActivityCode, ab.ActivityName,
        CAST(NULL AS int), CAST(NULL AS nvarchar(4000)), CAST(NULL AS nvarchar(4000)),
        pn.NetAmount * ab.ActAmount / pb.ProgAmount AS Amount,
        pn.Source
    FROM ProgNet pn
    JOIN ProgBase pb ON pb.BudgetYear = pn.BudgetYear AND pb.EntityId = pn.EntityId
                    AND pb.ProgramId  = pn.ProgramId  AND pb.CostType = pn.CostType
                    AND pb.ProgAmount <> 0
    JOIN ActBase ab  ON ab.BudgetYear = pn.BudgetYear AND ab.EntityId = pn.EntityId
                    AND ab.ProgramId  = pn.ProgramId  AND ab.CostType = pn.CostType
    JOIN core.Programs p ON p.ProgramId = pn.ProgramId
    JOIN core.Entities e ON e.EntityId  = pn.EntityId

    UNION ALL

    /* (c) allocation with no distributable direct cost in that category -> programme-level row. */
    SELECT
        pn.BudgetYear, pn.EntityId, e.EntityCode, e.EntityName,
        CAST(NULL AS int), CAST(NULL AS nvarchar(4000)), CAST(NULL AS nvarchar(4000)),
        pn.CostType,
        CAST(NULL AS nvarchar(4000)), CAST(NULL AS nvarchar(4000)), CAST(NULL AS nvarchar(4000)),
        p.ProgramId, p.ProgramCode, p.ProgramName, p.ProgramType,
        CAST(NULL AS int), CAST(NULL AS nvarchar(4000)), CAST(NULL AS nvarchar(4000)),
        CAST(NULL AS int), CAST(NULL AS nvarchar(4000)), CAST(NULL AS nvarchar(4000)),
        pn.NetAmount AS Amount,
        pn.Source
    FROM ProgNet pn
    JOIN core.Programs p ON p.ProgramId = pn.ProgramId
    JOIN core.Entities e ON e.EntityId  = pn.EntityId
    LEFT JOIN ProgBase pb ON pb.BudgetYear = pn.BudgetYear AND pb.EntityId = pn.EntityId
                         AND pb.ProgramId  = pn.ProgramId  AND pb.CostType = pn.CostType
                         AND pb.ProgAmount <> 0
    WHERE pb.ProgramId IS NULL;
');
