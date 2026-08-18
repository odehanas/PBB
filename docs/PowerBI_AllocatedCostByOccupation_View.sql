/* ==========================================================================
   GovBudget - Allocated HR cost per Occupation (for Power BI / reporting)
   --------------------------------------------------------------------------
   Occupation (job title) is stored on core.HrEmployeeCosts. The cost that has
   been spread to activities / projects lives in core.HrEmployeeCostAllocations
   (AllocatedAmount). These views group that allocated cost by Occupation.

     core.vw_AllocatedCostByOccupation
         -> detail grain: one row per (Year, Entity, Occupation, Programme,
            Activity, Project). Use it to slice allocated HR cost by job title
            across programmes / activities / projects.

     core.vw_AllocatedCostByOccupation_Summary
         -> one row per (Year, Entity, Occupation) with EmployeeCount,
            TotalAnnualCost (imported salary), AllocatedCost and the resulting
            UnallocatedCost (= TotalAnnualCost - AllocatedCost).

   NOTES
     * Only the ALLOCATED portion of salary appears in AllocatedCost; the full
       imported salary is TotalAnnualCost (Summary view only).
     * Employees with no Occupation captured are grouped under '(Unspecified)'.
     * PREREQUISITE: run docs/AddHrOccupation.sql first (adds the Occupation
       column) otherwise these views will fail to create.

   HOW TO RUN
     - Select the GovBudget database (db_ac6910_govbudget) and run the whole
       script. It uses EXEC(N'...') so there are NO GO separators and it can be
       pasted into a web SQL panel. Idempotent - re-running refreshes the views.
   ========================================================================== */

/* -------------------------------------------------------------------------
   1) Detail: allocated HR cost by Occupation x Programme / Activity / Project
   ------------------------------------------------------------------------- */
EXEC(N'
CREATE OR ALTER VIEW core.vw_AllocatedCostByOccupation AS
    SELECT
        emp.BudgetYear,
        emp.EntityId, e.EntityCode, e.EntityName,
        COALESCE(NULLIF(LTRIM(RTRIM(emp.Occupation)), N''''), N''(Unspecified)'') AS Occupation,
        prog.ProgramId, prog.ProgramCode, prog.ProgramName, prog.ProgramType,
        act.ActivityId, act.ActivityCode, act.ActivityName,
        a.ProjectId, proj.ProjectCode, proj.ProjectName,
        COUNT(DISTINCT emp.EmployeeCostId)  AS EmployeeCount,
        SUM(a.AllocatedAmount)              AS AllocatedCost
    FROM core.HrEmployeeCostAllocations a
    JOIN core.HrEmployeeCosts  emp ON emp.EmployeeCostId = a.EmployeeCostId
    JOIN core.Activities       act ON act.ActivityId     = a.ActivityId
    JOIN core.Programs        prog ON prog.ProgramId      = act.ProgramId
    LEFT JOIN core.Projects   proj ON proj.ProjectId      = a.ProjectId
    LEFT JOIN core.Entities   e    ON e.EntityId          = emp.EntityId
    GROUP BY
        emp.BudgetYear,
        emp.EntityId, e.EntityCode, e.EntityName,
        COALESCE(NULLIF(LTRIM(RTRIM(emp.Occupation)), N''''), N''(Unspecified)''),
        prog.ProgramId, prog.ProgramCode, prog.ProgramName, prog.ProgramType,
        act.ActivityId, act.ActivityCode, act.ActivityName,
        a.ProjectId, proj.ProjectCode, proj.ProjectName;
');

/* -------------------------------------------------------------------------
   2) Summary: one row per (Year, Entity, Occupation)
   ------------------------------------------------------------------------- */
EXEC(N'
CREATE OR ALTER VIEW core.vw_AllocatedCostByOccupation_Summary AS
    WITH Emp AS (
        SELECT
            emp.BudgetYear, emp.EntityId,
            COALESCE(NULLIF(LTRIM(RTRIM(emp.Occupation)), N''''), N''(Unspecified)'') AS Occupation,
            COUNT(*)            AS EmployeeCount,
            SUM(emp.AnnualCost) AS TotalAnnualCost
        FROM core.HrEmployeeCosts emp
        GROUP BY emp.BudgetYear, emp.EntityId,
                 COALESCE(NULLIF(LTRIM(RTRIM(emp.Occupation)), N''''), N''(Unspecified)'')
    ),
    Alloc AS (
        SELECT
            emp.BudgetYear, emp.EntityId,
            COALESCE(NULLIF(LTRIM(RTRIM(emp.Occupation)), N''''), N''(Unspecified)'') AS Occupation,
            SUM(a.AllocatedAmount) AS AllocatedCost
        FROM core.HrEmployeeCostAllocations a
        JOIN core.HrEmployeeCosts emp ON emp.EmployeeCostId = a.EmployeeCostId
        GROUP BY emp.BudgetYear, emp.EntityId,
                 COALESCE(NULLIF(LTRIM(RTRIM(emp.Occupation)), N''''), N''(Unspecified)'')
    )
    SELECT
        COALESCE(em.BudgetYear, al.BudgetYear)  AS BudgetYear,
        COALESCE(em.EntityId, al.EntityId)      AS EntityId,
        e.EntityCode, e.EntityName,
        COALESCE(em.Occupation, al.Occupation)  AS Occupation,
        ISNULL(em.EmployeeCount, 0)             AS EmployeeCount,
        ISNULL(em.TotalAnnualCost, 0)           AS TotalAnnualCost,
        ISNULL(al.AllocatedCost, 0)             AS AllocatedCost,
        ISNULL(em.TotalAnnualCost, 0) - ISNULL(al.AllocatedCost, 0) AS UnallocatedCost
    FROM Emp em
    FULL OUTER JOIN Alloc al
        ON  al.BudgetYear = em.BudgetYear
        AND al.EntityId   = em.EntityId
        AND al.Occupation = em.Occupation
    LEFT JOIN core.Entities e ON e.EntityId = COALESCE(em.EntityId, al.EntityId);
');
