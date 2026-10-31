/* ==========================================================================
   GovBudget - ANONYMISE A RESTORED COPY (for a sandbox / trial environment)
   --------------------------------------------------------------------------
   Use when the sandbox should contain the REAL structure (entities,
   programmes, activities, KPI definitions, chart of accounts) but must not
   expose real credentials, personal data, attachments, internal
   correspondence or exact budget figures.

   *** RUN THIS ONLY ON A RESTORED COPY. NEVER ON THE PRODUCTION DATABASE. ***

   The guard in section 0 refuses to run unless the database name looks like a
   non-production copy. Adjust the allowed names if EGA uses a different one.

   WHAT IT DOES
     1  Credentials      - every password hash removed; all accounts disabled
                           except one sandbox administrator with a temporary
                           password that must be changed at first sign-in.
     2  Personal data    - employee names and IDs replaced with sequential
                           placeholders. Occupations are KEPT (needed for the
                           cost-by-occupation analysis) as they are not
                           identifying on their own.
     3  Attachments      - all uploaded documents deleted.
     4  Free text        - internal messages, review narratives, approval and
                           budget notes, maturity notes replaced or cleared.
     5  Audit / tokens   - audit log and password-reset requests emptied.
     6  Amounts (OPTIONAL, section 6) - all monetary values multiplied by a
                           single factor, keeping every internal total
                           consistent.

   WHAT IT DELIBERATELY KEEPS
     Organisation names, programme/activity names and codes, KPI names,
     targets and baselines, and budget line descriptions - because without
     them the sandbox is not a useful functional test. If EGA classifies any
     of these as sensitive, section 7 has an optional block to rename the
     organisation layer as well.

   IMPORTANT: a single scaling factor hides absolute values but preserves
   ratios and relative structure. It is de-identification, not statistical
   anonymisation. If EGA requires figures that cannot be reverse-engineered,
   use docs/Sandbox_SampleData.sql against an empty database instead.
   ========================================================================== */

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

/* ==========================================================================
   0) SAFETY GUARD
   ========================================================================== */
IF DB_NAME() NOT LIKE '%SBX%'
   AND DB_NAME() NOT LIKE '%SANDBOX%'
   AND DB_NAME() NOT LIKE '%TEST%'
   AND DB_NAME() NOT LIKE '%COPY%'
   AND DB_NAME() NOT LIKE '%DEV%'
BEGIN
    RAISERROR (N'REFUSING TO RUN: database "%s" does not look like a sandbox copy. Restore the backup under a name containing SBX, SANDBOX, TEST, COPY or DEV first.', 16, 1, DB_NAME());
    /* Stops every remaining batch in this script from executing. */
    SET NOEXEC ON;
END
GO

PRINT CONCAT('Anonymising database: ', DB_NAME());
GO

BEGIN TRANSACTION;

/* ==========================================================================
   1) CREDENTIALS
      The application hashes any remaining clear-text password at start-up and
      clears the legacy column, so the temporary password below becomes a
      PBKDF2 hash on first run. MustChangePassword forces a new one.
   ========================================================================== */
DECLARE @SandboxAdmin  NVARCHAR(100) = N'sbxadmin';
DECLARE @TempPassword  NVARCHAR(128) = N'Sandbox#Change1';   -- change before handover

/* Disable and de-credential every existing account. */
UPDATE core.AppUsers
   SET IsActive           = 0,
       Password           = NULL,
       PasswordHash       = NULL,
       SecurityStamp      = NULL,
       FailedLoginCount   = 0,
       LockoutEndUtc      = NULL,
       LastLoginAt        = NULL,
       PasswordUpdatedAt  = NULL,
       MustChangePassword = 0;

/* One usable administrator. */
IF NOT EXISTS (SELECT 1 FROM core.AppUsers WHERE UserName = @SandboxAdmin)
    INSERT INTO core.AppUsers (UserName, Password, Role, EntityId, DepartmentId, IsActive, MustChangePassword)
    VALUES (@SandboxAdmin, @TempPassword, N'SYSADMIN', NULL, NULL, 1, 1);
ELSE
    UPDATE core.AppUsers
       SET Password = @TempPassword, PasswordHash = NULL, Role = N'SYSADMIN',
           IsActive = 1, MustChangePassword = 1
     WHERE UserName = @SandboxAdmin;

PRINT '1/6 credentials reset.';

/* ==========================================================================
   2) PERSONAL DATA - HR records
   ========================================================================== */
;WITH Numbered AS (
    SELECT EmployeeCostId,
           ROW_NUMBER() OVER (ORDER BY EmployeeCostId) AS rn
    FROM core.HrEmployeeCosts
)
UPDATE h
   SET h.EmployeeName = CONCAT(N'Employee ', RIGHT(CONCAT(N'0000', n.rn), 4)),
       h.EmployeeId   = CONCAT(N'ANON-', RIGHT(CONCAT(N'0000', n.rn), 4)),
       h.SourceFile   = NULL,
       h.ImportedBy   = N'anonymised'
FROM core.HrEmployeeCosts h
JOIN Numbered n ON n.EmployeeCostId = h.EmployeeCostId;

PRINT '2/6 HR personal data replaced.';

/* ==========================================================================
   3) ATTACHMENTS
   ========================================================================== */
DELETE FROM core.BudgetLineDocuments;

UPDATE core.BudgetSubmissionLines
   SET DocFileName = NULL, DocContentType = NULL, DocSizeBytes = NULL,
       DocContent = NULL, DocUploadedAt = NULL, DocUploadedBy = NULL
 WHERE DocContent IS NOT NULL OR DocFileName IS NOT NULL;

IF OBJECT_ID(N'core.DOF_CombindBudget_Final', N'U') IS NOT NULL
    UPDATE core.DOF_CombindBudget_Final
       SET DocFileName = NULL, DocContentType = NULL, DocSizeBytes = NULL,
           DocContent = NULL, DocUploadedAt = NULL, DocUploadedBy = NULL
     WHERE DocContent IS NOT NULL OR DocFileName IS NOT NULL;

PRINT '3/6 attachments removed.';

/* ==========================================================================
   4) FREE TEXT AND CORRESPONDENCE
   ========================================================================== */
DELETE FROM core.InternalMessages;

UPDATE core.BudgetLines            SET Notes = NULL           WHERE Notes IS NOT NULL;
UPDATE core.BudgetSubmissionLines  SET Notes = NULL           WHERE Notes IS NOT NULL;

UPDATE core.BudgetSubmissions
   SET ApprovalNote    = CASE WHEN ApprovalNote    IS NOT NULL THEN N'(note removed for sandbox)' END,
       ReturnNote      = CASE WHEN ReturnNote      IS NOT NULL THEN N'(note removed for sandbox)' END,
       SysApprovalNote = CASE WHEN SysApprovalNote IS NOT NULL THEN N'(note removed for sandbox)' END;

UPDATE core.BudgetRevisionRequests SET Note  = N'(note removed for sandbox)' WHERE Note  IS NOT NULL;
UPDATE core.MaturityAssessments    SET Notes = N'(note removed for sandbox)' WHERE Notes IS NOT NULL;

UPDATE core.EntityReviewNotes
   SET Body = N'(narrative removed for sandbox - re-enter to test this screen)'
 WHERE Body IS NOT NULL;

UPDATE core.ReviewNarratives
   SET Body           = N'(narrative removed for sandbox - re-enter to test this screen)',
       Owner          = NULL,
       SuccessMeasure = NULL
 WHERE Body IS NOT NULL OR Owner IS NOT NULL;

PRINT '4/6 free text cleared.';

/* ==========================================================================
   5) AUDIT TRAIL AND RESET TOKENS
   ========================================================================== */
DELETE FROM core.PasswordResetRequests;
DELETE FROM core.AuditLogs;

/* Saved report definitions belong to named users; keep the objects, detach the owner. */
UPDATE core.SavedReports SET OwnerUser = @SandboxAdmin WHERE OwnerUser <> @SandboxAdmin;

PRINT '5/6 audit trail and tokens emptied.';

COMMIT TRANSACTION;
GO

/* ==========================================================================
   6) OPTIONAL - SCALE ALL MONETARY VALUES
      Uncomment the block below to multiply every amount by one factor.
      M12 is corrected afterwards so Amount always equals M01..M12, and
      UnitPrice is recomputed from the scaled Amount, so the app's internal
      consistency checks still pass.
   ==========================================================================

DECLARE @Factor DECIMAL(9,4) = 0.8700;   -- e.g. 0.87 = shift every figure by -13%

BEGIN TRANSACTION;

UPDATE core.BudgetLines
   SET Amount    = ROUND(Amount    * @Factor, 2),
       F1_Amount = ROUND(F1_Amount * @Factor, 2),
       F2_Amount = ROUND(F2_Amount * @Factor, 2),
       M01 = ROUND(M01 * @Factor, 2), M02 = ROUND(M02 * @Factor, 2), M03 = ROUND(M03 * @Factor, 2),
       M04 = ROUND(M04 * @Factor, 2), M05 = ROUND(M05 * @Factor, 2), M06 = ROUND(M06 * @Factor, 2),
       M07 = ROUND(M07 * @Factor, 2), M08 = ROUND(M08 * @Factor, 2), M09 = ROUND(M09 * @Factor, 2),
       M10 = ROUND(M10 * @Factor, 2), M11 = ROUND(M11 * @Factor, 2), M12 = ROUND(M12 * @Factor, 2);

-- Push the rounding remainder into M12 and rebuild UnitPrice.
UPDATE core.BudgetLines
   SET M12 = Amount - (M01+M02+M03+M04+M05+M06+M07+M08+M09+M10+M11)
 WHERE Amount <> (M01+M02+M03+M04+M05+M06+M07+M08+M09+M10+M11+M12);

UPDATE core.BudgetLines
   SET UnitPrice = CASE WHEN Quantity <> 0 THEN Amount / Quantity ELSE Amount END;

UPDATE core.BudgetSubmissionLines
   SET Amount    = ROUND(Amount    * @Factor, 2),
       UnitPrice = ROUND(UnitPrice * @Factor, 4),
       F1_Amount = ROUND(F1_Amount * @Factor, 2),
       F2_Amount = ROUND(F2_Amount * @Factor, 2),
       M01 = ROUND(M01 * @Factor, 2), M02 = ROUND(M02 * @Factor, 2), M03 = ROUND(M03 * @Factor, 2),
       M04 = ROUND(M04 * @Factor, 2), M05 = ROUND(M05 * @Factor, 2), M06 = ROUND(M06 * @Factor, 2),
       M07 = ROUND(M07 * @Factor, 2), M08 = ROUND(M08 * @Factor, 2), M09 = ROUND(M09 * @Factor, 2),
       M10 = ROUND(M10 * @Factor, 2), M11 = ROUND(M11 * @Factor, 2), M12 = ROUND(M12 * @Factor, 2);

IF OBJECT_ID(N'core.DOF_CombindBudget_Final', N'U') IS NOT NULL
    UPDATE core.DOF_CombindBudget_Final
       SET Amount    = ROUND(Amount    * @Factor, 2),
           UnitPrice = ROUND(UnitPrice * @Factor, 4),
           F1_Amount = ROUND(F1_Amount * @Factor, 2),
           F2_Amount = ROUND(F2_Amount * @Factor, 2),
           M01 = ROUND(M01 * @Factor, 2), M02 = ROUND(M02 * @Factor, 2), M03 = ROUND(M03 * @Factor, 2),
           M04 = ROUND(M04 * @Factor, 2), M05 = ROUND(M05 * @Factor, 2), M06 = ROUND(M06 * @Factor, 2),
           M07 = ROUND(M07 * @Factor, 2), M08 = ROUND(M08 * @Factor, 2), M09 = ROUND(M09 * @Factor, 2),
           M10 = ROUND(M10 * @Factor, 2), M11 = ROUND(M11 * @Factor, 2), M12 = ROUND(M12 * @Factor, 2);

UPDATE core.HrEmployeeCosts            SET AnnualCost       = ROUND(AnnualCost       * @Factor, 2);
UPDATE core.HrEmployeeCostAllocations  SET AllocatedAmount  = ROUND(AllocatedAmount  * @Factor, 2);
UPDATE core.HistoricalGlActuals        SET Amount           = ROUND(Amount           * @Factor, 2);
UPDATE core.MidYearGlActualForecasts   SET ActualH1Amount   = ROUND(ActualH1Amount   * @Factor, 2),
                                           ForecastH2Amount = ROUND(ForecastH2Amount * @Factor, 2);
UPDATE core.AllocationTransactions     SET Amount           = ROUND(Amount           * @Factor, 2),
                                           BasisValue       = ROUND(BasisValue       * @Factor, 4),
                                           BasisTotal       = ROUND(BasisTotal       * @Factor, 4);

COMMIT TRANSACTION;
PRINT '6/6 monetary values scaled.';

   ========================================================================== */

/* ==========================================================================
   7) OPTIONAL - RENAME THE ORGANISATION LAYER
      Only if EGA classifies ministry / programme names as sensitive. This
      makes the sandbox much harder to relate to real operations, so review
      the trade-off before running it.
   ==========================================================================

BEGIN TRANSACTION;

;WITH E AS (SELECT EntityId, ROW_NUMBER() OVER (ORDER BY EntityId) rn FROM core.Entities)
UPDATE e SET e.EntityName = CONCAT(N'Entity ', E.rn)
FROM core.Entities e JOIN E ON E.EntityId = e.EntityId;

;WITH P AS (SELECT ProgramId, ROW_NUMBER() OVER (PARTITION BY EntityId ORDER BY ProgramId) rn FROM core.Programs)
UPDATE p SET p.ProgramName = CONCAT(N'Programme ', P.rn)
FROM core.Programs p JOIN P ON P.ProgramId = p.ProgramId;

;WITH A AS (SELECT ActivityId, ROW_NUMBER() OVER (PARTITION BY ProgramId ORDER BY ActivityId) rn FROM core.Activities)
UPDATE a SET a.ActivityName = CONCAT(N'Activity ', A.rn)
FROM core.Activities a JOIN A ON A.ActivityId = a.ActivityId;

UPDATE core.BudgetLines           SET Description = N'Budget line (anonymised)';
UPDATE core.BudgetSubmissionLines SET Description = N'Budget line (anonymised)';

COMMIT TRANSACTION;

   ========================================================================== */

/* ==========================================================================
   VERIFICATION - all four counts must be 0, and one active admin must remain
   ========================================================================== */
SELECT N'Password hashes remaining'  AS Check_, COUNT(*) AS Value FROM core.AppUsers WHERE PasswordHash IS NOT NULL
UNION ALL SELECT N'Attachments remaining',      COUNT(*) FROM core.BudgetLineDocuments
UNION ALL SELECT N'Audit rows remaining',       COUNT(*) FROM core.AuditLogs
UNION ALL SELECT N'Internal messages remaining', COUNT(*) FROM core.InternalMessages
UNION ALL SELECT N'Real employee names remaining', COUNT(*) FROM core.HrEmployeeCosts WHERE EmployeeName NOT LIKE N'Employee %'
UNION ALL SELECT N'Active accounts (expect 1)',  COUNT(*) FROM core.AppUsers WHERE IsActive = 1;
GO

PRINT 'Anonymisation complete. Start the application once so the temporary password is hashed, then sign in and change it immediately.';
GO

/* Release the guard from section 0 for the rest of the session. */
SET NOEXEC OFF;
GO
