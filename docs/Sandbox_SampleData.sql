/* ==========================================================================
   GovBudget - SANDBOX SAMPLE DATA (synthetic, safe to share)
   --------------------------------------------------------------------------
   Purpose
     Gives an EGA sandbox database enough realistic structure to exercise every
     major screen without using any real ministry figures: one test entity, two
     departments, three programmes (two mandate + one support, so cost
     reallocation can be demonstrated), six activities, a small chart of
     accounts, budget lines, HR costs with allocations, prior-year actuals,
     mid-year actuals and KPIs with baselines and targets.

   All names, codes and amounts below are INVENTED. Nothing here is derived
   from live data.

   PREREQUISITES - run in this order (see docs/Sandbox_Provisioning.md)
     1. docs/LocalDatabase_FullSchema.sql   (tables, views, categories)
     2. the incremental scripts listed in the provisioning guide
     3. start the application once (creates security + permission tables)
     4. THIS script

   SAFE TO RE-RUN. Every block is guarded, so running it twice inserts nothing
   the second time.

   TO REMOVE the sample data afterwards, see the delete block at the very end
   (commented out on purpose).
   ========================================================================== */

SET NOCOUNT ON;
GO

DECLARE @Year INT = YEAR(SYSUTCDATETIME());   -- current year; change if the trial needs a fixed year
DECLARE @Prev INT = @Year - 1;

PRINT CONCAT('Seeding GovBudget sandbox sample data for budget year ', @Year, '.');

/* ==========================================================================
   1) ENTITY, DEPARTMENTS
   ========================================================================== */
IF NOT EXISTS (SELECT 1 FROM core.Entities WHERE EntityCode = N'SBX')
    INSERT INTO core.Entities (EntityCode, EntityName, IsActive)
    VALUES (N'SBX', N'Sandbox Ministry (Test Data)', 1);

DECLARE @EntityId INT = (SELECT EntityId FROM core.Entities WHERE EntityCode = N'SBX');

IF NOT EXISTS (SELECT 1 FROM core.Departments WHERE EntityId = @EntityId AND DeptCode = N'SBX-D01')
    INSERT INTO core.Departments (EntityId, DeptCode, DeptName, IsActive)
    VALUES (@EntityId, N'SBX-D01', N'Planning & Budget Directorate', 1);

IF NOT EXISTS (SELECT 1 FROM core.Departments WHERE EntityId = @EntityId AND DeptCode = N'SBX-D02')
    INSERT INTO core.Departments (EntityId, DeptCode, DeptName, IsActive)
    VALUES (@EntityId, N'SBX-D02', N'Operations Directorate', 1);

DECLARE @D01 INT = (SELECT DepartmentId FROM core.Departments WHERE EntityId = @EntityId AND DeptCode = N'SBX-D01');
DECLARE @D02 INT = (SELECT DepartmentId FROM core.Departments WHERE EntityId = @EntityId AND DeptCode = N'SBX-D02');

/* ==========================================================================
   2) PROGRAMMES
      Two Mandate programmes deliver the outputs; one Support programme holds
      shared cost so the step-down reallocation engine has something to move.
   ========================================================================== */
IF NOT EXISTS (SELECT 1 FROM core.Programs WHERE EntityId = @EntityId AND ProgramCode = N'SBX-P01')
    INSERT INTO core.Programs (EntityId, ProgramCode, ProgramName, ProgramType, AllocationSequence, IsActive)
    VALUES (@EntityId, N'SBX-P01', N'Heritage Preservation', N'Mandate', NULL, 1);

IF NOT EXISTS (SELECT 1 FROM core.Programs WHERE EntityId = @EntityId AND ProgramCode = N'SBX-P02')
    INSERT INTO core.Programs (EntityId, ProgramCode, ProgramName, ProgramType, AllocationSequence, IsActive)
    VALUES (@EntityId, N'SBX-P02', N'Public Engagement', N'Mandate', NULL, 1);

IF NOT EXISTS (SELECT 1 FROM core.Programs WHERE EntityId = @EntityId AND ProgramCode = N'SBX-P09')
    INSERT INTO core.Programs (EntityId, ProgramCode, ProgramName, ProgramType, AllocationSequence, IsActive)
    VALUES (@EntityId, N'SBX-P09', N'Corporate Support Services', N'Support', 1, 1);

DECLARE @P01 INT = (SELECT ProgramId FROM core.Programs WHERE EntityId = @EntityId AND ProgramCode = N'SBX-P01');
DECLARE @P02 INT = (SELECT ProgramId FROM core.Programs WHERE EntityId = @EntityId AND ProgramCode = N'SBX-P02');
DECLARE @P09 INT = (SELECT ProgramId FROM core.Programs WHERE EntityId = @EntityId AND ProgramCode = N'SBX-P09');

/* ==========================================================================
   3) ACTIVITIES
   ========================================================================== */
DECLARE @Activities TABLE (ProgramId INT, Code NVARCHAR(30), Name NVARCHAR(200), DepartmentId INT);
INSERT INTO @Activities (ProgramId, Code, Name, DepartmentId) VALUES
    (@P01, N'SBX-P01.A01', N'Site Conservation and Maintenance', @D02),
    (@P01, N'SBX-P01.A02', N'Collections Management',            @D02),
    (@P02, N'SBX-P02.A01', N'Exhibitions and Public Events',     @D02),
    (@P02, N'SBX-P02.A02', N'Education and Outreach Programmes', @D02),
    (@P09, N'SBX-P09.A01', N'Finance and Administration',        @D01),
    (@P09, N'SBX-P09.A02', N'Information Technology Services',   @D01);

INSERT INTO core.Activities (ProgramId, DepartmentId, ActivityCode, ActivityName, IsActive)
SELECT a.ProgramId, a.DepartmentId, a.Code, a.Name, 1
FROM @Activities a
WHERE NOT EXISTS (
    SELECT 1 FROM core.Activities x WHERE x.ProgramId = a.ProgramId AND x.ActivityCode = a.Code);

/* ==========================================================================
   4) CHART OF ACCOUNTS (GL + budget items)
   ========================================================================== */
DECLARE @Gls TABLE (GLCode NVARCHAR(30), GLName NVARCHAR(200), GLType NVARCHAR(20));
INSERT INTO @Gls (GLCode, GLName, GLType) VALUES
    (N'SBX-410100', N'Fees and Charges',        N'REVENUE'),
    (N'SBX-510100', N'Salaries and Wages',      N'HR'),
    (N'SBX-520100', N'Utilities',               N'OPEX'),
    (N'SBX-520200', N'Contracted Services',     N'OPEX'),
    (N'SBX-520300', N'Supplies and Materials',  N'OPEX'),
    (N'SBX-610100', N'Equipment and Machinery', N'CAPEX');

INSERT INTO core.GLAccounts (GLCode, GLName, GLType)
SELECT g.GLCode, g.GLName, g.GLType
FROM @Gls g
WHERE NOT EXISTS (SELECT 1 FROM core.GLAccounts x WHERE x.GLCode = g.GLCode);

DECLARE @Items TABLE (ItemCode NVARCHAR(30), ItemName NVARCHAR(200), GLCode NVARCHAR(30));
INSERT INTO @Items (ItemCode, ItemName, GLCode) VALUES
    (N'SBX-I-REV01', N'Entry tickets and services',      N'SBX-410100'),
    (N'SBX-I-HR01',  N'Staff salaries',                  N'SBX-510100'),
    (N'SBX-I-UT01',  N'Electricity and water',           N'SBX-520100'),
    (N'SBX-I-SV01',  N'Specialist conservation services', N'SBX-520200'),
    (N'SBX-I-SV02',  N'Security and cleaning services',  N'SBX-520200'),
    (N'SBX-I-SP01',  N'Consumables and materials',       N'SBX-520300'),
    (N'SBX-I-EQ01',  N'Laboratory and display equipment', N'SBX-610100');

INSERT INTO core.Items (ItemCode, ItemName, GLAccountId, IsActive)
SELECT i.ItemCode, i.ItemName, gl.GLAccountId, 1
FROM @Items i
JOIN core.GLAccounts gl ON gl.GLCode = i.GLCode
WHERE NOT EXISTS (SELECT 1 FROM core.Items x WHERE x.ItemCode = i.ItemCode);

/* ==========================================================================
   5) BUDGET LINES for @Year
      Amounts are spread equally across the 12 months, with the rounding
      remainder pushed into M12 so the monthly total always equals Amount.
   ========================================================================== */
DECLARE @Lines TABLE (
    DeptCode     NVARCHAR(20),
    CategoryCode NVARCHAR(10),
    ItemCode     NVARCHAR(30),
    ActivityCode NVARCHAR(30),
    Amount       DECIMAL(18,2),
    Descr        NVARCHAR(300)
);
INSERT INTO @Lines (DeptCode, CategoryCode, ItemCode, ActivityCode, Amount, Descr) VALUES
    -- Mandate programme: Heritage Preservation
    (N'SBX-D02', N'OPEX',    N'SBX-I-SV01', N'SBX-P01.A01', 480000.00, N'Conservation works at heritage sites'),
    (N'SBX-D02', N'OPEX',    N'SBX-I-SP01', N'SBX-P01.A01',  95000.00, N'Conservation materials and consumables'),
    (N'SBX-D02', N'OPEX',    N'SBX-I-UT01', N'SBX-P01.A01',  60000.00, N'Site utilities'),
    (N'SBX-D02', N'OPEX',    N'SBX-I-SP01', N'SBX-P01.A02', 120000.00, N'Collections storage and documentation'),
    (N'SBX-D02', N'CAPEX',   N'SBX-I-EQ01', N'SBX-P01.A02', 250000.00, N'Conservation laboratory equipment'),
    -- Mandate programme: Public Engagement
    (N'SBX-D02', N'OPEX',    N'SBX-I-SV02', N'SBX-P02.A01', 310000.00, N'Exhibition setup, security and cleaning'),
    (N'SBX-D02', N'OPEX',    N'SBX-I-SP01', N'SBX-P02.A01',  75000.00, N'Exhibition print and display materials'),
    (N'SBX-D02', N'OPEX',    N'SBX-I-SV02', N'SBX-P02.A02', 140000.00, N'School programme delivery services'),
    -- Support programme: shared cost, to be reallocated to the mandate programmes
    (N'SBX-D01', N'OPEX',    N'SBX-I-SV02', N'SBX-P09.A01', 180000.00, N'Corporate administration services'),
    (N'SBX-D01', N'OPEX',    N'SBX-I-UT01', N'SBX-P09.A01',  45000.00, N'Head office utilities'),
    (N'SBX-D01', N'OPEX',    N'SBX-I-SV02', N'SBX-P09.A02', 220000.00, N'IT support and licences'),
    (N'SBX-D01', N'CAPEX',   N'SBX-I-EQ01', N'SBX-P09.A02', 130000.00, N'Server and network refresh'),
    -- Revenue
    (N'SBX-D01', N'REVENUE', N'SBX-I-REV01', NULL,          420000.00, N'Entry tickets and service fees');

INSERT INTO core.BudgetLines (
    BudgetYear, EntityId, DepartmentId, CategoryId, ItemId, ProgramId, ActivityId,
    Quantity, UnitPrice, Amount, DistributionMode,
    M01, M02, M03, M04, M05, M06, M07, M08, M09, M10, M11, M12,
    F1_Percent, F1_Amount, F2_Percent, F2_Amount,
    Dep_Method, Dep_LifeMonths, Dep_StartMonth,
    Notes, CreatedBy, EntrySource, Description)
SELECT
    @Year, @EntityId, d.DepartmentId, c.CategoryId, it.ItemId, act.ProgramId, act.ActivityId,
    1, l.Amount, l.Amount, N'EQUAL',
    m.Monthly, m.Monthly, m.Monthly, m.Monthly, m.Monthly, m.Monthly,
    m.Monthly, m.Monthly, m.Monthly, m.Monthly, m.Monthly,
    l.Amount - (m.Monthly * 11),
    0, 0, 0, 0,
    N'STRAIGHT', CASE WHEN c.CategoryCode = N'CAPEX' THEN 60 ELSE 0 END, 1,
    N'Sandbox sample data', N'sandbox-seed', N'MANUAL', l.Descr
FROM @Lines l
JOIN core.Departments d ON d.EntityId = @EntityId AND d.DeptCode = l.DeptCode
JOIN core.Categories  c ON c.CategoryCode = l.CategoryCode
JOIN core.Items      it ON it.ItemCode = l.ItemCode
LEFT JOIN core.Activities act ON act.ActivityCode = l.ActivityCode
CROSS APPLY (SELECT CAST(ROUND(l.Amount / 12.0, 2) AS DECIMAL(18,2)) AS Monthly) m
WHERE NOT EXISTS (
    SELECT 1 FROM core.BudgetLines b
    WHERE b.BudgetYear = @Year AND b.EntityId = @EntityId
      AND b.Description = l.Descr);

/* ==========================================================================
   6) HR COSTS + ALLOCATION TO ACTIVITIES
      Employee names are fictional. Each employee's annual cost is split over
      one or two activities so "Cost per KPI" and the HR views have data.
   ========================================================================== */
DECLARE @Hr TABLE (EmployeeId NVARCHAR(50), Name NVARCHAR(200), Occupation NVARCHAR(150),
                   DeptCode NVARCHAR(20), AnnualCost DECIMAL(18,2));
INSERT INTO @Hr (EmployeeId, Name, Occupation, DeptCode, AnnualCost) VALUES
    (N'SBX-E001', N'Test Employee One',   N'Conservator',          N'SBX-D02', 42000.00),
    (N'SBX-E002', N'Test Employee Two',   N'Curator',              N'SBX-D02', 38000.00),
    (N'SBX-E003', N'Test Employee Three', N'Education Officer',    N'SBX-D02', 30000.00),
    (N'SBX-E004', N'Test Employee Four',  N'Financial Analyst',    N'SBX-D01', 34000.00),
    (N'SBX-E005', N'Test Employee Five',  N'Systems Administrator', N'SBX-D01', 36000.00);

INSERT INTO core.HrEmployeeCosts (BudgetYear, EmployeeId, EmployeeName, Occupation, GLCode, GLKind,
                                  EntityId, EntityName, DepartmentId, DepartmentName, AnnualCost,
                                  ImportedBy, SourceFile)
SELECT @Year, h.EmployeeId, h.Name, h.Occupation, N'SBX-510100', N'HR',
       @EntityId, e.EntityName, d.DepartmentId, d.DeptName, h.AnnualCost,
       N'sandbox-seed', N'Sandbox_SampleData.sql'
FROM @Hr h
JOIN core.Entities    e ON e.EntityId = @EntityId
JOIN core.Departments d ON d.EntityId = @EntityId AND d.DeptCode = h.DeptCode
WHERE NOT EXISTS (
    SELECT 1 FROM core.HrEmployeeCosts x
    WHERE x.BudgetYear = @Year AND x.EmployeeId = h.EmployeeId);

DECLARE @HrAlloc TABLE (EmployeeId NVARCHAR(50), ActivityCode NVARCHAR(30), Amount DECIMAL(18,2));
INSERT INTO @HrAlloc (EmployeeId, ActivityCode, Amount) VALUES
    (N'SBX-E001', N'SBX-P01.A01', 42000.00),
    (N'SBX-E002', N'SBX-P01.A02', 26000.00),
    (N'SBX-E002', N'SBX-P02.A01', 12000.00),
    (N'SBX-E003', N'SBX-P02.A02', 30000.00),
    (N'SBX-E004', N'SBX-P09.A01', 34000.00),
    (N'SBX-E005', N'SBX-P09.A02', 36000.00);

INSERT INTO core.HrEmployeeCostAllocations (EmployeeCostId, ActivityId, AllocatedAmount, CreatedBy)
SELECT emp.EmployeeCostId, act.ActivityId, a.Amount, N'sandbox-seed'
FROM @HrAlloc a
JOIN core.HrEmployeeCosts emp ON emp.BudgetYear = @Year AND emp.EmployeeId = a.EmployeeId
JOIN core.Activities      act ON act.ActivityCode = a.ActivityCode
WHERE NOT EXISTS (
    SELECT 1 FROM core.HrEmployeeCostAllocations x
    WHERE x.EmployeeCostId = emp.EmployeeCostId AND x.ActivityId = act.ActivityId);

/* ==========================================================================
   7) PRIOR-YEAR ACTUALS + MID-YEAR ACTUALS / FORECAST
      Drives Budget vs Actual, the derived activity actuals and Mid-Year Forecast.
   ========================================================================== */
DECLARE @Hist TABLE (DeptCode NVARCHAR(20), GLCode NVARCHAR(30), GLType NVARCHAR(20), Amount DECIMAL(18,2));
INSERT INTO @Hist (DeptCode, GLCode, GLType, Amount) VALUES
    (N'SBX-D02', N'SBX-520200', N'OPEX',    790000.00),
    (N'SBX-D02', N'SBX-520300', N'OPEX',    268000.00),
    (N'SBX-D02', N'SBX-520100', N'OPEX',     57000.00),
    (N'SBX-D01', N'SBX-520200', N'OPEX',    385000.00),
    (N'SBX-D01', N'SBX-520100', N'OPEX',     43000.00),
    (N'SBX-D01', N'SBX-410100', N'REVENUE', 402000.00);

INSERT INTO core.HistoricalGlActuals (BudgetYear, EntityId, DepartmentId, GLCode, GLType, Amount, CreatedBy, SourceFile)
SELECT @Prev, @EntityId, d.DepartmentId, h.GLCode, h.GLType, h.Amount, N'sandbox-seed', N'Sandbox_SampleData.sql'
FROM @Hist h
JOIN core.Departments d ON d.EntityId = @EntityId AND d.DeptCode = h.DeptCode
WHERE NOT EXISTS (
    SELECT 1 FROM core.HistoricalGlActuals x
    WHERE x.BudgetYear = @Prev AND x.EntityId = @EntityId
      AND x.DepartmentId = d.DepartmentId AND x.GLCode = h.GLCode);

/* Mid-year: roughly 45% of the annual budget spent in H1, with an H2 forecast. */
DECLARE @Mid TABLE (GLCode NVARCHAR(30), GLType NVARCHAR(20), H1 DECIMAL(18,2), H2 DECIMAL(18,2));
INSERT INTO @Mid (GLCode, GLType, H1, H2) VALUES
    (N'SBX-520100', N'OPEX',     47000.00,  58000.00),
    (N'SBX-520200', N'OPEX',    382000.00, 448000.00),
    (N'SBX-520300', N'OPEX',    131000.00, 159000.00),
    (N'SBX-610100', N'CAPEX',   140000.00, 240000.00),
    (N'SBX-410100', N'REVENUE', 191000.00, 229000.00);

INSERT INTO core.MidYearGlActualForecasts (BudgetYear, EntityId, GLCode, GLType,
                                           ActualH1Amount, ForecastH2Amount, CreatedBy, SourceFile)
SELECT @Year, @EntityId, m.GLCode, m.GLType, m.H1, m.H2, N'sandbox-seed', N'Sandbox_SampleData.sql'
FROM @Mid m
WHERE NOT EXISTS (
    SELECT 1 FROM core.MidYearGlActualForecasts x
    WHERE x.BudgetYear = @Year AND x.EntityId = @EntityId AND x.GLCode = m.GLCode);

/* ==========================================================================
   8) PERFORMANCE LAYER: activity outputs + KPIs
      Deliberately mixed: UP and DOWN direction, on-track and behind, one
      programme-level KPI, so the KPI <-> Cost Linkage and scorecard screens
      show every state.
   ========================================================================== */
DECLARE @Outputs TABLE (ActivityCode NVARCHAR(30), Measure NVARCHAR(200), Volume DECIMAL(18,4));
INSERT INTO @Outputs (ActivityCode, Measure, Volume) VALUES
    (N'SBX-P01.A01', N'Sites conserved',            12),
    (N'SBX-P01.A02', N'Objects catalogued',       4500),
    (N'SBX-P02.A01', N'Exhibitions delivered',       8),
    (N'SBX-P02.A02', N'Students reached',        16000),
    (N'SBX-P09.A01', N'Transactions processed',  22000),
    (N'SBX-P09.A02', N'Service tickets closed',   3100);

INSERT INTO core.ActivityOutputs (ActivityId, BudgetYear, OutputMeasure, OutputVolume, IsPrimary, CreatedBy)
SELECT act.ActivityId, @Year, o.Measure, o.Volume, 1, N'sandbox-seed'
FROM @Outputs o
JOIN core.Activities act ON act.ActivityCode = o.ActivityCode
WHERE NOT EXISTS (
    SELECT 1 FROM core.ActivityOutputs x
    WHERE x.ActivityId = act.ActivityId AND x.BudgetYear = @Year AND x.OutputMeasure = o.Measure);

DECLARE @Kpi TABLE (
    Name NVARCHAR(300), Unit NVARCHAR(50), Direction NVARCHAR(10),
    Baseline DECIMAL(18,4), Target DECIMAL(18,4), Actual DECIMAL(18,4),
    ActivityCode NVARCHAR(30), ProgramCode NVARCHAR(30));
INSERT INTO @Kpi (Name, Unit, Direction, Baseline, Target, Actual, ActivityCode, ProgramCode) VALUES
    (N'Heritage sites in good condition',        N'%',       N'UP',   62.00,  75.00,  68.00, N'SBX-P01.A01', NULL),
    (N'Collection items digitally catalogued',   N'items',   N'UP',  3200.00, 5000.00, 4100.00, N'SBX-P01.A02', NULL),
    (N'Average conservation backlog',            N'months',  N'DOWN',  14.00,   8.00,  11.00, N'SBX-P01.A01', NULL),
    (N'Annual visitors',                         N'persons', N'UP', 145000.00, 180000.00, 121000.00, N'SBX-P02.A01', NULL),
    (N'Student participation in programmes',     N'students', N'UP', 12000.00, 16000.00, 15200.00, N'SBX-P02.A02', NULL),
    (N'Public satisfaction with services',       N'%',       N'UP',   71.00,  80.00,  78.00, NULL,           N'SBX-P02'),
    (N'Average IT incident resolution time',     N'hours',   N'DOWN',  9.50,   6.00,   7.20, N'SBX-P09.A02', NULL);

INSERT INTO core.Kpis (BudgetYear, Period, EntityId, ProgramId, ActivityId, KpiName, Unit,
                       Direction, Baseline, Target, ActualValue, CreatedBy)
SELECT @Year, N'MidYear', @EntityId, p.ProgramId, act.ActivityId, k.Name, k.Unit,
       k.Direction, k.Baseline, k.Target, k.Actual, N'sandbox-seed'
FROM @Kpi k
LEFT JOIN core.Activities act ON act.ActivityCode = k.ActivityCode
LEFT JOIN core.Programs   p   ON p.EntityId = @EntityId AND p.ProgramCode = k.ProgramCode
WHERE NOT EXISTS (
    SELECT 1 FROM core.Kpis x
    WHERE x.BudgetYear = @Year AND x.EntityId = @EntityId AND x.KpiName = k.Name);

/* ==========================================================================
   9) COST REALLOCATION: one driver and one step-down rule
      Left as Draft on purpose - a tester should press "Run" in the
      Allocation screen, which is the behaviour worth demonstrating.
   ========================================================================== */
IF NOT EXISTS (SELECT 1 FROM core.AllocationDrivers WHERE DriverCode = N'HEADCOUNT')
    INSERT INTO core.AllocationDrivers (DriverCode, DriverName, Unit, IsActive)
    VALUES (N'HEADCOUNT', N'Headcount', N'employees', 1);

DECLARE @DriverId INT = (SELECT DriverId FROM core.AllocationDrivers WHERE DriverCode = N'HEADCOUNT');

INSERT INTO core.AllocationDriverValues (DriverId, BudgetYear, TargetProgramId, TargetActivityId, Value)
SELECT @DriverId, @Year, v.ProgramId, NULL, v.Val
FROM (VALUES (@P01, CAST(3 AS DECIMAL(18,4))), (@P02, CAST(2 AS DECIMAL(18,4)))) AS v(ProgramId, Val)
WHERE NOT EXISTS (
    SELECT 1 FROM core.AllocationDriverValues x
    WHERE x.DriverId = @DriverId AND x.BudgetYear = @Year
      AND x.TargetProgramId = v.ProgramId AND x.TargetActivityId IS NULL);

IF NOT EXISTS (
    SELECT 1 FROM core.AllocationRules
    WHERE BudgetYear = @Year AND EntityId = @EntityId AND SourceProgramId = @P09)
BEGIN
    INSERT INTO core.AllocationRules (BudgetYear, EntityId, SourceProgramId, SourceActivityId, Method,
                                      DriverId, CategoryScopeCsv, TargetScope, SourcePercent, Sequence,
                                      IsActive, CreatedBy)
    VALUES (@Year, @EntityId, @P09, NULL, N'Driver', @DriverId, N'OPEX,HR', N'AllMandate', 100, 100, 1, N'sandbox-seed');
END

PRINT 'Sandbox sample data seeded.';
GO

/* ==========================================================================
   VERIFY
   ========================================================================== */
SELECT N'Entities'    AS TableName, COUNT(*) AS [RowCount] FROM core.Entities   WHERE EntityCode = N'SBX'
UNION ALL SELECT N'Departments', COUNT(*) FROM core.Departments d JOIN core.Entities e ON e.EntityId = d.EntityId WHERE e.EntityCode = N'SBX'
UNION ALL SELECT N'Programmes',  COUNT(*) FROM core.Programs    p JOIN core.Entities e ON e.EntityId = p.EntityId WHERE e.EntityCode = N'SBX'
UNION ALL SELECT N'Activities',  COUNT(*) FROM core.Activities WHERE ActivityCode LIKE N'SBX-%'
UNION ALL SELECT N'BudgetLines', COUNT(*) FROM core.BudgetLines b JOIN core.Entities e ON e.EntityId = b.EntityId WHERE e.EntityCode = N'SBX'
UNION ALL SELECT N'HR staff',    COUNT(*) FROM core.HrEmployeeCosts WHERE EmployeeId LIKE N'SBX-%'
UNION ALL SELECT N'KPIs',        COUNT(*) FROM core.Kpis k JOIN core.Entities e ON e.EntityId = k.EntityId WHERE e.EntityCode = N'SBX';
GO

/* ==========================================================================
   REMOVING THE SAMPLE DATA  (uncomment to run - deletes in FK-safe order)
   --------------------------------------------------------------------------
DECLARE @E INT = (SELECT EntityId FROM core.Entities WHERE EntityCode = N'SBX');

DELETE FROM core.AllocationRuleTargets
WHERE RuleId IN (SELECT RuleId FROM core.AllocationRules WHERE EntityId = @E);
DELETE FROM core.AllocationTransactions
WHERE EntityId = @E;
DELETE FROM core.AllocationRuns          WHERE EntityId = @E;
DELETE FROM core.AllocationRules         WHERE EntityId = @E;
DELETE FROM core.AllocationDriverValues
WHERE TargetProgramId IN (SELECT ProgramId FROM core.Programs WHERE EntityId = @E);
DELETE FROM core.HrEmployeeCostAllocations
WHERE EmployeeCostId IN (SELECT EmployeeCostId FROM core.HrEmployeeCosts WHERE EntityId = @E);
DELETE FROM core.HrEmployeeCosts         WHERE EntityId = @E;
DELETE FROM core.ActivityOutputs
WHERE ActivityId IN (SELECT a.ActivityId FROM core.Activities a
                     JOIN core.Programs p ON p.ProgramId = a.ProgramId WHERE p.EntityId = @E);
DELETE FROM core.KpiCostLinks
WHERE KpiId IN (SELECT KpiId FROM core.Kpis WHERE EntityId = @E);
DELETE FROM core.Kpis                    WHERE EntityId = @E;
DELETE FROM core.MidYearGlActualForecasts WHERE EntityId = @E;
DELETE FROM core.HistoricalGlActuals     WHERE EntityId = @E;
DELETE FROM core.BudgetLineDocuments
WHERE BudgetLineId IN (SELECT BudgetLineId FROM core.BudgetLines WHERE EntityId = @E);
DELETE FROM core.BudgetLines             WHERE EntityId = @E;
DELETE FROM core.Activities
WHERE ProgramId IN (SELECT ProgramId FROM core.Programs WHERE EntityId = @E);
DELETE FROM core.Programs                WHERE EntityId = @E;
DELETE FROM core.Departments             WHERE EntityId = @E;
DELETE FROM core.Entities                WHERE EntityId = @E;
DELETE FROM core.Items      WHERE ItemCode LIKE N'SBX-%';
DELETE FROM core.GLAccounts WHERE GLCode   LIKE N'SBX-%';
   ========================================================================== */
