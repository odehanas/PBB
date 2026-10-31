-- ==========================================================================
-- GovBudget - Employee cost per hour: setup tables
-- --------------------------------------------------------------------------
-- PURPOSE
--   Derive a standard (fully loaded) hourly cost per employee, for costing of
--   services / outputs / KPIs. This is an ADDITIVE feature only.
--
-- IMPACT ON THE RUNNING SYSTEM: NONE
--   * No existing table is altered. No column is added, renamed or dropped.
--   * No existing view, stored procedure or application code path is touched.
--   * The HR import, the activity allocation and the step-down reallocation
--     all continue to read and write exactly what they do today.
--   * These two tables are written to ONLY from the (future) setup screen.
--     Nothing reads them until you choose to surface the rate.
--   * To back the whole feature out: drop the view, drop these two tables.
--     Nothing else refers to them.
--
-- DESIGN
--   Variables, not date arithmetic. A calendar is a handful of numbers, so a
--   finance user can maintain it without a holiday-date table. If you later
--   want exact holiday dates (UAE public holidays shift with the Islamic
--   calendar), add core.WorkCalendarHolidays and swap PublicHolidayDays for a
--   count from it - the view is the only thing that would change.
--
-- HOW TO RUN
--   Paste into the SmarterASP SQL console and run. No GO, no transactions,
--   no PRINT, no EXEC, no block comments. Idempotent - safe to re-run.
--   Run AddHrHourlyRate_View.sql afterwards.
-- ==========================================================================

-- --------------------------------------------------------------------------
-- 1) core.WorkCalendars - the setup / variables table
--
--    One row per (BudgetYear, EntityId). EntityId NULL = the default that
--    applies to every entity without its own row. Entity-specific rows win.
--    Because the unique constraint treats NULLs as equal, SQL Server enforces
--    exactly one default row per year for you.
-- --------------------------------------------------------------------------
IF OBJECT_ID(N'core.WorkCalendars', N'U') IS NULL
BEGIN
    CREATE TABLE core.WorkCalendars
    (
        CalendarId            int IDENTITY(1, 1) NOT NULL,
        BudgetYear            int            NOT NULL,
        EntityId              int            NULL,
        CalendarName          nvarchar(100)  NOT NULL,

        -- Contracted pattern
        HoursPerDay           decimal(5, 2)  NOT NULL CONSTRAINT DF_WorkCal_HoursPerDay   DEFAULT (8.00),
        WorkDaysPerWeek       decimal(4, 2)  NOT NULL CONSTRAINT DF_WorkCal_DaysPerWeek   DEFAULT (5.00),
        WeeksPerYear          decimal(5, 2)  NOT NULL CONSTRAINT DF_WorkCal_WeeksPerYear  DEFAULT (52.00),

        -- Paid time the employee is NOT available to work on activities.
        -- These are PAID, so they are already inside AnnualCost - which is
        -- exactly why they are deducted from the divisor. See the view header.
        PublicHolidayDays     decimal(5, 2)  NOT NULL CONSTRAINT DF_WorkCal_Holidays      DEFAULT (14.00),
        AnnualLeaveDays       decimal(5, 2)  NOT NULL CONSTRAINT DF_WorkCal_Leave         DEFAULT (22.00),
        OtherPaidAbsenceDays  decimal(5, 2)  NOT NULL CONSTRAINT DF_WorkCal_OtherAbsence  DEFAULT (0.00),

        -- Optional haircut for training / admin / meetings. LEAVE AT 100.
        -- Anything below 100 means hours x rate no longer reconciles back to
        -- total salary cost, which breaks the 100% allocation invariant the
        -- system currently guarantees.
        UtilisationPct        decimal(5, 2)  NOT NULL CONSTRAINT DF_WorkCal_Utilisation   DEFAULT (100.00),

        IsActive              bit            NOT NULL CONSTRAINT DF_WorkCal_IsActive      DEFAULT (1),
        CreatedAt             datetime2(7)   NOT NULL CONSTRAINT DF_WorkCal_CreatedAt     DEFAULT (sysutcdatetime()),
        CreatedBy             nvarchar(100)  NULL,
        UpdatedAt             datetime2(7)   NULL,
        UpdatedBy             nvarchar(100)  NULL,

        CONSTRAINT PK_WorkCalendars PRIMARY KEY (CalendarId),
        CONSTRAINT UQ_WorkCalendars_YearEntity UNIQUE (BudgetYear, EntityId),
        CONSTRAINT FK_WorkCalendars_Entity FOREIGN KEY (EntityId)
            REFERENCES core.Entities (EntityId)
    );
END;

-- --------------------------------------------------------------------------
-- 2) core.HrEmployeeHoursOverride - exceptions only
--
--    Only add a row where an employee does NOT follow their entity calendar
--    (shift workers, part-timers, mid-year joiners, vacant posts).
--
--    KEYED ON (BudgetYear, EmployeeId) - the business key - NOT on
--    EmployeeCostId. That is deliberate: if HR data is deleted and re-imported
--    the surrogate EmployeeCostId changes, but EmployeeId does not, so the
--    overrides survive a re-import instead of silently detaching.
-- --------------------------------------------------------------------------
IF OBJECT_ID(N'core.HrEmployeeHoursOverride', N'U') IS NULL
BEGIN
    CREATE TABLE core.HrEmployeeHoursOverride
    (
        OverrideId              int IDENTITY(1, 1) NOT NULL,
        BudgetYear              int            NOT NULL,
        EmployeeId              nvarchar(50)   NOT NULL,

        -- Hours actually available for activity work in the year. This is the
        -- number the standard rate divides by when present.
        ProductiveHoursPerYear  decimal(9, 2)  NULL,

        -- Optional: contracted/paid hours, for the nominal rate. Leave NULL to
        -- keep using the calendar's gross figure.
        ContractedHoursPerYear  decimal(9, 2)  NULL,

        Note                    nvarchar(300)  NULL,
        CreatedAt               datetime2(7)   NOT NULL CONSTRAINT DF_HrHoursOvr_CreatedAt DEFAULT (sysutcdatetime()),
        CreatedBy               nvarchar(100)  NULL,
        UpdatedAt               datetime2(7)   NULL,
        UpdatedBy               nvarchar(100)  NULL,

        CONSTRAINT PK_HrEmployeeHoursOverride PRIMARY KEY (OverrideId),
        CONSTRAINT UQ_HrHoursOverride_YearEmployee UNIQUE (BudgetYear, EmployeeId)
    );
END;

-- --------------------------------------------------------------------------
-- 3) Seed the 2026 default calendar (8 hours x 5 days x 52 weeks).
--    Applies to every entity that has no row of its own.
--      Gross paid hours = 52 x 5 x 8            = 2,080
--      Less holidays    = 14 x 8                =  -112
--      Less leave       = 22 x 8                =  -176
--      Productive hours                         = 1,792
--    Adjust PublicHolidayDays / AnnualLeaveDays to your actual entitlement.
-- --------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM core.WorkCalendars WHERE BudgetYear = 2026 AND EntityId IS NULL)
BEGIN
    INSERT INTO core.WorkCalendars
        (BudgetYear, EntityId, CalendarName, HoursPerDay, WorkDaysPerWeek,
         WeeksPerYear, PublicHolidayDays, AnnualLeaveDays, CreatedBy)
    VALUES
        (2026, NULL, N'Default government office calendar', 8.00, 5.00,
         52.00, 14.00, 22.00, N'setup-script');
END;
