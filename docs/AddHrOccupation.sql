/* ==========================================================================
   GovBudget - Add Occupation column to core.HrEmployeeCosts
   --------------------------------------------------------------------------
   Adds an optional Occupation (job title) field to the imported HR employee
   cost records. Idempotent: safe to run multiple times. No GO separators, so
   it can be pasted into a web SQL panel and run as a single batch.

   HOW TO RUN
     - Select the GovBudget database (db_ac6910_govbudget) and execute.
   ========================================================================== */

IF NOT EXISTS (
    SELECT 1
    FROM sys.columns
    WHERE object_id = OBJECT_ID(N'core.HrEmployeeCosts')
      AND name = N'Occupation'
)
BEGIN
    ALTER TABLE core.HrEmployeeCosts ADD Occupation nvarchar(150) NULL;
END;
