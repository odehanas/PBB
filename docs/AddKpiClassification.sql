/*
    KPI classification - ADDITIVE column migration
    ----------------------------------------------
    Adds PBB classification + extended definition fields to core.Kpis:
      - KpiType             : Input | Output | Outcome
      - Dimension           : Efficiency | Quality
      - ReadingType         : Cumulative | Rate
      - Priority            : High | Medium | Low
      - KpiCode             : source KPI identifier (e.g. DiM-01.01)
      - CalculationMethod   : how the KPI is computed (free text)
      - Scope               : KPI/programme scope (free text)
      - ProgramOwner        : owner name
      - StrategicTarget2029 : long-horizon strategic target (numeric)

    All columns are NULLable so existing KPI rows remain valid.
    Idempotent - safe to run multiple times. No GO batch separators.

    Run against the target database (select it in the SSMS dropdown):
    -- USE db_ac6910_govbudget;   -- hosted
    -- USE GovBudgetDB;           -- local
*/

IF COL_LENGTH('core.Kpis', 'KpiType') IS NULL
BEGIN
    PRINT 'Adding core.Kpis.KpiType...'
    ALTER TABLE core.Kpis ADD KpiType NVARCHAR(20) NULL;
END
ELSE PRINT 'core.Kpis.KpiType already exists.'

IF COL_LENGTH('core.Kpis', 'Dimension') IS NULL
BEGIN
    PRINT 'Adding core.Kpis.Dimension...'
    ALTER TABLE core.Kpis ADD Dimension NVARCHAR(20) NULL;
END
ELSE PRINT 'core.Kpis.Dimension already exists.'

IF COL_LENGTH('core.Kpis', 'ReadingType') IS NULL
BEGIN
    PRINT 'Adding core.Kpis.ReadingType...'
    ALTER TABLE core.Kpis ADD ReadingType NVARCHAR(20) NULL;
END
ELSE PRINT 'core.Kpis.ReadingType already exists.'

/* ---- Extended KPI definition fields (from the source KPI sheet) ---- */

IF COL_LENGTH('core.Kpis', 'Priority') IS NULL
BEGIN
    PRINT 'Adding core.Kpis.Priority...'
    ALTER TABLE core.Kpis ADD Priority NVARCHAR(20) NULL;
END
ELSE PRINT 'core.Kpis.Priority already exists.'

IF COL_LENGTH('core.Kpis', 'KpiCode') IS NULL
BEGIN
    PRINT 'Adding core.Kpis.KpiCode...'
    ALTER TABLE core.Kpis ADD KpiCode NVARCHAR(50) NULL;
END
ELSE PRINT 'core.Kpis.KpiCode already exists.'

IF COL_LENGTH('core.Kpis', 'CalculationMethod') IS NULL
BEGIN
    PRINT 'Adding core.Kpis.CalculationMethod...'
    ALTER TABLE core.Kpis ADD CalculationMethod NVARCHAR(MAX) NULL;
END
ELSE PRINT 'core.Kpis.CalculationMethod already exists.'

IF COL_LENGTH('core.Kpis', 'Scope') IS NULL
BEGIN
    PRINT 'Adding core.Kpis.Scope...'
    ALTER TABLE core.Kpis ADD Scope NVARCHAR(MAX) NULL;
END
ELSE PRINT 'core.Kpis.Scope already exists.'

IF COL_LENGTH('core.Kpis', 'ProgramOwner') IS NULL
BEGIN
    PRINT 'Adding core.Kpis.ProgramOwner...'
    ALTER TABLE core.Kpis ADD ProgramOwner NVARCHAR(200) NULL;
END
ELSE PRINT 'core.Kpis.ProgramOwner already exists.'

IF COL_LENGTH('core.Kpis', 'StrategicTarget2029') IS NULL
BEGIN
    PRINT 'Adding core.Kpis.StrategicTarget2029...'
    ALTER TABLE core.Kpis ADD StrategicTarget2029 DECIMAL(18, 4) NULL;
END
ELSE PRINT 'core.Kpis.StrategicTarget2029 already exists.'

-- Relative weight used to distribute the linked activity's cost across ALL its KPIs
-- (not only Output KPIs). NULL/zero for every KPI of an activity => equal split.
IF COL_LENGTH('core.Kpis', 'CostWeight') IS NULL
BEGIN
    PRINT 'Adding core.Kpis.CostWeight...'
    ALTER TABLE core.Kpis ADD CostWeight DECIMAL(18, 4) NULL;
END
ELSE PRINT 'core.Kpis.CostWeight already exists.'

PRINT 'AddKpiClassification.sql complete.'
