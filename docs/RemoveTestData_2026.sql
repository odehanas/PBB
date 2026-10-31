-- ==========================================================================
-- GovBudget - Remove pilot/test data (RDOF, RHRD) and test budget submissions
-- --------------------------------------------------------------------------
-- WHAT THIS REMOVES
--   * 9  HrEmployeeCosts rows   - "Sample Employee", ABC1..ABC6 (RDOF + RHRD)
--   * 14 HrEmployeeCostAllocations rows - children of the above
--   * 6  BudgetSubmissions      - all RDOF, all test (3 Returned + 3 Draft v2)
--   * 3  BudgetRevisionRequests - children of the above
--   * 3  DOF_CombindBudget_Final - the entire table, all test rows
--
-- WHAT THIS KEEPS (deliberately)
--   * The RDOF and RHRD entities themselves, their Departments (2),
--     Programs (2), BudgetLines (3) and AppUsers (4).
--   * All 470 real Customs (RCUD) and Antiquities (RDAM) employees and their
--     876 allocations.
--
-- IDENTITY IS NOT RESEEDED - ON PURPOSE
--   The 9 test rows occupy EmployeeCostId 1-9, but the table runs 1-479 with
--   no gaps, so IDs 10-479 are real employees. Reseeding the identity to 0
--   would hand IDs 1-9 to the next nine imports and then collide with real
--   employee 10 on a primary key violation. EmployeeCostId is a surrogate key
--   that is never shown to a user; the real business key is the unique index
--   on (BudgetYear, EmployeeId), which stays clean. Gaps are harmless.
--
-- HOW TO RUN
--   Paste into the SmarterASP SQL console and run. Written as plain numbered
--   statements: no GO, no transactions, no PRINT, no EXEC, no block comments.
--   Statements are ordered so foreign keys are never violated.
--   TAKE A DATABASE BACKUP FIRST.
-- ==========================================================================

-- 1) Final combined budget rows (children of BudgetSubmissions). Whole table.
DELETE FROM core.DOF_CombindBudget_Final;

-- 2) Revision requests (children of BudgetSubmissions).
DELETE FROM core.BudgetRevisionRequests;

-- 3) Submission lines (currently 0 rows, included for safety).
DELETE FROM core.BudgetSubmissionLines;

-- 4) Version-2 drafts first: BudgetSubmissions has a self-referencing FK
--    (ParentSubmissionId), so children must go before parents.
DELETE FROM core.BudgetSubmissions WHERE ParentSubmissionId IS NOT NULL;

-- 5) Remaining submissions.
DELETE FROM core.BudgetSubmissions;

-- 6) HR allocations belonging to RDOF / RHRD employees.
DELETE a
FROM core.HrEmployeeCostAllocations a
INNER JOIN core.HrEmployeeCosts h ON h.EmployeeCostId = a.EmployeeCostId
INNER JOIN core.Entities e ON e.EntityId = h.EntityId
WHERE e.EntityCode IN ('RDOF', 'RHRD');

-- 7) The RDOF / RHRD employee cost rows themselves.
DELETE h
FROM core.HrEmployeeCosts h
INNER JOIN core.Entities e ON e.EntityId = h.EntityId
WHERE e.EntityCode IN ('RDOF', 'RHRD');

-- 8) Verification. Expected after a clean run:
--      HrEmployeeCosts    = 470 rows, 87,363,024 total cost
--      Allocations        = 876 rows, 87,363,024 allocated  (must match cost)
--      BudgetSubmissions  = 0 rows
SELECT 'HrEmployeeCosts' AS Chk, COUNT(*) AS Rows,
       CAST(SUM(AnnualCost) AS decimal(18, 0)) AS TotalCost
FROM core.HrEmployeeCosts;

SELECT 'Allocations' AS Chk, COUNT(*) AS Rows,
       CAST(SUM(AllocatedAmount) AS decimal(18, 0)) AS TotalAllocated
FROM core.HrEmployeeCostAllocations;

SELECT 'BudgetSubmissions' AS Chk, COUNT(*) AS Rows FROM core.BudgetSubmissions;

SELECT 'RemainingByEntity' AS Chk, e.EntityCode, COUNT(*) AS Emps,
       CAST(SUM(h.AnnualCost) AS decimal(18, 0)) AS Cost
FROM core.HrEmployeeCosts h
INNER JOIN core.Entities e ON e.EntityId = h.EntityId
GROUP BY e.EntityCode
ORDER BY Cost DESC;
