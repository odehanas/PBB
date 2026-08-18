-- Adds an EntrySource marker to core.BudgetLines so the system can tell apart
-- manually-entered lines ("MANUAL") from Excel-uploaded lines ("UPLOAD").
--
-- Behaviour that relies on this column:
--   * Manual data entry saves rows as 'MANUAL'.
--   * Bulk Excel upload saves rows as 'UPLOAD'.
--   * A new upload deletes only the previously-uploaded ('UPLOAD') rows for the
--     same year/entity/department/category. Manual rows (and legacy NULL rows)
--     are kept UNLESS the user explicitly confirms deleting them too.
--
-- Run once against the GovBudget database.

IF NOT EXISTS (
    SELECT 1
    FROM sys.columns
    WHERE object_id = OBJECT_ID(N'core.BudgetLines')
      AND name = N'EntrySource'
)
BEGIN
    ALTER TABLE core.BudgetLines ADD EntrySource varchar(10) NULL;
END
GO

-- OPTIONAL backfill: if you know the current rows were created by an Excel
-- upload and want a future upload to replace them automatically, mark them as
-- 'UPLOAD'. Leaving them NULL keeps them protected (treated as manual/legacy).
--
-- UPDATE core.BudgetLines SET EntrySource = 'UPLOAD' WHERE EntrySource IS NULL;
-- GO
